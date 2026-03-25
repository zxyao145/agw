using Agw.Appliaction.Services.Agentflows;
using Agw.Appliaction.Services.Agents;
using Agw.Domain.Entities;
using Agw.Shared.Enums;

namespace Agw.Jobs.Services;

public class AgentExecutor(
    AgentRuntimeService agentRuntimeService,
    AgentflowRuntimeService agentflowRuntimeService) : IAgentExecutor
{
    public async Task ExecuteAsync(Job task, CancellationToken cancellationToken)
    {
        if (task.AgentId == null || task.AgentType == null)
        {
            throw new InvalidOperationException("Scheduled task agent target is required.");
        }

        var prompt = string.IsNullOrWhiteSpace(task.Prompt)
            ? $"Run scheduled task: {task.Name}"
            : task.Prompt;

        var sessionId = Guid.NewGuid().ToString("N");
        var contextId = Guid.NewGuid().ToString("N");

        switch (task.AgentType.Value)
        {
            case ProjectTaskAgentType.Agent:
                _ = await agentRuntimeService.ExecuteAsync(
                    task.AgentId.Value,
                    sessionId,
                    prompt,
                    cancellationToken,
                    task.ProjectId,
                    contextId);
                break;
            case ProjectTaskAgentType.Agentflow:
                _ = await agentflowRuntimeService.ExecuteAsync(
                    task.AgentId.Value,
                    sessionId,
                    prompt,
                    cancellationToken,
                    task.ProjectId,
                    contextId);
                break;
            default:
                throw new NotSupportedException($"Unsupported agent type: {task.AgentType}");
        }
    }
}
