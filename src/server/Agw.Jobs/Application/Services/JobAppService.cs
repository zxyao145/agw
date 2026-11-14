using Agw.Auth.Contracts;
using Agw.Events.Contracts;
using Agw.Jobs.Contracts;
using Agw.Jobs.Domain.Services;
using Agw.Projects.Contracts.Execution;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Exceptions;

namespace Agw.Jobs.Application.Services;

public sealed class JobAppService
{
    private readonly IEventJobFacade _jobs;
    private readonly JobDefinitionDomainService _definitions;
    private readonly IProjectTaskFacade _projectTasks;
    private readonly IApplicationLock _applicationLock;
    private readonly TimeProvider _timeProvider;
    private readonly IUserInfoService _userInfo;

    public JobAppService(
        IEventJobFacade jobs,
        JobDefinitionDomainService definitions,
        IProjectTaskFacade projectTasks,
        IApplicationLock applicationLock,
        TimeProvider timeProvider,
        IUserInfoService userInfo
    )
    {
        _jobs = jobs;
        _definitions = definitions;
        _projectTasks = projectTasks;
        _applicationLock = applicationLock;
        _timeProvider = timeProvider;
        _userInfo = userInfo;
    }

    public async Task<IReadOnlyList<JobResponse>> ListAsync(Guid? projectId, CancellationToken cancellationToken) =>
        (await _jobs.ListAsync(projectId, cancellationToken)).Select(ToResponse).ToArray();

    public async Task<JobResponse> GetAsync(Guid id, CancellationToken cancellationToken) =>
        ToResponse(await _jobs.FindAsync(id, cancellationToken) ?? throw new AgwException(ErrorCodes.JobNotFound));

    public async Task<JobResponse> CreateAsync(JobCreateRequest request, CancellationToken cancellationToken)
    {
        await using var lease = await AcquireDefinitionLockAsync(cancellationToken);
        var definition = await ResolveDefinitionAsync(request, lease.Token);
        return ToResponse(await _jobs.CreateAsync(definition, lease.Token));
    }

    public Task<JobResponse> UpdateAsync(JobUpdateRequest request, CancellationToken cancellationToken) =>
        UpdateAsync(
            request.Id,
            null,
            _ => new JobCreateRequest
            {
                Name = request.Name,
                Enabled = request.Enabled,
                ProjectId = request.ProjectId,
                TargetKind = request.TargetKind,
                AgentId = request.AgentId,
                Instructions = request.Instructions,
                Schedule = request.Schedule,
            },
            cancellationToken
        );

    public async Task<JobResponse> SetEnabledAsync(JobEnableRequest request, CancellationToken cancellationToken)
    {
        await using var lease = await AcquireDefinitionLockAsync(cancellationToken);
        return ToResponse(await _jobs.SetEnabledAsync(request.Id, request.Enabled, lease.Token));
    }

    public async Task<JobResponse> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        ToResponse(await _jobs.DeleteAsync(id, cancellationToken));

    public async Task<IReadOnlyList<JobRunResponse>> ListRunsAsync(
        Guid jobId,
        int offset,
        int limit,
        CancellationToken cancellationToken
    )
    {
        var runs = await _jobs.ListRunsAsync(jobId, offset, limit, cancellationToken);
        var turnIds = runs.Where(run => run.TurnId.HasValue).Select(run => run.TurnId!.Value).Distinct().ToArray();
        var conversations = await _projectTasks.ResolveConversationIdsAsync(turnIds, cancellationToken);
        return runs.Select(run => new JobRunResponse
            {
                Id = run.Id,
                DeliveryId = run.DeliveryId,
                Attempt = run.Attempt,
                StartedAt = run.StartedAt,
                EndedAt = run.EndedAt,
                Result = run.Result,
                Error = run.Error,
                ConversationId =
                    run.TurnId is { } turnId && conversations.TryGetValue(turnId, out var conversationId)
                        ? conversationId
                        : null,
            })
            .ToArray();
    }

    internal async Task<JobResponse> GetForProjectAsync(Guid id, Guid projectId, CancellationToken cancellationToken)
    {
        var job = await GetAsync(id, cancellationToken);
        RequireProject(job, projectId);
        return job;
    }

