using DSystem.Domain.Entities;
using DSystem.Domain.Enums;
using DSystem.Domain.Models;
using DSystem.Domain.Repositories;
using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
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
    private readonly IRepository<WorkflowAgent> _workflowAgentRepository;
    private readonly AgentRuntimeService _agentRuntimeService;
    private readonly IWorkflowAgentExecutor _executor;

    public WorkflowRuntimeService(
        IRepository<Workflow> workflowRepository,
        IRepository<WorkflowAgent> workflowAgentRepository,
        AgentRuntimeService agentRuntimeService,
        IWorkflowAgentExecutor executor)
    {
        _workflowRepository = workflowRepository;
        _workflowAgentRepository = workflowAgentRepository;
        _agentRuntimeService = agentRuntimeService;
        _executor = executor;
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

        var workflowAgents = await _workflowAgentRepository.ListAsync(x => x.WorkflowId == workflowId);
        var ordered = workflowAgents.OrderBy(x => x.Order).ToList();

        switch (workflow.Pattern)
        {
            case WorkflowOrchestrationPattern.Concurrent:
                return await ExecuteConcurrentAsync(workflow, ordered, input, cancellationToken);

            case WorkflowOrchestrationPattern.Sequential:
                return await ExecuteSequentialAsync(workflow, ordered, input, cancellationToken);

            case WorkflowOrchestrationPattern.GroupChat:
                return await ExecuteGroupChatAsync(workflow, ordered, input, cancellationToken);

            case WorkflowOrchestrationPattern.Handoff:
                return await ExecuteHandoffAsync(workflow, ordered, input, cancellationToken);

            case WorkflowOrchestrationPattern.Magentic:
                return await ExecuteMagenticAsync(workflow, ordered, input, cancellationToken);

            default:
                return new WorkflowExecutionResult(
                    workflow.Id,
                    workflow.Pattern,
                    NotImplemented: true,
                    Message: $"Workflow pattern '{workflow.Pattern}' is not implemented yet.",
                    Input: input,
                    FinalOutput: null,
                    Outputs: Array.Empty<WaChatMessage>());
        }
    }

    private async Task<WorkflowExecutionResult?> ExecuteConcurrentAsync(
        Workflow workflow,
        IReadOnlyList<WorkflowAgent> agents,
        string input,
        CancellationToken cancellationToken)
    {
        var aiAgents = new List<AIAgent>();
        foreach (var wa in agents)
        {
            var aiAgent = await _agentRuntimeService.CreateAiAgentAsync(wa.AgentId);
            if (aiAgent == null)
            {
                return null;
            }
            aiAgents.Add(aiAgent);
        }

        var aiWorkflow = AgentWorkflowBuilder.BuildConcurrent(aiAgents);
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

    private async Task<WorkflowExecutionResult?> ExecuteSequentialAsync(
        Entities.Workflow workflow,
        IReadOnlyList<WorkflowAgent> agents,
        string input,
        CancellationToken cancellationToken)
    {
        if (agents.Count == 0)
        {
            return null;
        }

        var outputs = new List<WaChatMessage>();
        var current = input;
        List<AIAgent> aiAgents = new ();
        foreach (var wa in agents.OrderBy(x => x.Order))
        {
            var aiAgent = await _agentRuntimeService.CreateAiAgentAsync(wa.AgentId);
            if (aiAgent == null)
            {
                return null;
            }

            aiAgents.Add(aiAgent);
            //var output = await _executor.ExecuteAsync(aiAgent, current, cancellationToken);
            //outputs.Add(new WorkflowExecutionAgentResult(aiAgent.Id, aiAgent.Name, wa.Order, output));
            //current = output;
        }
        // create workflow
        var agentWorkflow = AgentWorkflowBuilder.BuildSequential(aiAgents);

        // Run the workflow
        var messages = new List<ChatMessage> { new(ChatRole.User, input) };
        StreamingRun run = await InProcessExecution.StreamAsync(agentWorkflow, messages);
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

        // Display final result
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
            FinalOutput: current,
            Outputs: outputs);
    }

    private async Task<WorkflowExecutionResult?> ExecuteGroupChatAsync(
        Workflow workflow,
        IReadOnlyList<WorkflowAgent> agents,
        string input,
        CancellationToken cancellationToken)
    {
        if (agents.Count == 0)
        {
            return null;
        }

        var aiAgents = new List<AIAgent>();
        foreach (var wa in agents)
        {
            var aiAgent = await _agentRuntimeService.CreateAiAgentAsync(wa.AgentId);
            if (aiAgent == null)
            {
                return null;
            }
            aiAgents.Add(aiAgent);
        }

        // Parse configuration for max iterations (default to 10)
        int maxIterations = 10;
        if (!string.IsNullOrEmpty(workflow.ConfigurationJson))
        {
            try
            {
                var config = System.Text.Json.JsonDocument.Parse(workflow.ConfigurationJson);
                if (config.RootElement.TryGetProperty("maxIterations", out var maxIterProp))
                {
                    maxIterations = maxIterProp.GetInt32();
                }
            }
            catch
            {
                // Use default if parsing fails
            }
        }

        // Create group chat workflow with RoundRobinGroupChatManager
        var agentWorkflow = AgentWorkflowBuilder.CreateGroupChatBuilderWith(
            agents => new RoundRobinGroupChatManager(agents)
            {
                MaximumIterationCount = maxIterations
            })
            .AddParticipants(aiAgents.ToArray())
            .Build();

        // Run the workflow
        var messages = new List<ChatMessage> { new(ChatRole.User, input) };
        StreamingRun run = await InProcessExecution.StreamAsync(agentWorkflow, messages);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        List<ChatMessage> result = new();
        await foreach (WorkflowEvent evt in run.WatchStreamAsync().ConfigureAwait(false))
        {
            if (evt is AgentRunUpdateEvent e)
            {
                Console.WriteLine($"[GroupChat] {e.ExecutorId}: {e.Data}");
            }
            else if (evt is WorkflowOutputEvent outputEvt)
            {
                result = (List<ChatMessage>)outputEvt.Data!;
                break;
            }
        }

        var outputs = new List<WaChatMessage>();
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
            FinalOutput: result.LastOrDefault()?.Text,
            Outputs: outputs);
    }

    private async Task<WorkflowExecutionResult?> ExecuteHandoffAsync(
        Workflow workflow,
        IReadOnlyList<WorkflowAgent> agents,
        string input,
        CancellationToken cancellationToken)
    {
        if (agents.Count == 0)
        {
            return null;
        }

        var aiAgents = new List<AIAgent>();
        foreach (var wa in agents)
        {
            var aiAgent = await _agentRuntimeService.CreateAiAgentAsync(wa.AgentId);
            if (aiAgent == null)
            {
                return null;
            }
            aiAgents.Add(aiAgent);
        }

        // For handoff pattern, use group chat with specialized instructions
        // to simulate handoff behavior where agents pass control to each other
        if (aiAgents.Count < 2)
        {
            return new WorkflowExecutionResult(
                workflow.Id,
                workflow.Pattern,
                NotImplemented: true,
                Message: "Handoff pattern requires at least 2 agents.",
                Input: input,
                FinalOutput: null,
                Outputs: Array.Empty<WaChatMessage>());
        }

        // Parse configuration for max handoffs (default to 5)
        int maxIterations = 5;
        if (!string.IsNullOrEmpty(workflow.ConfigurationJson))
        {
            try
            {
                var config = System.Text.Json.JsonDocument.Parse(workflow.ConfigurationJson);
                if (config.RootElement.TryGetProperty("maxHandoffs", out var maxHandoffsProp))
                {
                    maxIterations = maxHandoffsProp.GetInt32();
                }
            }
            catch
            {
                // Use default if parsing fails
            }
        }

        // Use GroupChat as the foundation for handoff pattern
        // The agents will coordinate handoffs through their instructions
        var agentWorkflow = AgentWorkflowBuilder.CreateGroupChatBuilderWith(
            agents => new RoundRobinGroupChatManager(agents)
            {
                MaximumIterationCount = maxIterations
            })
            .AddParticipants(aiAgents.ToArray())
            .Build();

        // Run the workflow
        var messages = new List<ChatMessage> { new(ChatRole.User, input) };
        StreamingRun run = await InProcessExecution.StreamAsync(agentWorkflow, messages);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        List<ChatMessage> result = new();
        await foreach (WorkflowEvent evt in run.WatchStreamAsync().ConfigureAwait(false))
        {
            if (evt is AgentRunUpdateEvent e)
            {
                Console.WriteLine($"[Handoff] {e.ExecutorId}: {e.Data}");
            }
            else if (evt is WorkflowOutputEvent outputEvt)
            {
                result = (List<ChatMessage>)outputEvt.Data!;
                break;
            }
        }

        var outputs = new List<WaChatMessage>();
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
            FinalOutput: result.LastOrDefault()?.Text,
            Outputs: outputs);
    }

    private async Task<WorkflowExecutionResult?> ExecuteMagenticAsync(
        Workflow workflow,
        IReadOnlyList<WorkflowAgent> agents,
        string input,
        CancellationToken cancellationToken)
    {
        if (agents.Count == 0)
        {
            return null;
        }

        var aiAgents = new List<AIAgent>();
        foreach (var wa in agents)
        {
            var aiAgent = await _agentRuntimeService.CreateAiAgentAsync(wa.AgentId);
            if (aiAgent == null)
            {
                return null;
            }
            aiAgents.Add(aiAgent);
        }

        // Magentic pattern requires an orchestrator (first agent)
        // and specialized worker agents
        if (aiAgents.Count < 2)
        {
            return new WorkflowExecutionResult(
                workflow.Id,
                workflow.Pattern,
                NotImplemented: true,
                Message: "Magentic pattern requires at least 2 agents (1 orchestrator + workers).",
                Input: input,
                FinalOutput: null,
                Outputs: Array.Empty<WaChatMessage>());
        }

        // Parse configuration for parameters
        int maxRounds = 10;
        int maxStallCount = 3;
        if (!string.IsNullOrEmpty(workflow.ConfigurationJson))
        {
            try
            {
                var config = System.Text.Json.JsonDocument.Parse(workflow.ConfigurationJson);
                if (config.RootElement.TryGetProperty("maxRounds", out var maxRoundsProp))
                {
                    maxRounds = maxRoundsProp.GetInt32();
                }
                if (config.RootElement.TryGetProperty("maxStallCount", out var maxStallProp))
                {
                    maxStallCount = maxStallProp.GetInt32();
                }
            }
            catch
            {
                // Use defaults if parsing fails
            }
        }

        // Use GroupChat with custom manager for Magentic-style orchestration
        // The first agent acts as the orchestrator/manager
        var orchestrator = aiAgents[0];
        var workers = aiAgents.Skip(1).ToList();

        // Create a group chat with the orchestrator managing worker agents
        var agentWorkflow = AgentWorkflowBuilder.CreateGroupChatBuilderWith(
            allAgents =>
            {
                // Use RoundRobinGroupChatManager with the orchestrator pattern
                // The orchestrator will coordinate task distribution
                return new RoundRobinGroupChatManager(allAgents)
                {
                    MaximumIterationCount = maxRounds
                };
            })
            .AddParticipants([orchestrator, .. workers])
            .Build();

        // Run the workflow
        var messages = new List<ChatMessage> { new(ChatRole.User, input) };
        StreamingRun run = await InProcessExecution.StreamAsync(agentWorkflow, messages);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        List<ChatMessage> result = new();
        await foreach (WorkflowEvent evt in run.WatchStreamAsync().ConfigureAwait(false))
        {
            if (evt is AgentRunUpdateEvent e)
            {
                Console.WriteLine($"[Magentic] {e.ExecutorId}: {e.Data}");
            }
            else if (evt is WorkflowOutputEvent outputEvt)
            {
                result = (List<ChatMessage>)outputEvt.Data!;
                break;
            }
        }

        var outputs = new List<WaChatMessage>();
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
            FinalOutput: result.LastOrDefault()?.Text,
            Outputs: outputs);
    }
}