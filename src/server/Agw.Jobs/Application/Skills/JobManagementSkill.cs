using System.ComponentModel;

using Agw.Agents.Execution.Turns;
using Agw.Jobs.Application.Contracts;
using Agw.Jobs.Application.Services;
using Agw.Shared;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Exceptions;

using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Jobs.Application.Skills;

#pragma warning disable MAAI001

internal sealed class JobManagementSkill : AgentClassSkill<JobManagementSkill>
{
    private readonly Guid _projectId;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IRuntimeTurnContextAccessor _turnContextAccessor;

    public JobManagementSkill(
        Guid projectId,
        IServiceScopeFactory serviceScopeFactory,
        IRuntimeTurnContextAccessor turnContextAccessor)
    {
        _projectId = ProjectDefaults.GetDefaultProjectIdentifier(projectId);
        _serviceScopeFactory = serviceScopeFactory;
        _turnContextAccessor = turnContextAccessor;
    }

    public override AgentSkillFrontmatter Frontmatter { get; } = new(
        JobManagementSkillRegistration.SkillName,
        "Manage scheduled jobs in the current project. Use when asked to list, inspect, create, modify, enable, disable, or delete jobs.");

    protected override string Instructions =>
        """
        Use this skill only to manage jobs in the current project.

        - Use list-jobs to discover jobs and get-job before changing or deleting a job.
        - Read job-trigger-reference before creating a job or changing its schedule.
        - create-job and update-job require an interactive user context.
        - update-job has patch semantics. Omitted values remain unchanged.
        - To clear a prompt, set clearPrompt to true. Do not send prompt and clearPrompt together.
        - When changing the agent target, provide both agentType and agentId.
        - Before deleting, show the job details and ask the user to confirm the exact job ID.
        - Call delete-job only after confirmation, passing the same job ID in confirmation.
        - Report the returned job ID and nextRunTime. Never claim a write succeeded without a successful script result.
        """;

    [AgentSkillResource("job-trigger-reference")]
    [Description("Valid trigger types and values for scheduled jobs.")]
    public string JobTriggerReference =>
        """
        # Job trigger reference

        All schedules are evaluated in UTC.

        - Once: an RFC 3339 timestamp with `Z` or an explicit offset, for example `2026-08-01T09:00:00Z`.
        - Interval: a positive .NET TimeSpan value, for example `00:15:00` for fifteen minutes.
        - Cron: a standard five-field cron expression interpreted in UTC, for example `0 9 * * 1-5`.

        AgentType values are `Agent` and `Agentflow`.
        """;

