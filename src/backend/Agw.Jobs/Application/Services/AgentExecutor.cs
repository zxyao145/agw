using Agw.Appliaction.Services.Agentflows;
using Agw.Appliaction.Services.Agents;
using Agw.Jobs.Domain.Entities;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Enums;
using Agw.Shared.Utils;
using Agw.Tasks.Application;
using Agw.Tasks.Domain.Services;

namespace Agw.Jobs.Application.Services;

public class AgentExecutor(
    AgentRuntimeService agentRuntimeService,
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
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="NotSupportedException"></exception>
    public async Task<Guid> ExecuteAsync(Job job, CancellationToken cancellationToken)
    {
        if (job.AgentId == null || job.AgentType == null)
        {
            throw new InvalidOperationException("Job agent target is required.");
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
