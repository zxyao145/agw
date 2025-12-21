using DSystem.Domain.Entities;
using DSystem.Domain.Enums;
using DSystem.Domain.Models;
using DSystem.Domain.Repositories;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;
using System.Threading;
using Workflow = DSystem.Domain.Entities.Workflow;

namespace DSystem.Domain.Services;

public interface IWorkflowAgentExecutor
{
    Task<string> ExecuteAsync(AiAgent agent, string input, CancellationToken cancellationToken = default);
}

/// <summary>
/// Placeholder executor so workflow orchestration can run end-to-end without a real LLM/Agent Framework integration yet.
/// </summary>
public class PlaceholderWorkflowAgentExecutor : IWorkflowAgentExecutor
{
    public Task<string> ExecuteAsync(AiAgent agent, string input, CancellationToken cancellationToken = default)
    {
        var output = $"[placeholder] agent={agent.Name}; input={input}";
        return Task.FromResult(output);
    }
}
public record WaChatMessage(string AuthorName, string Role, string Content);

public record WorkflowExecutionAgentResult(Guid AgentId, string AgentName, int Order, string Output);

public record WorkflowExecutionResult(
    Guid WorkflowId,
    WorkflowOrchestrationPattern Pattern,
    bool NotImplemented,
    string? Message,
    string Input,
    string? FinalOutput,
    IReadOnlyList<WaChatMessage> Outputs);

public class WorkflowRuntimeService
{
    private readonly IRepository<Workflow> _workflowRepository;
    private readonly IRepository<WorkflowNode> _workflowNodeRepository;
    private readonly IRepository<WorkflowEdge> _workflowEdgeRepository;
    private readonly AgentRuntimeService _agentRuntimeService;
    private readonly IWorkflowAgentExecutor _executor;

    public WorkflowRuntimeService(
        IRepository<Workflow> workflowRepository,
        IRepository<WorkflowNode> workflowAgentRepository,
        IRepository<WorkflowEdge> workflowEdgeRepository,
        AgentRuntimeService agentRuntimeService,
        IWorkflowAgentExecutor executor)
    {
        _workflowRepository = workflowRepository;
        _workflowNodeRepository = workflowAgentRepository;
        _agentRuntimeService = agentRuntimeService;
        _executor = executor;
        _workflowEdgeRepository = workflowEdgeRepository;
    }

    public async Task<WorkflowExecutionResult?> ExecuteAsync(
        Guid workflowId,
        string input,
        CancellationToken cancellationToken = default)
    {
        var workflow = await _workflowRepository.GetByIdAsync(workflowId);
        if (workflow == null || !workflow.Enable)
        {
            return null;
        }

        var aiWorkflow = await CreateAiFlow(workflow, cancellationToken);
        if (aiWorkflow == null)
        {
            return null;
        }

        var messages = new List<ChatMessage> { new(ChatRole.User, input) };

        StreamingRun run = await InProcessExecution.StreamAsync(aiWorkflow, messages);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        List<ChatMessage> result = new();
        await foreach (WorkflowEvent evt in run.WatchStreamAsync().ConfigureAwait(false))
        {
            if (evt is AgentRunUpdateEvent e)
            {
                Console.WriteLine($"{e.ExecutorId}: {e.Data}");
            }
            else if (evt is WorkflowOutputEvent outputEvt)
            {
                result = (List<ChatMessage>)outputEvt.Data!;
                break;
            }
        }

        var outputs = new List<WaChatMessage>();

        // Display aggregated results from all agents
        Console.WriteLine("===== Final Aggregated Results =====");
        foreach (var message in result)
        {
            var chatMsg =
                new WaChatMessage(message.AuthorName ?? "", message.Role.ToString(), message.Text);
            outputs.Add(chatMsg);
        }

        return new WorkflowExecutionResult(
            workflow.Id,
            workflow.Pattern,
            NotImplemented: false,
            Message: null,
            Input: input,
            FinalOutput: null,
            Outputs: outputs);
    }


    public async Task<Microsoft.Agents.AI.Workflows.Workflow?>
        CreateAiFlow(Guid workflowId, CancellationToken cancellationToken)
    {
        var workflow = await _workflowRepository.GetByIdAsync(workflowId);
        if (workflow == null || !workflow.Enable)
        {
            return null;
        }
        return await CreateAiFlow(workflow, cancellationToken);
    }

    private async Task<Microsoft.Agents.AI.Workflows.Workflow?>
        CreateAiFlow(Workflow workflow, CancellationToken cancellationToken)
    {
        Guid workflowId = workflow.Id;

        var workflowNodes = await _workflowNodeRepository
            .ListAsync(x => x.WorkflowId == workflowId);

        var workflowEdges = await _workflowNodeRepository
            .ListAsync(x => x.WorkflowId == workflowId);

        List<AIAgent> aiAgents = new();
        foreach (var node in workflowNodes)
        {
            AIAgent? aiAgent;
            if (node.Type == WorkflowNodeType.AgentNode)
            {
                aiAgent = await _agentRuntimeService.CreateAiAgentAsync(node.RelateId);

            }
            else
            {
                var flowNode = await this.CreateAiFlow(node.RelateId, cancellationToken);
                aiAgent = flowNode?.AsAgent() ?? null;
            }
            if (aiAgent == null)
            {
                return null;
            }
            aiAgents.Add(aiAgent);
        }

        // 根据 workflowEdges 构建 aiAgents 之间的连接关系

        Microsoft.Agents.AI.Workflows.Workflow? aiFlow;
        switch (workflow.Pattern)
        {
            case WorkflowOrchestrationPattern.Concurrent:
                aiFlow = AgentWorkflowBuilder.BuildConcurrent(aiAgents);
                break;
            case WorkflowOrchestrationPattern.Sequential:
                aiFlow = AgentWorkflowBuilder.BuildSequential(aiAgents);
                break;
            case WorkflowOrchestrationPattern.GroupChat:
                aiFlow = AgentWorkflowBuilder.CreateGroupChatBuilderWith(
                     agents => new RoundRobinGroupChatManager(agents)
                     {
                         MaximumIterationCount = 5
                     })
                     .AddParticipants(aiAgents.ToArray())
                     .Build();
                break;
            case WorkflowOrchestrationPattern.Handoff:
                aiFlow = null;
                //aiFlow = AgentWorkflowBuilder.StartHandoffWith(triageAgent)
                //    .WithHandoffs(triageAgent, [mathTutor, historyTutor]) // Triage can route to either specialist
                //    .WithHandoff(mathTutor, triageAgent)                  // Math tutor can return to triage
                //    .WithHandoff(historyTutor, triageAgent)               // History tutor can return to triage
                //    .Build();
                break;
            case WorkflowOrchestrationPattern.Magentic:
                aiFlow = null;
                //int maxRounds = 10;
                //int maxStallCount = 3;
                //aiFlow = AgentWorkflowBuilder.CreateGroupChatBuilderWith(
                //    allAgents => new MagenticOrchestrationManager(
                //        allAgents,
                //        maxRounds: maxRounds,
                //        maxStallCount: maxStallCount,
                //        maxResetCount: 2
                //    ))
                //    .AddParticipants([orchestrator, .. workers])
                //    .Build();
                break;
            default:
                aiFlow = null;
                break;
        }

        return aiFlow;
    }
}