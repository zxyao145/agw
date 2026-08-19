using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Agents.Dtos;
using Agw.Projects.Application;
using Agw.Projects.Domain.Services;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;

namespace Agw.Jobs.Execution;

public sealed class JobAgentExecutor : IJobAgentExecutor
{
    private const string JobExecutorUser = "job-executor";

    private readonly IAgentRuntimeService _agentRuntimeService;
    private readonly IAgentflowRuntimeService _agentflowRuntimeService;
    private readonly TaskExecutionAppService _taskExecutionAppService;

    public JobAgentExecutor(
        IAgentRuntimeService agentRuntimeService,
        IAgentflowRuntimeService agentflowRuntimeService,
        TaskExecutionAppService taskExecutionAppService
    )
    {
        _agentRuntimeService = agentRuntimeService;
        _agentflowRuntimeService = agentflowRuntimeService;
        _taskExecutionAppService = taskExecutionAppService;
    }

    public async Task<Guid> ExecuteAsync(Job job, CancellationToken cancellationToken)
    {
        if (job.AgentId == null || job.AgentType == null)
        {
            throw new AgwException(ErrorCodes.JobAgentTargetRequired);
        }

        var (prompt, title) = BuildPromptAndTitle(job);
        var contextId = ContextIdUtil.GenContextId();
        var createResult = await _taskExecutionAppService.CreateRunningAsync(
            job.ProjectId,
            new TaskCreateRequest(JobId: job.Id, Input: prompt, Title: title, ContextId: contextId),
            JobExecutorUser
        );

        if (createResult.Type != ApplicationResultType.Success || createResult.Value == null)
        {
            throw new AgwException(
                ErrorCodes.TaskCreationFailed,
                createResult.Error ?? "Failed to create task for job execution."
            );
        }

        var taskId = createResult.Value.TaskId;

        try
        {
            object? execution = job.AgentType.Value switch
            {
                AgentRuntimeType.Agent => await _agentRuntimeService.ExecuteByIdAsync(
                    new AgentExecuteByIdRequest(prompt, job.AgentId.Value, taskId, job.ProjectId, contextId),
                    cancellationToken
                ),
                AgentRuntimeType.Agentflow => await _agentflowRuntimeService.ExecuteAsync(
                    job.AgentId.Value,
                    taskId,
                    prompt,
                    cancellationToken,
                    job.ProjectId,
                    contextId
                ),
                _ => throw new AgwException(
                    ErrorCodes.UnsupportedAgentType,
                    $"Unsupported agent type: {job.AgentType}"
                ),
            };

            if (execution == null)
            {
                var targetText = job.AgentType == AgentRuntimeType.Agent ? "Agent" : "Agentflow";
                throw new AgwException(
                    ErrorCodes.AgentExecutionFailed,
                    $"{targetText} execution failed (target disabled/missing or runtime unavailable)."
                );
            }

            var succeededTask = await _taskExecutionAppService.MarkSucceededAsync(taskId, JobExecutorUser);
            if (succeededTask == null)
            {
                throw new AgwException(
                    ErrorCodes.TaskMarkSucceededFailed,
                    $"Failed to mark task {taskId} as succeeded."
                );
            }

            return taskId;
        }
        catch (Exception ex)
        {
            _ = await _taskExecutionAppService.MarkFailedAsync(taskId, ex.Message, JobExecutorUser);
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
                : TaskTitleFactory.Create(trimmedPrompt, "Scheduled Job");

            return (trimmedPrompt, title);
        }

        if (!string.IsNullOrWhiteSpace(trimmedName))
        {
            return ($"Run job: {trimmedName}", trimmedName);
        }

        return ("Scheduled Job", "Scheduled Job");
    }
}
