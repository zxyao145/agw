using System.ComponentModel;
using Agw.Auth.Contracts;
using Agw.Jobs.Application.Services;
using Agw.Jobs.Contracts;
using Agw.Jobs.Contracts.Tools;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Exceptions;
using Agw.Tools.Abstractions;
using Agw.Tools.Abstractions.Attributes;
using Agw.Tools.Abstractions.Generated;

namespace Agw.Jobs.Application.Tools;

[AgwToolContainer(AgwToolPermission.ReadOnly, DefaultCategory = "Jobs", AllowInPlanMode = true)]
[ExcludeToolFromList]
public sealed class JobManagementToolExecutor
{
    private readonly JobAppService _service;
    private readonly IAgentExecutionContextAccessor _executionContext;

    public JobManagementToolExecutor(JobAppService service, IAgentExecutionContextAccessor executionContext)
    {
        _service = service;
        _executionContext = executionContext;
    }

    [AgwTool(Name = "agw_job_list")]
    [Description("Lists all scheduled jobs in the current project.")]
    public async Task<IReadOnlyList<JobToolResponse>> ListJobsAsync(
        [AgwToolService] IAgwToolInvocationContext invocationContext,
        CancellationToken cancellationToken
    )
    {
        var jobs = await _service.ListByProjectAsync(GetProjectId(invocationContext), cancellationToken);
        return jobs.Select(Map).ToArray();
    }

    [AgwTool(Name = "agw_job_get")]
    [Description("Gets one scheduled job in the current project by job ID.")]
    public async Task<JobToolResponse> GetJobAsync(
        Guid jobId,
        [AgwToolService] IAgwToolInvocationContext invocationContext,
        CancellationToken cancellationToken
    )
    {
        var projectId = GetProjectId(invocationContext);
        var job =
            await _service.GetByProjectAsync(jobId, projectId, cancellationToken)
            ?? throw new AgwException(ErrorCodes.JobNotFound);
        return Map(job);
    }

    [AgwTool("agw_job_create", AgwToolPermission.Write, AllowInPlanMode = false)]
    [Description("Creates a scheduled job in the current project.")]
    public async Task<JobToolResponse> CreateJobAsync(
        string prompt,
        AgentRuntimeType agentType,
        Guid agentId,
        TriggerType triggerType,
        string triggerValue,
        [AgwToolService] IAgwToolInvocationContext invocationContext,
        string? name = null,
        int maxRetryCount = 3,
        bool isEnabled = true,
        CancellationToken cancellationToken = default
    )
    {
        var projectId = GetProjectId(invocationContext);
        var userId = RequireInteractiveUserId(projectId);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Job prompt is required.");
        }

