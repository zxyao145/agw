using A2A;
using Agw.Appliaction.Services.Agents;
using Agw.Shared.Enums;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Agw.A2A;


public class CommonAgentHandler : IAgentHandler
{
    private readonly AgentCard _agentCard;
    private readonly AgentRuntimeService _agentRuntimeService;

    public CommonAgentHandler(AgentCard agentCard, AgentRuntimeService agentRuntimeService)
    {
        _agentCard = agentCard;
        _agentRuntimeService = agentRuntimeService;
    }



    public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        var targetState = GetTargetStateFromMetadata(context.Message.Metadata);
        var updater = new TaskUpdater(eventQueue, context.TaskId, context.ContextId);

        await updater.SubmitAsync(cancellationToken);


        var userText = context.Message.Parts.FirstOrDefault((Part p) => p.Text != null)?.Text;


        await updater.AddArtifactAsync([Part.FromText($"Echo: {userText}")], cancellationToken: cancellationToken);

        // Transition to the target state (defaults to Completed)
        switch (targetState)
        {
            case TaskState.Working:
                await updater.StartWorkAsync(cancellationToken: cancellationToken);
                break;
            case TaskState.Failed:
                await updater.FailAsync(cancellationToken: cancellationToken);
                break;
            case TaskState.Canceled:
                await updater.CancelAsync(cancellationToken);
                break;
            case TaskState.InputRequired:
                await updater.RequireInputAsync(
                    new Message { Role = Role.Agent, MessageId = Guid.NewGuid().ToString("N"), Parts = [Part.FromText("Need input")] },
                    cancellationToken);
                break;
            default:
                await updater.CompleteAsync(cancellationToken: cancellationToken);
                break;
        }

    }


    private static TaskState? GetTargetStateFromMetadata(Dictionary<string, JsonElement>? metadata)
    {
        if (metadata?.TryGetValue("task-target-state", out var targetStateElement) == true)
        {
            if (Enum.TryParse<TaskState>(targetStateElement.GetString(), true, out var state))
            {
                return state;
            }
        }

        return null;
    }


    public async Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        await new TaskUpdater(eventQueue, context.TaskId, context.ContextId).CancelAsync(cancellationToken);
    }
}


public class AgentHandlerFactory
{
    private readonly A2AAgentService _a2aAgentService;
    private readonly AgentRuntimeService _agentRuntimeService;



    public AgentHandlerFactory(A2AAgentService a2aAgentService, AgentRuntimeService agentRuntimeService)
    {
        _a2aAgentService = a2aAgentService;
        _agentRuntimeService = agentRuntimeService;
    }


    public async Task<IAgentHandler?> CreateAsync(string agentName)
    {
        var agentCard = await _a2aAgentService.GetAgentCardAsync(agentName);
        if (agentCard == null)
        {
            return null;
        }

        return new CommonAgentHandler(agentCard, _agentRuntimeService);
    }
}
