using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.Outbound;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.HumanInteraction.InProcess;

/// <summary>
/// 进程内的即时交互通道：发出请求并在内存等待回答，供 Agentflow 与 External Agent 桥接使用。
/// The in-process live interaction channel: publishes a request and waits in memory for its answer, used by Agentflow and External Agent bridges.
/// </summary>
public sealed class InProcessInteractionSession : IInteractionHandler, IHumanInteractionChannel
{
    private readonly object _sync = new();
    private readonly Dictionary<string, PendingInteraction> _pending = new(StringComparer.Ordinal);
    private readonly IExecutionMessageSink _sink;
    private readonly InteractionPermissionState _permissions;
    private readonly Action<int>? _pendingCountChanged;
    private readonly bool _allowInteraction;
    private bool _closed;
    public IInteractionRequestRegistry Requests { get; } = new InteractionRequestRegistry();
    internal InteractionPermissionState PermissionState => _permissions;

    public InProcessInteractionSession(
        IExecutionMessageSink sink,
        AgwPermissionMode? permissionMode = null,
        Action<int>? pendingCountChanged = null,
        bool allowInteraction = true
    )
        : this(sink, new InteractionPermissionState(permissionMode), pendingCountChanged, allowInteraction) { }

    internal InProcessInteractionSession(
        IExecutionMessageSink sink,
        InteractionPermissionState permissions,
        Action<int>? pendingCountChanged = null,
        bool allowInteraction = true
    )
    {
        _sink = sink;
        _permissions = permissions;
        _pendingCountChanged = pendingCountChanged;
        _allowInteraction = allowInteraction;
    }

    public async ValueTask<InteractionResolution> ResolveAsync(
        InteractionRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        PendingInteraction pending;
        lock (_sync)
        {
            if (_closed)
                throw new AgwException(ErrorCodes.AgentExecutionFailed, "The interaction session has ended.");
            // 交互不可用时就地拒绝，不挂起也不推送请求；Agent 拿到结果后继续本轮。
            // Decline in place when interaction is unavailable: nothing is queued or streamed, and the Agent continues.
            if (!_allowInteraction)
                return new InteractionResolution.Resolved(InteractionRules.Decline(request));

            pending = new PendingInteraction(request, new(TaskCreationOptions.RunContinuationsAsynchronously));
            if (!_pending.TryAdd(request.InteractionId, pending))
                throw new AgwException(ErrorCodes.InvalidParam, "This interaction is already pending.");
            _pendingCountChanged?.Invoke(_pending.Count);
        }

        try
        {
            // A response can arrive while WriteAsync is still running.
            await _sink
                .WriteAsync(
                    InteractionMessageMapper.Create(request, Guid.CreateVersion7().ToString("N")),
                    cancellationToken
                )
                .ConfigureAwait(false);
            var response = await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new InteractionResolution.Resolved(
                InteractionRules.ValidateAndNormalize(request, response, _permissions.Current)
            );
        }
        finally
        {
            lock (_sync)
            {
                if (_pending.TryGetValue(request.InteractionId, out var current) && ReferenceEquals(current, pending))
                {
                    _pending.Remove(request.InteractionId);
                    _pendingCountChanged?.Invoke(_pending.Count);
                }
            }
        }
    }

    public async ValueTask<UserInputResponse> RequestAsync(
        UserInputRequest request,
        CancellationToken cancellationToken
    )
    {
        var interaction = new UserInputInteraction
        {
            InteractionId = Guid.CreateVersion7().ToString("N"),
            Prompt = request.Prompt,
            Source = request.Source,
            InputKind = request.InputKind,
            Payload = request.Payload,
        };
        var result = await ResolveAsync(interaction, cancellationToken).ConfigureAwait(false);
        return (UserInputResponse)((InteractionResolution.Resolved)result).Response;
    }

    public ValueTask<bool> TrySubmitAsync(InteractionResponse response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromResult(false);
        lock (_sync)
        {
            if (!_pending.TryGetValue(response.InteractionId, out var pending))
                return ValueTask.FromResult(false);
            var decision = InteractionRules.ValidateAndNormalize(pending.Request, response, _permissions.Current);
            _pending.Remove(response.InteractionId);
            pending.Completion.TrySetResult(decision);
            _pendingCountChanged?.Invoke(_pending.Count);
            return ValueTask.FromResult(true);
        }
    }

    public void CancelAll()
    {
        lock (_sync)
        {
            _closed = true;
            foreach (var pending in _pending.Values)
                pending.Completion.TrySetCanceled();
            _pending.Clear();
            _pendingCountChanged?.Invoke(0);
        }
    }

    private sealed record PendingInteraction(
        InteractionRequest Request,
        TaskCompletionSource<InteractionResponse> Completion
    );
}
