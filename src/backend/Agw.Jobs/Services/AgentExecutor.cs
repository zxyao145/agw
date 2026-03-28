using Agw.Appliaction.Services.Agentflows;
using Agw.Appliaction.Services.Agents;
using Agw.Domain.Entities;
using Agw.Shared;
using Agw.Shared.Enums;
using Agw.Shared.Contracts;
using Agw.Tasks.Services;

namespace Agw.Jobs.Services;

public class AgentExecutor(
    AgentRuntimeService agentRuntimeService,
    AgentflowRuntimeService agentflowRuntimeService,
    ProjectTaskAppService projectTaskAppService) : IAgentExecutor
{
    private const string JobExecutorUser = "job-executor";

    public async Task ExecuteAsync(Job job, CancellationToken cancellationToken)
    {
        if (job.AgentId == null || job.AgentType == null)
        {
            throw new InvalidOperationException("Job agent target is required.");
        }

        var prompt = string.IsNullOrWhiteSpace(job.Prompt)
            ? $"Run job: {job.Name}"
            : job.Prompt;

        var title = string.IsNullOrWhiteSpace(job.Name)
            ? "Scheduled Job"
            : job.Name.Trim();
        var description = string.IsNullOrWhiteSpace(job.Name)
            ? prompt
            : job.Name.Trim();
        var contextId = job.Id.Normalize();

        var createResult = await projectTaskAppService.CreateRunningAsync(
            job.ProjectId,
            new ProjectTaskCreateRequest(
                AgentType: job.AgentType.Value,
                AgentflowId: job.AgentType == ProjectTaskAgentType.Agentflow ? job.AgentId : null,
                AgentId: job.AgentType == ProjectTaskAgentType.Agent ? job.AgentId : null,
                Description: description,
                Input: prompt,
                SessionId: null,
                Title: title,
                SystemPrompt: null,
                ContextId: contextId),
            JobExecutorUser);

        if (createResult.Type != ApplicationResultType.Success || createResult.Value == null)
        {
            throw new InvalidOperationException(
                createResult.Error ?? "Failed to create project task for job execution.");
        }

        var projectTaskId = createResult.Value.Id;
        var sessionId = createResult.Value.SessionId;

        try
        {
            object? execution = job.AgentType.Value switch
            {
                ProjectTaskAgentType.Agent => await agentRuntimeService.ExecuteAsync(
                    job.AgentId.Value,
                    sessionId,
                    prompt,
                    cancellationToken,
                    job.ProjectId,
                    contextId),
                ProjectTaskAgentType.Agentflow => await agentflowRuntimeService.ExecuteAsync(
                    job.AgentId.Value,
                    sessionId,
                    prompt,
                    cancellationToken,
                    job.ProjectId,
                    contextId),
                _ => throw new NotSupportedException($"Unsupported agent type: {job.AgentType}")
            };

            if (execution == null)
            {
                var targetText = job.AgentType == ProjectTaskAgentType.Agent ? "Agent" : "Agentflow";
                throw new InvalidOperationException(
                    $"{targetText} execution failed (target disabled/missing or runtime unavailable).");
            }

            var succeededTask = await projectTaskAppService.MarkSucceededAsync(
                projectTaskId,
                JobExecutorUser);
            if (succeededTask == null)
            {
                throw new InvalidOperationException(
                    $"Failed to mark project task {projectTaskId} as succeeded.");
            }
        }
        catch (Exception ex)
        {
            _ = await projectTaskAppService.MarkFailedAsync(
                projectTaskId,
                ex.Message,
                JobExecutorUser);
            throw;
        }
    }
}
