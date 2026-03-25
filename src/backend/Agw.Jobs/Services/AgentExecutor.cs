using Agw.Appliaction.Services.Agentflows;
using Agw.Appliaction.Services.Agents;
using Agw.Domain.Entities;
using Agw.Shared.Enums;

namespace Agw.Jobs.Services;

public class AgentExecutor(
    AgentRuntimeService agentRuntimeService,
    AgentflowRuntimeService agentflowRuntimeService) : IAgentExecutor
{
    public async Task ExecuteAsync(Job job, CancellationToken cancellationToken)
    {
        if (job.AgentId == null || job.AgentType == null)
        {
            throw new InvalidOperationException("Job agent target is required.");
        }

        var prompt = string.IsNullOrWhiteSpace(job.Prompt)
            ? $"Run job: {job.Name}"
            : job.Prompt;

        var sessionId = Guid.NewGuid().ToString("N");
        var contextId = Guid.NewGuid().ToString("N");

        switch (job.AgentType.Value)
        {
            case ProjectTaskAgentType.Agent:
                _ = await agentRuntimeService.ExecuteAsync(
                    job.AgentId.Value,
                    sessionId,
                    prompt,
                    cancellationToken,
                    job.ProjectId,
                    contextId);
                break;
            case ProjectTaskAgentType.Agentflow:
                _ = await agentflowRuntimeService.ExecuteAsync(
                    job.AgentId.Value,
                    sessionId,
                    prompt,
                    cancellationToken,
                    job.ProjectId,
                    contextId);
                break;
            default:
                throw new NotSupportedException($"Unsupported agent type: {job.AgentType}");
        }
    }
}