    internal Task<JobResponse> UpdateForProjectAsync(
        Guid id,
        Guid projectId,
        Func<JobResponse, JobCreateRequest> patch,
        CancellationToken cancellationToken
    ) => UpdateAsync(id, projectId, patch, cancellationToken);

    internal async Task<JobResponse> DeleteForProjectAsync(Guid id, Guid projectId, CancellationToken cancellationToken)
    {
        await using var lease = await AcquireDefinitionLockAsync(cancellationToken);
        _ = await GetForProjectAsync(id, projectId, lease.Token);
        return await DeleteAsync(id, lease.Token);
    }

    private async Task<JobResponse> UpdateAsync(
        Guid id,
        Guid? projectId,
        Func<JobResponse, JobCreateRequest> update,
        CancellationToken cancellationToken
    )
    {
        await using var lease = await AcquireDefinitionLockAsync(cancellationToken);
        var current = await GetAsync(id, lease.Token);
        if (projectId is { } expectedProjectId)
        {
            RequireProject(current, expectedProjectId);
        }
        var definition = await ResolveDefinitionAsync(update(current), lease.Token, current.ProjectId);
        return ToResponse(await _jobs.UpdateAsync(id, definition, lease.Token, expectedProjectId: current.ProjectId));
    }

    private Task<ApplicationLockScope> AcquireDefinitionLockAsync(CancellationToken cancellationToken) =>
        AgentDefinitionLock.AcquireScopeAsync(_applicationLock, _userInfo.RequiredUserId, cancellationToken);

    private async Task<EventJobDefinition> ResolveDefinitionAsync(
        JobCreateRequest request,
        CancellationToken cancellationToken,
        Guid? currentProjectId = null
    )
    {
        var schedule = request.Schedule ?? throw new AgwException(ErrorCodes.InvalidParam);
        var validTarget = Enum.IsDefined(request.TargetKind);
        if (string.IsNullOrWhiteSpace(schedule.Kind) || !validTarget || string.IsNullOrWhiteSpace(request.Instructions))
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }
        await _definitions.EnsureTargetsVisibleAsync(
            request.ProjectId,
            request.TargetKind,
            request.AgentId,
            _userInfo.RequiredUserId,
            currentProjectId
        );
        var name = await _definitions.ResolveNameAsync(request.Name, _timeProvider.GetUtcNow(), cancellationToken);
        return new EventJobDefinition
        {
            Name = name,
            Enabled = request.Enabled,
            ProjectId = request.ProjectId,
            TargetKind = (EventJobTargetKind)request.TargetKind,
            AgentId = request.AgentId,
            Instructions = request.Instructions,
            Schedule = new EventJobSchedule
            {
                Kind = schedule.Kind,
                At = schedule.At,
                Interval = schedule.Interval,
                Cron = schedule.Cron,
            },
        };
    }

    private static void RequireProject(JobResponse job, Guid projectId)
    {
        if (job.ProjectId != projectId)
        {
            throw new AgwException(ErrorCodes.JobNotFound);
        }
    }

    private static JobResponse ToResponse(EventJob job)
    {
        var definition = job.Definition;
        var schedule = definition.Schedule;
        return new JobResponse
        {
            Id = job.Id,
            Name = definition.Name,
            Enabled = definition.Enabled,
            ProjectId = definition.ProjectId,
            TargetKind = (AgentRuntimeType)definition.TargetKind,
            AgentId = definition.AgentId,
            Instructions = definition.Instructions,
            Schedule = new JobScheduleResponse
            {
                Kind = schedule.Kind,
                At = schedule.At,
                Interval = schedule.Interval,
                Cron = schedule.Cron,
            },
            DueAt = job.DueAt,
            ScheduleCompleted = job.ScheduleCompleted,
            LastDelivery = job.LastDelivery is { } delivery
                ? new JobDeliveryResponse
                {
                    Id = delivery.Id,
                    State = delivery.State,
                    Attempt = delivery.Attempt,
                    LastError = delivery.LastError,
                }
                : null,
        };
    }
}
