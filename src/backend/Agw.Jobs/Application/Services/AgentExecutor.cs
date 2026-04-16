using Agw.Agents.Application.Agentflows;
using Agw.Agents.Application.AgentRun;
using Agw.Agents.Application.AgentRun.Dtos;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Agw.Tasks.Application;
using Agw.Tasks.Domain.Services;

namespace Agw.Jobs.Application.Services;

public class AgentExecutor(
    IAgentRuntimeService agentRuntimeService,
    AgentflowRuntimeService agentflowRuntimeService,
    ProjectTaskAppService projectTaskAppService) : IAgentExecutor
{
    private const string JobExecutorUser = "job-executor";

    /// <summary>
    /// return task ID
    /// </summary>
    /// <param name="job"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="AgwException"></exception>
    public async Task<Guid> ExecuteAsync(Job job, CancellationToken cancellationToken)
    {
        if (job.AgentId == null || job.AgentType == null)
        {
            throw new AgwException(ErrorCodes.JobAgentTargetRequired);
        }

        var (prompt, title) = BuildPromptAndTitle(job);

        var contextId = TaskUtil.GenContextId();

        var createResult = await projectTaskAppService.CreateRunningAsync(
            job.ProjectId,
            new ProjectTaskCreateRequest(
                JobId: job.Id,
                Input: prompt,
                Title: title,
                ContextId: contextId),
            JobExecutorUser);

        if (createResult.Type != ApplicationResultType.Success || createResult.Value == null)
        {
            throw new AgwException(
                ErrorCodes.ProjectTaskCreationFailed,
                createResult.Error ?? "Failed to create project task for job execution.");
        }

        var projectTaskId = createResult.Value.Id;

        try
        {
            object? execution = job.AgentType.Value switch
            {
                AgentRuntimeType.Agent => await agentRuntimeService.ExecuteByIdAsync
                (
                    new AgentExecuteByIdRequest(prompt, job.AgentId.Value, projectTaskId, job.ProjectId, contextId),
                    cancellationToken
                ),
                AgentRuntimeType.Agentflow => await agentflowRuntimeService.ExecuteAsync(
                    job.AgentId.Value,
                    projectTaskId,
                    prompt,
                    cancellationToken,
                    job.ProjectId,
                    contextId),
                _ => throw new AgwException(ErrorCodes.UnsupportedAgentType, $"Unsupported agent type: {job.AgentType}")
            };

            if (execution == null)
            {
                var targetText = job.AgentType == AgentRuntimeType.Agent ? "Agent" : "Agentflow";
                throw new AgwException(
                    ErrorCodes.AgentExecutionFailed,
                    $"{targetText} execution failed (target disabled/missing or runtime unavailable).");
            }

            var succeededTask = await projectTaskAppService.MarkSucceededAsync(
                projectTaskId,
                JobExecutorUser);
            if (succeededTask == null)
            {
                throw new AgwException(
                    ErrorCodes.ProjectTaskMarkSucceededFailed,
                    $"Failed to mark project task {projectTaskId} as succeeded.");
            }
            return projectTaskId;
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

    private static (string Prompt, string Title) BuildPromptAndTitle(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);

        var trimmedName = job.Name.Trim();
        var trimmedPrompt = job.Prompt?.Trim();

        if (!string.IsNullOrWhiteSpace(trimmedPrompt))
        {
            var title = !string.IsNullOrWhiteSpace(trimmedName)
                ? trimmedName
                : ProjectTaskTitleFactory.Create(trimmedPrompt, "Scheduled Job");

            return (trimmedPrompt, title);
        }

        if (!string.IsNullOrWhiteSpace(trimmedName))
        {
            return ($"Run job: {trimmedName}", trimmedName);
        }

        return ("Scheduled Job", "Scheduled Job");
    }
}