        if (agentId == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "agentId cannot be empty.");
        }

        if (maxRetryCount < 0)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Job maxRetryCount cannot be negative.");
        }

        var job = await _service.CreateAsync(
            new JobCreateRequest
            {
                ProjectId = projectId,
                AgentType = agentType,
                AgentId = agentId,
                Name = name ?? string.Empty,
                Prompt = prompt,
                TriggerType = triggerType,
                TriggerValue = triggerValue,
                MaxRetryCount = maxRetryCount,
                IsEnabled = isEnabled,
            },
            userId
        );
        return Map(job);
    }

    [AgwTool("agw_job_update", AgwToolPermission.Write, AllowInPlanMode = false)]
    [Description("Updates only the supplied fields of a scheduled job in the current project.")]
    public async Task<JobToolResponse> UpdateJobAsync(
        Guid jobId,
        [AgwToolService] IAgwToolInvocationContext invocationContext,
        string? name = null,
        string? prompt = null,
        bool clearPrompt = false,
        AgentRuntimeType? agentType = null,
        Guid? agentId = null,
        TriggerType? triggerType = null,
        string? triggerValue = null,
        int? maxRetryCount = null,
        bool? isEnabled = null,
        CancellationToken cancellationToken = default
    )
    {
        var projectId = GetProjectId(invocationContext);
        var userId = RequireInteractiveUserId(projectId);
        ValidatePatch(
            name,
            prompt,
            clearPrompt,
            agentType,
            agentId,
            triggerType,
            triggerValue,
            maxRetryCount,
            isEnabled
        );

        var existing =
            await _service.GetByProjectAsync(jobId, projectId, cancellationToken)
            ?? throw new AgwException(ErrorCodes.JobNotFound);
        var updated = await _service.UpdateByProjectAsync(
            jobId,
            projectId,
            new JobUpdateRequest
            {
                ProjectId = projectId,
                AgentType = agentType ?? existing.AgentType,
                AgentId = agentId ?? existing.AgentId,
                Name = name ?? existing.Name,
                Prompt = clearPrompt ? null : prompt ?? existing.Prompt,
                TriggerType = triggerType ?? existing.TriggerType,
                TriggerValue = triggerValue ?? existing.TriggerValue,
                MaxRetryCount = maxRetryCount ?? existing.MaxRetryCount,
                IsEnabled = isEnabled ?? existing.IsEnabled,
                Status = existing.Status,
            },
            userId,
            recalculateSchedule: triggerType != null || triggerValue != null,
            cancellationToken: cancellationToken
        );

        return Map(updated ?? throw new AgwException(ErrorCodes.JobNotFound));
    }

    [AgwTool("agw_job_delete", AgwToolPermission.Write, AllowInPlanMode = false)]
    [Description("Deletes a scheduled job in the current project after exact job ID confirmation.")]
    public async Task<JobToolResponse> DeleteJobAsync(
        Guid jobId,
        string confirmation,
        [AgwToolService] IAgwToolInvocationContext invocationContext,
        CancellationToken cancellationToken
    )
    {
        var projectId = GetProjectId(invocationContext);
        _ = RequireInteractiveUserId(projectId);
        if (!Guid.TryParse(confirmation, out var confirmedJobId) || confirmedJobId != jobId)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Delete confirmation must exactly match the job ID.");
        }

        var deleted = await _service.DeleteByProjectAsync(jobId, projectId, cancellationToken);
        return Map(deleted ?? throw new AgwException(ErrorCodes.JobNotFound));
    }

    private string RequireInteractiveUserId(Guid projectId)
    {
        var context = _executionContext.Current;
        if (context == null || ProjectDefaults.GetDefaultProjectIdentifier(context.ProjectId) != projectId)
        {
            throw new AgwException(ErrorCodes.InteractiveAdminRequired);
        }

        if (
            UserInfoUtil.IsContextActive
            && !string.Equals(context.UserId, UserInfoUtil.RequiredUserId, StringComparison.Ordinal)
        )
        {
            throw new AgwException(ErrorCodes.AuthenticationRequired, "A stable user id is required.");
        }

        return context.UserId;
    }

    private static Guid GetProjectId(IAgwToolInvocationContext invocationContext) =>
        ProjectDefaults.GetDefaultProjectIdentifier(invocationContext.ProjectId);

    private static void ValidatePatch(
        string? name,
        string? prompt,
        bool clearPrompt,
        AgentRuntimeType? agentType,
        Guid? agentId,
        TriggerType? triggerType,
        string? triggerValue,
        int? maxRetryCount,
        bool? isEnabled
    )
    {
        if (
            name == null
            && prompt == null
            && !clearPrompt
            && agentType == null
            && agentId == null
            && triggerType == null
            && triggerValue == null
            && maxRetryCount == null
            && isEnabled == null
        )
        {
            throw new AgwException(ErrorCodes.NoChangesToMake);
        }

        if (name != null && string.IsNullOrWhiteSpace(name))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Job name cannot be blank.");
        }

        if (clearPrompt && prompt != null)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "prompt and clearPrompt cannot be supplied together.");
        }

        if (prompt != null && string.IsNullOrWhiteSpace(prompt))
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "Job prompt cannot be blank. Use clearPrompt to remove it."
            );
        }

        if (agentType.HasValue != agentId.HasValue)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "agentType and agentId must be supplied together.");
        }

        if (agentId == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "agentId cannot be empty.");
        }

        if (maxRetryCount < 0)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Job maxRetryCount cannot be negative.");
        }
    }

    private static JobToolResponse Map(Job job) =>
        new(
            job.Id,
            job.ProjectId,
            job.AgentType,
            job.AgentId,
            job.Name,
            job.Prompt,
            job.TriggerType,
            job.TriggerValue,
            job.NextRunTime,
            job.Status,
            job.IsEnabled,
            job.RetryCount,
            job.MaxRetryCount,
            job.LastError
        );
}
