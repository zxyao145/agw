using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Configuration;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Turns;
using Agw.Auth.Contracts;
using Agw.Events.Contracts;
using Agw.Extensions.Contracts;
using Agw.Projects.Contracts.Execution;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Microsoft.Extensions.Options;

namespace Agw.Agents.Execution.Inbound.Facades;

internal sealed class BackgroundExecutionFacade : IBackgroundExecutionFacade
{
    private readonly AgentExecutionFacade _executions;
    private readonly IDurableAgentExecutionFacade _durableExecutions;
    private readonly IProjectTaskFacade _projectTasks;
    private readonly IBackgroundExecutionPersistence _persistence;
    private readonly IBackgroundExecutionCompletionTransaction _completion;
    private readonly IExtensionCallerAccessor _extensionCallerAccessor;
    private readonly IEventContextFacade _eventContexts;
    private readonly IUserInfoService _userInfoService;
    private readonly IApplicationLock _applicationLock;
    private readonly BackgroundExecutionHost _host;
    private readonly TurnBroadcastRegistry _broadcasts;
    private readonly ExecutionProvider _provider;
    private readonly TimeProvider _timeProvider;

    public BackgroundExecutionFacade(
        AgentExecutionFacade executions,
        IDurableAgentExecutionFacade durableExecutions,
        IProjectTaskFacade projectTasks,
        IBackgroundExecutionPersistence persistence,
        IBackgroundExecutionCompletionTransaction completion,
        IExtensionCallerAccessor extensionCallerAccessor,
        IEventContextFacade eventContexts,
        IUserInfoService userInfoService,
        IApplicationLock applicationLock,
        BackgroundExecutionHost host,
        TurnBroadcastRegistry broadcasts,
        IOptions<ExecutionRuntimeOptions> options,
        TimeProvider timeProvider
    )
    {
        _executions = executions;
        _durableExecutions = durableExecutions;
        _projectTasks = projectTasks;
        _persistence = persistence;
        _completion = completion;
        _extensionCallerAccessor = extensionCallerAccessor;
        _eventContexts = eventContexts;
        _userInfoService = userInfoService;
        _applicationLock = applicationLock;
        _host = host;
        _broadcasts = broadcasts;
        _provider = options.Value.Provider;
        _timeProvider = timeProvider;
    }

    public async Task<BackgroundExecutionReceipt> SubmitAsync(
        BackgroundExecutionRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        var caller = ResolveCaller(request.CorrelationKey);
        using var userScope = PushUser(caller.UserId);
        var requestHash = ComputeRequestHash(request, caller);
        await using var lease = await _applicationLock.AcquireAsync(GetLockName(request.TurnId), cancellationToken);
        var existing = await _persistence
            .FindAsync(request.TurnId, caller.UserId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return ExistingReceipt(existing, caller, request.CorrelationKey, requestHash, request.ParentEventId);
        }

        var target = await _executions
            .ResolveBackgroundTargetAsync(request.Target, caller.UserId, request.ToolPolicy, cancellationToken)
            .ConfigureAwait(false);
        var lineage = await ResolveLineageAsync(request.ParentEventId, request.TurnId, cancellationToken)
            .ConfigureAwait(false);
        var contextId = await ResolveContextIdAsync(request, cancellationToken).ConfigureAwait(false);
        var projectTask = await _projectTasks
            .GetOrCreateAsync(
                new StartProjectTaskRequest(
                    request.ProjectId,
                    request.TurnId,
                    null,
                    AgwMessageUtil.ExtractInputText(request.Input),
                    request.Title,
                    contextId,
                    caller.UserId,
                    ProjectTaskStatus.Running,
                    caller.RequesterExtensionId?.ToString("D")
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        var execution = new BackgroundExecution
        {
            Id = request.TurnId,
            CreateBy = caller.UserId,
            CallerKey = caller.CallerKey,
            CorrelationKey = request.CorrelationKey,
            ProjectId = request.ProjectId,
            ConversationId = projectTask.ProjectConversationId,
            TargetKind = target.Kind,
            TargetId = target.Id,
            ToolPolicy = request.ToolPolicy,
            State = AgentExecutionState.Queued,
            RequestHash = requestHash,
            ChainId = lineage.ChainId,
            ParentEventId = request.ParentEventId,
            Hops = lineage.Hops,
        };
        _persistence.Stage(execution);

        var agentRequest = new AgentExecutionRequest(
            request.TurnId,
            caller.UserId,
            target,
            projectTask,
            request.Input,
            Resume: request.ConversationId.HasValue,
            HumanInteractionPolicy: HumanInteractionPolicy.Reject,
            PermissionMode: request.PermissionMode,
            ToolPolicy: request.ToolPolicy
        );
        var accepted = await _executions.AcceptBackgroundAsync(agentRequest, cancellationToken).ConfigureAwait(false);
        await _persistence.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _host.Enqueue(accepted, caller.UserId);
        return new BackgroundExecutionReceipt(
            request.TurnId,
            projectTask.ProjectConversationId,
            AgentExecutionState.Queued
        );
    }

    public async Task<BackgroundExecutionStatus> GetAsync(Guid turnId, CancellationToken cancellationToken)
    {
        if (turnId == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "turnId is required.");
        }

        var userId = ResolveUserId();
        using var userScope = PushUser(userId);
        var execution =
            await _persistence.FindAsync(turnId, userId, cancellationToken).ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound);
        EnsureCallerOwns(execution, extensionCallerOnly: true);
        return MapStatus(execution);
    }

