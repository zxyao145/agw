using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Agw.Agents.Execution.Agentflows.Workflows;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agentflows.Messaging;

// These events exist only in the runner's observation stream, never in the workflow graph.
internal sealed class AgentflowInputEvent : WorkflowEvent
{
    public AgentflowInputEvent(AgwMessage message)
        : base(message)
    {
        Message = message;
    }

    public AgwMessage Message { get; }
    public TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class AgentflowInputMessages : IDisposable
{
    private readonly AgentflowAgentSessionScope _scope;
    private readonly Channel<AgentflowInputEvent> _inputs = Channel.CreateUnbounded<AgentflowInputEvent>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false }
    );
    private readonly CancellationTokenSource _stopped = new();

    public AgentflowInputMessages(AgentflowAgentSessionScope scope)
    {
        _scope = scope;
        scope.InputObserver = PublishAsync;
    }

    private async ValueTask PublishAsync(ChatMessage input, CancellationToken cancellationToken)
    {
        var snapshot = input.Clone();
        snapshot.AdditionalProperties = input.AdditionalProperties == null ? null : new(input.AdditionalProperties);
        var message = snapshot.ToAiMessage();
        if (message == null || message.Contents.Count == 0)
            return;
        var notification = new AgentflowInputEvent(message);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopped.Token);
        try
        {
            await _inputs.Writer.WriteAsync(notification, linked.Token).ConfigureAwait(false);
            // Hold this node until its input has reached the display stream. This also lets the
            // reader drain preceding workflow output before releasing the downstream response.
            await notification.Observed.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (_stopped.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The runner stopped observing. A display message arriving late must not fail its node.
        }
    }

    public async IAsyncEnumerable<WorkflowEvent> ObserveAsync(
        IAsyncEnumerable<WorkflowEvent> source,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopped.Token);
        await using var events = source.GetAsyncEnumerator(linked.Token);
        Task<bool>? next = null;
        Task<bool>? inputReady = null;
        try
        {
            while (true)
            {
                next ??= events.MoveNextAsync().AsTask();
                inputReady ??= _inputs.Reader.WaitToReadAsync(linked.Token).AsTask();
                await Task.WhenAny(next, inputReady).ConfigureAwait(false);
                // Existing workflow events precede the node waiting to publish its input.
                if (next.IsCompleted)
                {
                    if (!await next.ConfigureAwait(false))
                        break;
                    yield return events.Current;
                    next = null;
                    continue;
                }
                if (!await inputReady.ConfigureAwait(false))
                    break;
                inputReady = null;
                while (_inputs.Reader.TryRead(out var input))
                {
                    input.Observed.TrySetResult();
                    yield return input;
                }
            }
        }
        finally
        {
            await _stopped.CancelAsync().ConfigureAwait(false);
            await linked.CancelAsync().ConfigureAwait(false);
            if (next != null)
                await ((Task)next).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    public void Dispose()
    {
        _scope.InputObserver = null;
        _stopped.Cancel();
        _inputs.Writer.TryComplete();
        _stopped.Dispose();
    }
}
