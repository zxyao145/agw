using DSystem.Domain.Entities;
using DSystem.Domain.Enums;
using DSystem.Domain.Models;
using DSystem.Domain.Repositories;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;

namespace DSystem.Domain.Services;

public interface IAgentflowAgentExecutor
{
    Task<string> ExecuteAsync(AiAgent agent, string input, CancellationToken cancellationToken = default);
}

/// <summary>
/// Placeholder executor so workflow orchestration can run end-to-end without a real LLM/Agent Framework integration yet.
/// </summary>
public class PlaceholderAgentflowAgentExecutor : IAgentflowAgentExecutor
{
    public Task<string> ExecuteAsync(AiAgent agent, string input, CancellationToken cancellationToken = default)
    {
        var output = $"[placeholder] agent={agent.Name}; input={input}";
        return Task.FromResult(output);
    }
}

public record AgentflowExecutionAgentResult(Guid AgentId, string AgentName, int Order, string Output);

public record AgentflowExecutionResult(string ThreadId, IReadOnlyList<AiMessage> Messages);

public class AgentflowRuntimeService
{
    private readonly IRepository<Agentflow> _agentflowRepository;
    private readonly IRepository<AgentflowNode> _agentflowNodeRepository;
    private readonly IRepository<AgentflowEdge> _agentflowEdgeRepository;
    private readonly AgentRuntimeService _agentRuntimeService;
    private readonly IAgentflowAgentExecutor _executor;

    public AgentflowRuntimeService(
        IRepository<Agentflow> agentflowRepository,
        IRepository<AgentflowNode> agentflowAgentRepository,
        IRepository<AgentflowEdge> agentflowEdgeRepository,
        AgentRuntimeService agentRuntimeService,
        IAgentflowAgentExecutor executor)
    {
        _agentflowRepository = agentflowRepository;
        _agentflowNodeRepository = agentflowAgentRepository;
        _agentRuntimeService = agentRuntimeService;
        _executor = executor;
        _agentflowEdgeRepository = agentflowEdgeRepository;
    }

    public async IAsyncEnumerable<AiMessage> ExecuteStreamingAsync(
        Guid agentflowId,
        string input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var agentflow = await _agentflowRepository.GetByIdAsync(agentflowId);
        if (agentflow == null || !agentflow.Enable)
        {
            yield break;
        }

        var workflow = await CreateAiWorkflow(agentflow, cancellationToken);
        if (workflow == null)
        {
            yield break;
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, input)
        };

        StreamingRun run = await InProcessExecution.StreamAsync(workflow, messages);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        await foreach (WorkflowEvent evt in run.WatchStreamAsync().ConfigureAwait(false))
        {
            //if (evt is AgentRunUpdateEvent e)
            //{
            //    // Stream intermediate updates
            //    var message = new AiMessage(
            //        Guid.NewGuid().ToString(),
            //        e.ExecutorId,
            //        "assistant",
            //        e.Data?.ToString() ?? ""
            //    );
            //    yield return message;
            //}
            //else 
            if (evt is WorkflowOutputEvent outputEvt)
            {
                // Stream final results
                var result = (List<ChatMessage>)outputEvt.Data!;
                foreach (var msg in result)
                {
                    var chatMsg = new AiMessage(
                        msg.MessageId,
                        msg.AuthorName,
                        msg.Role.Value,
                        AiMessageType.Text,
                        msg.Text
                    );
                    yield return chatMsg;
                }
                yield break;
            }
        }
    }

    public async Task<AgentflowExecutionResult?> ExecuteAsync(
        Guid agentflowId,
        string input,
        CancellationToken cancellationToken = default)
    {
        var agentflow = await _agentflowRepository.GetByIdAsync(agentflowId);
        if (agentflow == null || !agentflow.Enable)
        {
            return null;
        }

        var workflow = await CreateAiWorkflow(agentflow, cancellationToken);
        if (workflow == null)
        {
            return null;
        }


        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, input)
        };

        StreamingRun run = await InProcessExecution.StreamAsync(workflow, messages);
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

        var outputs = new List<AiMessage>();

        // Display aggregated results from all agents
        Console.WriteLine("===== Final Aggregated Results =====");
        foreach (var message in result)
        {
            var chatMsg =
                new AiMessage(message.MessageId, message.AuthorName, message.Role.Value, AiMessageType.Text, message.Text);
            outputs.Add(chatMsg);
        }


        return new AgentflowExecutionResult("", Messages: outputs);
    }


    public async Task<Workflow?>
        CreateAiWorkflow(Guid agentflowId, CancellationToken cancellationToken)
    {
        var agentflow = await _agentflowRepository.GetByIdAsync(agentflowId);
        if (agentflow == null || !agentflow.Enable)
        {
            return null;
        }
        return await CreateAiWorkflow(agentflow, cancellationToken);
    }

    private async Task<Workflow?>
        CreateAiWorkflow(Agentflow agentflow, CancellationToken cancellationToken)
    {
        Guid agentflowId = agentflow.Id;

        var agentflowNodes = await _agentflowNodeRepository
            .ListAsync(x => x.AgentflowId == agentflowId);

        var agentflowEdges = await _agentflowNodeRepository
            .ListAsync(x => x.AgentflowId == agentflowId);

        List<AIAgent> aiAgents = new();
        foreach (var node in agentflowNodes)
        {
            AIAgent? aiAgent;
            if (node.Type == AgentflowNodeType.AgentNode)
            {
                aiAgent = await _agentRuntimeService.CreateAiAgentAsync(node.RelateId);

            }
            else
            {
                var flowNode = await this.CreateAiWorkflow(node.RelateId, cancellationToken);
                aiAgent = flowNode?.AsAgent() ?? null;
            }
            if (aiAgent == null)
            {
                return null;
            }
            aiAgents.Add(aiAgent);
        }

        Workflow? aiFlow;
        switch (agentflow.Pattern)
        {
            case AgentflowOrchestrationPattern.Concurrent:
                aiFlow = AgentWorkflowBuilder.BuildConcurrent(aiAgents);
                break;
            case AgentflowOrchestrationPattern.Sequential:
                aiFlow = AgentWorkflowBuilder.BuildSequential(aiAgents);
                break;
            case AgentflowOrchestrationPattern.GroupChat:
                aiFlow = AgentWorkflowBuilder.CreateGroupChatBuilderWith(
                     agents => new RoundRobinGroupChatManager(agents)
                     {
                         MaximumIterationCount = 5
                     })
                     .AddParticipants(aiAgents.ToArray())
                     .Build();
                break;
            case AgentflowOrchestrationPattern.Handoff:
                aiFlow = null;
                //aiFlow = AgentWorkflowBuilder.StartHandoffWith(triageAgent)
                //    .WithHandoffs(triageAgent, [mathTutor, historyTutor]) // Triage can route to either specialist
                //    .WithHandoff(mathTutor, triageAgent)                  // Math tutor can return to triage
                //    .WithHandoff(historyTutor, triageAgent)               // History tutor can return to triage
                //    .Build();
                break;
            case AgentflowOrchestrationPattern.Magentic:
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