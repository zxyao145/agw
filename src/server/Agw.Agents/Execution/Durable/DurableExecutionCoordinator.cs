using System.Runtime.CompilerServices;
using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Commands.Exec;
using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Turns;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agw.Agents.Execution.Durable;

/// <summary>
/// 协调 PostgreSQL execution 状态、PostgreSQL 分布式锁与可替换的消息回放实现。
/// 状态数据库决定执行正确性，event stream 只负责输出回放。
/// </summary>
internal sealed class DurableExecutionCoordinator
{
    private static readonly TimeSpan StatusPollingInterval = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IApplicationLock _applicationLock;
    private readonly IExecutionEventStream _eventStream;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DurableExecutionCoordinator> _logger;
    private readonly TimeSpan _streamPollingInterval;
    private readonly AgentflowCheckpointStore? _checkpointStore;

    /// <summary>
    /// 初始化 distributed execution 的持久状态、排他锁和消息回放边界。
    /// </summary>
    public DurableExecutionCoordinator(
        IServiceScopeFactory scopeFactory,
        IApplicationLock applicationLock,
        IExecutionEventStream eventStream,
        TimeProvider timeProvider,
        IOptions<ExecutionRuntimeOptions> options,
        ILogger<DurableExecutionCoordinator> logger,
        AgentflowCheckpointStore? checkpointStore = null
    )
    {
        _scopeFactory = scopeFactory;
        _applicationLock = applicationLock;
        _eventStream = eventStream;
        _timeProvider = timeProvider;
        _logger = logger;
        _checkpointStore = checkpointStore;
        _streamPollingInterval = TimeSpan.FromMilliseconds(
            options.Value.Distributed.EventStream.ReadPollingMilliseconds
        );
    }