    public async IAsyncEnumerable<AgentExecutionEvent> SubscribeAsync(
        Guid turnId,
        string? afterCursor,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        if (turnId == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "turnId is required.");
        }

        var userId = ResolveUserId();
        using var userScope = PushUser(userId);
        var execution =
            await _persistence.FindAsync(turnId, userId, cancellationToken).ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound);
        EnsureCallerOwns(execution, extensionCallerOnly: true);

        if (_provider == ExecutionProvider.Distributed)
        {
            await foreach (
                var item in _durableExecutions
                    .SubscribeAsync(turnId, userId, afterCursor, cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                yield return item;
            }

            yield break;
        }

        var broadcast = _broadcasts.Find(turnId, userId);
        if (broadcast == null)
        {
            yield break;
        }

        var afterSequence = ParseCursor(afterCursor);
        while (true)
        {
            var entries = broadcast.ReadAfter(afterSequence, out var changed);
            foreach (var entry in entries)
            {
                afterSequence = entry.Sequence;
                yield return new AgentExecutionEvent(
                    entry.Sequence.ToString(CultureInfo.InvariantCulture),
                    entry.Message
                );
            }

            if (broadcast.IsFinished)
            {
                yield break;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<bool> CancelAsync(BackgroundExecutionCancelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (
            request.TurnId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.CorrelationKey)
            || string.IsNullOrWhiteSpace(request.Reason)
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, "turnId, correlationKey, and reason are required.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var caller = ResolveCaller(request.CorrelationKey);
        using var userScope = PushUser(caller.UserId);
        var existing = await _persistence
            .FindAsync(request.TurnId, caller.UserId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            var lineage = await ResolveLineageAsync(request.ParentEventId, request.TurnId, cancellationToken)
                .ConfigureAwait(false);
            var tombstone = new BackgroundExecution
            {
                Id = request.TurnId,
                CreateBy = caller.UserId,
                CallerKey = caller.CallerKey,
                CorrelationKey = request.CorrelationKey,
                State = AgentExecutionState.Interrupted,
                Error = request.Reason,
                CompletedAt = _timeProvider.GetUtcNow(),
                ChainId = lineage.ChainId,
                ParentEventId = request.ParentEventId,
                Hops = lineage.Hops,
            };
            if (await _completion.RegisterCancelledAsync(tombstone, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            existing = await _persistence
                .FindAsync(request.TurnId, caller.UserId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
            {
                return false;
            }
        }

        await using var lease = await _applicationLock.AcquireAsync(GetLockName(request.TurnId), cancellationToken);
        existing = await _persistence.FindAsync(request.TurnId, caller.UserId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }
        EnsureCallerOwns(execution: existing, extensionCallerOnly: true);
        if (
            !string.Equals(existing.CallerKey, caller.CallerKey, StringComparison.Ordinal)
            || !string.Equals(existing.CorrelationKey, request.CorrelationKey, StringComparison.Ordinal)
            || existing.ParentEventId != request.ParentEventId
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, "The cancellation caller does not match the execution.");
        }

        if (IsTerminal(existing.State))
        {
            return false;
        }

        if (_provider == ExecutionProvider.Distributed)
        {
            var interrupted = await _executions
                .InterruptAsync(request.TurnId, caller.UserId, request.Reason, cancellationToken)
                .ConfigureAwait(false);
            if (!interrupted)
            {
                return false;
            }

            return await _persistence
                .MarkCancellationRequestedAsync(
                    request.TurnId,
                    caller.UserId,
                    request.Reason,
                    _timeProvider.GetUtcNow(),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        if (!_host.CancelInProcess(request.TurnId))
        {
            return false;
        }

        return await _persistence
            .MarkCancellationRequestedAsync(
                request.TurnId,
                caller.UserId,
                request.Reason,
                _timeProvider.GetUtcNow(),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async Task<EventLineage> ResolveLineageAsync(
        Guid? parentEventId,
        Guid rootTurnId,
        CancellationToken cancellationToken
    )
    {
        if (!parentEventId.HasValue)
        {
            return new EventLineage(rootTurnId, 0);
        }

        var parent =
            await _eventContexts.GetAsync(parentEventId.Value, cancellationToken).ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound);
        return new EventLineage(parent.ChainId, parent.Hops);
    }

    private async Task<string> ResolveContextIdAsync(
        BackgroundExecutionRequest request,
        CancellationToken cancellationToken
    )
    {
        if (request.ConversationId is not { } conversationId)
        {
            return ContextIdUtil.GenContextId();
        }

        var contextId = await _projectTasks
            .FindContextIdAsync(request.ProjectId, conversationId, cancellationToken)
            .ConfigureAwait(false);
        return contextId ?? throw new AgwException(ErrorCodes.ResourceNotFound);
    }

    private BackgroundExecutionReceipt ExistingReceipt(
        BackgroundExecution execution,
        BackgroundExecutionCaller caller,
        string correlationKey,
        string requestHash,
        Guid? parentEventId
    )
    {
        if (
            !string.Equals(execution.CallerKey, caller.CallerKey, StringComparison.Ordinal)
            || !string.Equals(execution.CorrelationKey, correlationKey, StringComparison.Ordinal)
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, "The submission caller does not match the execution.");
        }

        if (
            execution.RequestHash != null
            && !string.Equals(execution.RequestHash, requestHash, StringComparison.Ordinal)
        )
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "The TurnId is already registered for different request content."
            );
        }
        if (execution.RequestHash == null && execution.State != AgentExecutionState.Interrupted)
        {
            throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                "The background execution record is incomplete."
            );
        }
        if (execution.RequestHash == null && execution.ParentEventId != parentEventId)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "The cancellation parent event does not match the submission."
            );
        }

        return new BackgroundExecutionReceipt(execution.Id, execution.ConversationId ?? Guid.Empty, execution.State);
    }

    private BackgroundExecutionCaller ResolveCaller(string correlationKey)
    {
        var extensionCaller = _extensionCallerAccessor.Current;
        if (extensionCaller != null)
        {
            if (extensionCaller.ExtensionId == Guid.Empty || string.IsNullOrWhiteSpace(extensionCaller.UserId))
            {
                throw new AgwException(ErrorCodes.AuthenticationRequired);
            }

            return new BackgroundExecutionCaller(
                extensionCaller.UserId.Trim(),
                $"ext:{extensionCaller.ExtensionId:D}",
                extensionCaller.ExtensionId
            );
        }

        var userId = _userInfoService.RequiredUserId;
        if (!correlationKey.StartsWith("delivery:", StringComparison.Ordinal))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "A background execution caller is required.");
        }

        return new BackgroundExecutionCaller(userId, correlationKey, null);
    }

    private string ResolveUserId()
    {
        if (_extensionCallerAccessor.Current is { } caller)
        {
            return string.IsNullOrWhiteSpace(caller.UserId)
                ? throw new AgwException(ErrorCodes.AuthenticationRequired)
                : caller.UserId.Trim();
        }

        return _userInfoService.RequiredUserId;
    }

    private void EnsureCallerOwns(BackgroundExecution execution, bool extensionCallerOnly)
    {
        var extensionCaller = _extensionCallerAccessor.Current;
        if (extensionCallerOnly && extensionCaller != null)
        {
            if (!string.Equals(execution.CallerKey, $"ext:{extensionCaller.ExtensionId:D}", StringComparison.Ordinal))
            {
                throw new AgwException(ErrorCodes.ResourceNotFound);
            }
        }
    }

    private string ComputeRequestHash(BackgroundExecutionRequest request, BackgroundExecutionCaller caller)
    {
        var canonical = JsonUtil.Serialize(
            new BackgroundExecutionRequestFingerprint
            {
                UserId = caller.UserId,
                CallerKey = caller.CallerKey,
                TurnId = request.TurnId,
                Target = request.Target,
                ProjectId = request.ProjectId,
                ConversationId = request.ConversationId,
                Title = request.Title,
                Input = request.Input,
                PermissionMode = request.PermissionMode,
                ToolPolicy = request.ToolPolicy,
                CorrelationKey = request.CorrelationKey,
                ParentEventId = request.ParentEventId,
            }
        );
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static BackgroundExecutionStatus MapStatus(BackgroundExecution execution) =>
        new(
            execution.Id,
            execution.CallerKey,
            execution.CorrelationKey,
            execution.State,
            execution.ResultText,
            execution.Error,
            execution.InputTokens,
            execution.OutputTokens
        );

    private static void ValidateRequest(BackgroundExecutionRequest request)
    {
        if (
            request.TurnId == Guid.Empty
            || request.ProjectId == Guid.Empty
            || request.Target == null
            || request.Input == null
            || string.IsNullOrWhiteSpace(request.CorrelationKey)
            || !Enum.IsDefined(request.ToolPolicy)
            || request.ConversationId == Guid.Empty
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, "The background execution request is invalid.");
        }
    }

    private static long ParseCursor(string? afterCursor)
    {
        if (afterCursor == null)
        {
            return 0;
        }

        if (
            !long.TryParse(afterCursor, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            || sequence < 0
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, "The execution cursor is invalid.");
        }

        return sequence;
    }

    private static bool IsTerminal(AgentExecutionState state) =>
        state is AgentExecutionState.Completed or AgentExecutionState.Failed or AgentExecutionState.Interrupted;

    private static string GetLockName(Guid turnId) => $"agw:background-execution:{turnId:N}";

    private static IDisposable PushUser(string userId) =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "BackgroundExecution")
            )
        );

    private sealed record BackgroundExecutionCaller(string UserId, string CallerKey, Guid? RequesterExtensionId);

    private sealed record EventLineage(Guid ChainId, int Hops);

    // 字段顺序用于稳定 RequestHash 的序列化结果。
    // Keep field order stable for RequestHash serialization.
    private sealed record BackgroundExecutionRequestFingerprint
    {
        public required string UserId { get; init; }
        public required string CallerKey { get; init; }
        public required Guid TurnId { get; init; }
        public required AgentTarget Target { get; init; }
        public required Guid ProjectId { get; init; }
        public Guid? ConversationId { get; init; }
        public required string Title { get; init; }
        public required AgwUserInput Input { get; init; }
        public required AgentExecutionPermissionMode PermissionMode { get; init; }
        public required BackgroundToolPolicy ToolPolicy { get; init; }
        public required string CorrelationKey { get; init; }
        public Guid? ParentEventId { get; init; }
    }
}
