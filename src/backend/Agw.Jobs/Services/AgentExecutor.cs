using Agw.Appliaction.Services.Agentflows;
using Agw.Appliaction.Services.Agents;
using Agw.Domain.Entities;
using Agw.Domain.Services;
using Agw.Shared;
using Agw.Shared.Contracts;
using Agw.Shared.Enums;
using Agw.Shared.Utils;
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

        var contextId = TaskUtil.GenContextId();

        var createResult = await projectTaskAppService.CreateRunningAsync(
            job.ProjectId,
            new ProjectTaskCreateRequest(
                JobId: job.Id,
                Input: prompt,
                Title: string.IsNullOrWhiteSpace(job.Name)
                    ? ProjectTaskTitleFactory.Create(prompt, "Scheduled Job")
                    : job.Name.Trim(),
                ContextId: contextId),
            JobExecutorUser);

        if (createResult.Type != ApplicationResultType.Success || createResult.Value == null)
        {
            throw new InvalidOperationException(
                createResult.Error ?? "Failed to create project task for job execution.");
        }

        var projectTaskId = createResult.Value.Id;

        try
        {
            object? execution = job.AgentType.Value switch
            {
                AgentRuntimeType.Agent => await agentRuntimeService.ExecuteAsync(
                    job.AgentId.Value,
                    projectTaskId,
                    prompt,
                    cancellationToken,
                    job.ProjectId,
                    contextId),
                AgentRuntimeType.Agentflow => await agentflowRuntimeService.ExecuteAsync(
                    job.AgentId.Value,
                    projectTaskId,
                    prompt,
                    cancellationToken,
                    job.ProjectId,
                    contextId),
                _ => throw new NotSupportedException($"Unsupported agent type: {job.AgentType}")
            };

            if (execution == null)
            {
                var targetText = job.AgentType == AgentRuntimeType.Agent ? "Agent" : "Agentflow";
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
