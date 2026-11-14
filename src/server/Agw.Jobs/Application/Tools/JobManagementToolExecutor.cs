using System.ComponentModel;
using Agw.Auth.Contracts;
using Agw.Jobs.Application.Services;
using Agw.Jobs.Contracts;
using Agw.Jobs.Contracts.Tools;
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
    ) => (await _service.ListAsync(GetProjectId(invocationContext), cancellationToken)).Select(Map).ToArray();

    [AgwTool(Name = "agw_job_get")]
    [Description("Gets one scheduled job in the current project by job ID.")]
    public async Task<JobToolResponse> GetJobAsync(
        Guid jobId,
        [AgwToolService] IAgwToolInvocationContext invocationContext,
        CancellationToken cancellationToken
    ) => Map(await _service.GetForProjectAsync(jobId, GetProjectId(invocationContext), cancellationToken));

    [AgwTool("agw_job_create", AgwToolPermission.Write, AllowInPlanMode = false)]
    [Description("Creates a scheduled job in the current project.")]
    public async Task<JobToolResponse> CreateJobAsync(
        string instructions,
        AgentRuntimeType targetKind,
        Guid agentId,
        string kind,
        [AgwToolService] IAgwToolInvocationContext invocationContext,
        string? name = null,
        DateTimeOffset? at = null,
        string? interval = null,
        string? cron = null,
        bool enabled = true,
        CancellationToken cancellationToken = default
    )
    {
        var projectId = GetProjectId(invocationContext);
        RequireInteractiveUser(projectId);
        var job = await _service.CreateAsync(
            new JobCreateRequest
            {
                Name = name,
                Enabled = enabled,
                ProjectId = projectId,
                TargetKind = targetKind,
                AgentId = agentId,
                Instructions = instructions,
                Schedule = new JobScheduleDto
                {
                    Kind = kind,
                    At = at,
                    Interval = interval,
                    Cron = cron,
                },
            },
            cancellationToken
        );
        return Map(job);
    }

    [AgwTool("agw_job_update", AgwToolPermission.Write, AllowInPlanMode = false)]
    [Description("Updates only the supplied fields of a scheduled job in the current project.")]
    public async Task<JobToolResponse> UpdateJobAsync(
        Guid jobId,
        [AgwToolService] IAgwToolInvocationContext invocationContext,
        string? name = null,
        string? instructions = null,
        AgentRuntimeType? targetKind = null,
        Guid? agentId = null,
        string? kind = null,
        DateTimeOffset? at = null,
        string? interval = null,
        string? cron = null,
        bool? enabled = null,
        CancellationToken cancellationToken = default
    )
    {
        var projectId = GetProjectId(invocationContext);
        RequireInteractiveUser(projectId);
        var hasNoChanges =
            name is null
            && instructions is null
            && targetKind is null
            && agentId is null
            && kind is null
            && at is null
            && interval is null
            && cron is null
            && enabled is null;
        if (hasNoChanges)
        {
            throw new AgwException(ErrorCodes.NoChangesToMake);
        }
        if (name is not null && string.IsNullOrWhiteSpace(name))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Job name cannot be blank.");
        }
        if (instructions is not null && string.IsNullOrWhiteSpace(instructions))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Job instructions cannot be blank.");
        }
        if (targetKind.HasValue != agentId.HasValue)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "targetKind and agentId must be supplied together.");
        }

        var job = await _service.UpdateForProjectAsync(
            jobId,
            projectId,
            current => new JobCreateRequest
            {
                Name = name ?? current.Name,
                Enabled = enabled ?? current.Enabled,
                ProjectId = projectId,
                TargetKind = targetKind ?? current.TargetKind,
                AgentId = agentId ?? current.AgentId,
                Instructions = instructions ?? current.Instructions,
                Schedule = new JobScheduleDto
                {
                    Kind = kind ?? current.Schedule.Kind,
                    At = at ?? current.Schedule.At,
                    Interval = interval ?? current.Schedule.Interval,
                    Cron = cron ?? current.Schedule.Cron,
                },
            },
            cancellationToken
        );
        return Map(job);
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
        RequireInteractiveUser(projectId);
        if (!Guid.TryParse(confirmation, out var confirmedJobId) || confirmedJobId != jobId)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Delete confirmation must exactly match the job ID.");
        }

        return Map(await _service.DeleteForProjectAsync(jobId, projectId, cancellationToken));
    }

    private void RequireInteractiveUser(Guid projectId)
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
    }

    private static Guid GetProjectId(IAgwToolInvocationContext invocationContext) =>
        ProjectDefaults.GetDefaultProjectIdentifier(invocationContext.ProjectId);

    private static JobToolResponse Map(JobResponse job) =>
        new()
        {
            Id = job.Id,
            Name = job.Name,
            Enabled = job.Enabled,
            ProjectId = job.ProjectId,
            TargetKind = job.TargetKind,
            AgentId = job.AgentId,
            Instructions = job.Instructions,
            Schedule = job.Schedule,
            DueAt = job.DueAt,
            ScheduleCompleted = job.ScheduleCompleted,
            LastDelivery = job.LastDelivery,
        };
}