    [AgentSkillScript("list-jobs")]
    [Description("Lists all scheduled jobs in the current project.")]
    private async Task<IReadOnlyList<JobSkillResponse>> ListJobsAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<JobAppService>();
        var jobs = await service.ListByProjectAsync(_projectId, cancellationToken);
        return jobs.Select(Map).ToArray();
    }

    [AgentSkillScript("get-job")]
    [Description("Gets one scheduled job in the current project by job ID.")]
    private async Task<JobSkillResponse> GetJobAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<JobAppService>();
        var job = await service.GetByProjectAsync(jobId, _projectId, cancellationToken)
            ?? throw new AgwException(ErrorCodes.JobNotFound);
        return Map(job);
    }

    [AgentSkillScript("create-job")]
    [Description("Creates a scheduled job in the current project.")]
    private async Task<JobSkillResponse> CreateJobAsync(
        string prompt,
        AgentRuntimeType agentType,
        Guid agentId,
        TriggerType triggerType,
        string triggerValue,
        string? name = null,
        int maxRetryCount = 3,
        bool isEnabled = true,
        CancellationToken cancellationToken = default)
    {
        var userName = RequireInteractiveUser();
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

        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<JobAppService>();
        var job = await service.CreateAsync(
            new JobCreateRequest
            {
                ProjectId = _projectId,
                AgentType = agentType,
                AgentId = agentId,
                Name = name ?? string.Empty,
                Prompt = prompt,
                TriggerType = triggerType,
                TriggerValue = triggerValue,
                MaxRetryCount = maxRetryCount,
                IsEnabled = isEnabled,
            },
            userName);
        return Map(job);
    }

    [AgentSkillScript("update-job")]
    [Description("Updates only the supplied fields of a scheduled job in the current project.")]
    private async Task<JobSkillResponse> UpdateJobAsync(
        Guid jobId,
        string? name = null,
        string? prompt = null,
        bool clearPrompt = false,
        AgentRuntimeType? agentType = null,
        Guid? agentId = null,
        TriggerType? triggerType = null,
        string? triggerValue = null,
        int? maxRetryCount = null,
        bool? isEnabled = null,
        CancellationToken cancellationToken = default)
    {
        var userName = RequireInteractiveUser();
        ValidatePatch(
            name,
            prompt,
            clearPrompt,
            agentType,
            agentId,
            triggerType,
            triggerValue,
            maxRetryCount,
            isEnabled);

        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<JobAppService>();
        var existing = await service.GetByProjectAsync(jobId, _projectId, cancellationToken)
            ?? throw new AgwException(ErrorCodes.JobNotFound);
        var updated = await service.UpdateByProjectAsync(
            jobId,
            _projectId,
            new JobUpdateRequest
            {
                ProjectId = _projectId,
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
            userName,
            recalculateSchedule: triggerType != null || triggerValue != null,
            cancellationToken: cancellationToken);

        return Map(updated ?? throw new AgwException(ErrorCodes.JobNotFound));
    }

    [AgentSkillScript("delete-job")]
    [Description("Deletes a scheduled job in the current project after exact job ID confirmation.")]
    private async Task<JobSkillResponse> DeleteJobAsync(
        Guid jobId,
        string confirmation,
        CancellationToken cancellationToken)
    {
        _ = RequireInteractiveUser();
        if (!Guid.TryParse(confirmation, out var confirmedJobId) || confirmedJobId != jobId)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "Delete confirmation must exactly match the job ID.");
        }

        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<JobAppService>();
        var deleted = await service.DeleteByProjectAsync(jobId, _projectId, cancellationToken);
        return Map(deleted ?? throw new AgwException(ErrorCodes.JobNotFound));
    }

    private string RequireInteractiveUser()
    {
        var context = _turnContextAccessor.Current;
        if (context == null ||
            ProjectDefaults.GetDefaultProjectIdentifier(context.Settings.ProjectId) != _projectId)
        {
            throw new AgwException(ErrorCodes.InteractiveAdminRequired);
        }

        return string.IsNullOrWhiteSpace(context.UserName)
            ? Constants.AdminUserName
            : context.UserName;
    }

    private static void ValidatePatch(
        string? name,
        string? prompt,
        bool clearPrompt,
        AgentRuntimeType? agentType,
        Guid? agentId,
        TriggerType? triggerType,
        string? triggerValue,
        int? maxRetryCount,
        bool? isEnabled)
    {
        if (name == null &&
            prompt == null &&
            !clearPrompt &&
            agentType == null &&
            agentId == null &&
            triggerType == null &&
            triggerValue == null &&
            maxRetryCount == null &&
            isEnabled == null)
        {
            throw new AgwException(ErrorCodes.NoChangesToMake);
        }

        if (name != null && string.IsNullOrWhiteSpace(name))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Job name cannot be blank.");
        }

        if (clearPrompt && prompt != null)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "prompt and clearPrompt cannot be supplied together.");
        }

        if (prompt != null && string.IsNullOrWhiteSpace(prompt))
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "Job prompt cannot be blank. Use clearPrompt to remove it.");
        }

        if (agentType.HasValue != agentId.HasValue)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "agentType and agentId must be supplied together.");
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

    private static JobSkillResponse Map(Job job) =>
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
            job.LastError);
}

#pragma warning restore MAAI001