    /// <summary>
    /// 幂等登记启动清单与 Queued 状态；后台 worker 会在分布式锁保护下领取执行。
    /// </summary>
    public async Task StartAsync(
        Guid executionId,
        string userName,
        string userId,
        ExecCommand command,
        TaskProjection task,
        ExecutionSettings settings,
        CancellationToken cancellationToken
    )
    {
        var agentId =
            command.AgentId ?? throw new AgwException(ErrorCodes.InvalidParam, "ExecCommand.agentId is required.");
        if (!command.Stream)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Distributed execution requires ExecCommand.stream=true.");
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<DurableExecutionStore>();
        await store
            .RegisterAsync(
                executionId,
                userName,
                userId,
                agentId,
                command.AgentType,
                command.Input,
                task,
                settings,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 在 execution 分布式锁内校验 pending request，并把人工回答原子写入 PostgreSQL。
    /// </summary>
    public async Task SubmitHumanResponseAsync(
        SubmitDurableHumanResponseRequest request,
        string userName,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExecutionId == Guid.Empty || string.IsNullOrWhiteSpace(request.RequestId))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "executionId and requestId are required.");
        }
        if (request.RequestId.Trim().Length > 128)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "requestId is too long.");
        }

        await using var executionLock = await _applicationLock
            .AcquireAsync(DurableExecutionLock.GetResourceName(request.ExecutionId), cancellationToken)
            .ConfigureAwait(false);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<DurableExecutionStore>();
        await store.SubmitHumanResponseAsync(request, userName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 从 PostgreSQL 获取已鉴权 execution 的当前状态。
    /// </summary>
    public async Task<DurableExecutionStatusResponse> GetStatusAsync(
        Guid executionId,
        string userName,
        CancellationToken cancellationToken
    )
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<DurableExecutionStore>();
        var snapshot = await store.GetAuthorizedAsync(executionId, userName, cancellationToken).ConfigureAwait(false);
        return ToStatus(snapshot);
    }

    /// <summary>
    /// 获取已鉴权 execution 当前仍未收到回答的人工请求。
    /// </summary>
    public async Task<IReadOnlyList<DurableHumanInteractionSnapshot>> GetPendingAsync(
        Guid executionId,
        string userName,
        CancellationToken cancellationToken
    )
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<DurableExecutionStore>();
        var snapshot = await store.GetAuthorizedAsync(executionId, userName, cancellationToken).ConfigureAwait(false);
        return snapshot.Status == DurableExecutionStatus.WaitingForHuman ? snapshot.GetUnansweredInteractions() : [];
    }

    /// <summary>
    /// 在 PostgreSQL 中持久写入 Interrupted，并通过并发版本阻止 Running 分段覆盖该终态。
    /// </summary>
    public async Task<bool> InterruptAsync(
        Guid executionId,
        string userName,
        string? reason,
        CancellationToken cancellationToken
    )
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<DurableExecutionStore>();
        return await store.RequestInterruptAsync(executionId, userName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 等待来源 execution 完全释放分布式锁后，原子截断历史并登记新的恢复分支。
    /// </summary>
    public async Task ResumeCheckpointAsync(
        Guid occurrenceId,
        Guid resumeExecutionId,
        Guid projectId,
        string contextId,
        Guid agentflowId,
        string userName,
        CancellationToken cancellationToken
    )
    {
        var checkpointStore =
            _checkpointStore
            ?? throw new AgwException(
                ErrorCodes.DurableExecutionUnavailable,
                "Agentflow checkpoint services are not configured."
            );
        var sourceExecutionId =
            await checkpointStore
                .GetSourceExecutionIdAsync(occurrenceId, userName, cancellationToken)
                .ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.DurableExecutionNotFound);

        await using var executionLock = await _applicationLock
            .AcquireAsync(DurableExecutionLock.GetResourceName(sourceExecutionId), cancellationToken)
            .ConfigureAwait(false);
        await checkpointStore
            .PrepareDistributedResumeAsync(
                occurrenceId,
                resumeExecutionId,
                projectId,
                contextId,
                agentflowId,
                userName,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 从 cursor 开始合并 event stream 回放与 PostgreSQL pending、terminal 降级消息。
    /// </summary>
    internal async IAsyncEnumerable<ExecutionStreamEntry> ReadAsync(
        Guid executionId,
        string? afterCursor,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var cursor = afterCursor;
        var emittedInteractions = new HashSet<string>(StringComparer.Ordinal);
        var nextStatusCheck = DateTimeOffset.MinValue;
        var streamFailureReported = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            IReadOnlyList<ExecutionStreamEntry> entries;
            try
            {
                entries = await _eventStream.ReadAsync(executionId, cursor, cancellationToken).ConfigureAwait(false);
                streamFailureReported = false;
            }
            catch (AgwException exception) when (exception.Code == ErrorCodes.DurableExecutionUnavailable.Code)
            {
                if (!streamFailureReported)
                {
                    _logger.LogWarning(
                        exception,
                        "Output replay is unavailable for distributed execution {ExecutionId}; PostgreSQL status polling will continue.",
                        executionId
                    );
                    streamFailureReported = true;
                }
                entries = [];
            }

            foreach (var entry in entries)
            {
                cursor = entry.Cursor;
                yield return entry;
                if (IsTerminalMessage(entry.Message))
                {
                    yield break;
                }
            }
            if (entries.Count > 0)
            {
                continue;
            }

            var now = _timeProvider.GetUtcNow();
            if (now >= nextStatusCheck)
            {
                var snapshot = await GetSnapshotAsync(executionId, cancellationToken).ConfigureAwait(false);
                if (snapshot.Status == DurableExecutionStatus.WaitingForHuman)
                {
                    // pending 只在 checkpoint 与请求已经原子落库后合成，回答不会指向未持久化边界。
                    foreach (var interaction in snapshot.GetUnansweredInteractions())
                    {
                        if (!emittedInteractions.Add(interaction.RequestId))
                        {
                            continue;
                        }

                        yield return new ExecutionStreamEntry(
                            cursor ?? "0-0",
                            DurableHumanInteractionMapper.ToMessage(
                                interaction,
                                executionId,
                                ResolveStreamingScopeId(snapshot.Manifest)
                            )
                        );
                    }
                }
                if (IsTerminal(snapshot.Status))
                {
                    var terminalStatus = snapshot.Status switch
                    {
                        DurableExecutionStatus.Failed => "failed",
                        DurableExecutionStatus.Interrupted => "interrupted",
                        _ => "completed",
                    };
                    yield return new ExecutionStreamEntry(
                        cursor ?? "0-0",
                        TurnMessageFactory.CreateFinished(terminalStatus, executionId)
                    );
                    yield break;
                }

                nextStatusCheck = now + StatusPollingInterval;
            }

            await Task.Delay(_streamPollingInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 将 PostgreSQL 快照映射为连接层使用的最小状态响应。
    /// </summary>
    internal static DurableExecutionStatusResponse ToStatus(DurableExecutionSnapshot snapshot) =>
        new(snapshot.Manifest.ExecutionId, snapshot.Status, ResolveStreamingScopeId(snapshot.Manifest));

    /// <summary>
    /// 获取跨 Server 稳定的前端消息作用域；旧客户端未提供消息标识时退回 executionId。
    /// </summary>
    private static string ResolveStreamingScopeId(DurableExecutionManifest manifest) =>
        string.IsNullOrWhiteSpace(manifest.Input.MessageId)
            ? manifest.ExecutionId.ToString("D")
            : manifest.Input.MessageId;

    /// <summary>
    /// 在独立 DI scope 中加载 execution 快照，避免后台订阅持有 request scope DbContext。
    /// </summary>
    private async Task<DurableExecutionSnapshot> GetSnapshotAsync(Guid executionId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<DurableExecutionStore>();
        return await store.GetAsync(executionId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 判断执行状态是否已经终止。
    /// </summary>
    private static bool IsTerminal(DurableExecutionStatus status) =>
        status
            is DurableExecutionStatus.Completed
                or DurableExecutionStatus.Failed
                or DurableExecutionStatus.Interrupted;

    /// <summary>
    /// 判断回放消息是否为终止当前 turn 的控制消息。
    /// </summary>
    private static bool IsTerminalMessage(AgwMessage message) =>
        message.AdditionalProperties != null
        && message.AdditionalProperties.TryGetValue("type", out var type)
        && string.Equals(type as string, "turn-finished", StringComparison.Ordinal);
}
