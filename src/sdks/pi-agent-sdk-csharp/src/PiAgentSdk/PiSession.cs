using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PiAgentSdk.Internal;

namespace PiAgentSdk;

/// <summary>Owns one lazy Pi RPC process and its provider-side session state.</summary>
/// <remarks>
/// A session permits one active run at a time. If abort cleanup must kill the child process, the object becomes
/// faulted; persistent state can be reopened by creating a new object with <see cref="PiAgent.ResumeSession"/>.
/// </remarks>
public sealed class PiSession : IAsyncDisposable
{
    private readonly PiRpcConnection _connection;
    private readonly string? _resumeSessionId;
    private readonly TimeSpan _abortGracePeriod;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _startLock = new(1, 1);

    private int _started;
    private int _activeRun;
    private int _faulted;
    private int _disposed;

    internal PiSession(
        PiRpcConnection connection,
        PiSessionOptions options,
        string? resumeSessionId,
        TimeSpan abortGracePeriod,
        ILogger? logger
    )
    {
        _connection = connection;
        Options = options;
        _resumeSessionId = resumeSessionId;
        _abortGracePeriod = abortGracePeriod;
        _logger = logger;
    }

    /// <summary>Gets the provider-issued session identifier after startup.</summary>
    public string? Id { get; private set; }

    /// <summary>Gets the Pi JSONL session-file path reported during startup, when available.</summary>
    public string? SessionFile { get; private set; }

    /// <summary>Gets the options used to create this session.</summary>
    public PiSessionOptions Options { get; }

    /// <summary>Gets a value indicating whether this session can no longer execute runs.</summary>
    public bool IsFaulted => Volatile.Read(ref _faulted) != 0;

    /// <summary>Starts the Pi process if necessary and validates its session identity.</summary>
    /// <param name="cancellationToken">Cancels startup and the <c>get_state</c> handshake.</param>
    /// <returns>A task that completes after the validated handshake.</returns>
    /// <exception cref="PiProtocolException">Pi returns missing or mismatched session state.</exception>
    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        if (Volatile.Read(ref _started) != 0)
        {
            return;
        }

        await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started != 0)
            {
                return;
            }

            await _connection.StartAsync(cancellationToken).ConfigureAwait(false);
            var data = await _connection
                .SendCommandAsync(PiCommands.GetState(), cancellationToken)
                .ConfigureAwait(false);
            if (data is not { ValueKind: JsonValueKind.Object })
            {
                Volatile.Write(ref _faulted, 1);
                throw new PiProtocolException("Pi get_state response did not contain session state.");
            }

            var state =
                data.Value.Deserialize<PiSessionState>(PiProtocolJson.Options)
                ?? throw new PiProtocolException("Pi get_state response was empty.");
            if (string.IsNullOrWhiteSpace(state.SessionId))
            {
                Volatile.Write(ref _faulted, 1);
                throw new PiProtocolException("Pi get_state response did not include a session ID.");
            }

            if (
                _resumeSessionId != null
                && !string.Equals(_resumeSessionId, state.SessionId, StringComparison.OrdinalIgnoreCase)
            )
            {
                Volatile.Write(ref _faulted, 1);
                throw new PiProtocolException(
                    $"Pi resumed session '{state.SessionId}' instead of requested session '{_resumeSessionId}'."
                );
            }

            Id = state.SessionId;
            SessionFile = state.SessionFile;
            Volatile.Write(ref _started, 1);
        }
        catch
        {
            Volatile.Write(ref _faulted, 1);
            throw;
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>Runs a prompt and streams Pi protocol events in arrival order until <c>agent_settled</c>.</summary>
    /// <param name="prompt">The nonempty prompt text.</param>
    /// <param name="images">Optional base64 image attachments.</param>
    /// <param name="cancellationToken">Cancels the run and initiates abort-and-drain cleanup.</param>
    /// <returns>An asynchronous stream of Pi events.</returns>
    /// <exception cref="PiSessionBusyException">Another run is already active in this session.</exception>
    /// <remarks>
    /// Disposing the enumerator early follows the same abort-and-drain path as cancellation. If Pi does not settle
    /// within <see cref="PiAgentOptions.AbortGracePeriod"/>, the process tree is killed and this object is faulted.
    /// </remarks>
    public async IAsyncEnumerable<PiEvent> RunStreamingAsync(
        string prompt,
        IReadOnlyList<PiImage>? images = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ThrowIfUnavailable();
        if (Interlocked.CompareExchange(ref _activeRun, 1, 0) != 0)
        {
            throw new PiSessionBusyException();
        }

        var promptSent = false;
        var settled = false;
        try
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            promptSent = true;
            try
            {
                await _connection
                    .SendCommandAsync(PiCommands.Prompt(prompt, images), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (PiRpcException)
            {
                // Pi rejected the prompt before accepting a run, so no agent_settled event follows.
                promptSent = false;
                throw;
            }

            while (true)
            {
                var evt = await _connection.Events.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (evt is PiMarkerEvent { Type: "agent_settled" })
                {
                    settled = true;
                }

                yield return evt;
                if (settled)
                {
                    yield break;
                }
            }
        }
        finally
        {
            if (promptSent && !settled)
            {
                await AbortAndDrainAsync().ConfigureAwait(false);
            }

            Volatile.Write(ref _activeRun, 0);
        }
    }

    /// <summary>Runs a prompt to settlement and collects its authoritative messages, final text, and usage.</summary>
    /// <param name="prompt">The nonempty prompt text.</param>
    /// <param name="images">Optional base64 image attachments.</param>
    /// <param name="cancellationToken">Cancels the run and initiates abort-and-drain cleanup.</param>
    /// <returns>The collected result after Pi reaches <c>agent_settled</c>.</returns>
    public async Task<PiTurn> RunAsync(
        string prompt,
        IReadOnlyList<PiImage>? images = null,
        CancellationToken cancellationToken = default
    )
    {
        var messages = new List<PiMessage>();
        var usage = new PiUsage();
        var hasUsage = false;
        var finalResponse = string.Empty;
        PiTerminalError? terminalError = null;

        await foreach (var evt in RunStreamingAsync(prompt, images, cancellationToken).ConfigureAwait(false))
        {
            switch (evt)
            {
                case PiTurnEndEvent turnEnd:
                    if (turnEnd.Message != null)
                    {
                        messages.Add(turnEnd.Message);
                    }

                    messages.AddRange(turnEnd.ToolResults);
                    if (turnEnd.Message is PiAssistantMessage assistant)
                    {
                        finalResponse = ExtractText(assistant.Content);
                        if (assistant.Usage != null)
                        {
                            usage += assistant.Usage;
                            hasUsage = true;
                        }

                        if (string.Equals(assistant.StopReason, "error", StringComparison.OrdinalIgnoreCase))
                        {
                            terminalError = new PiTerminalError(
                                assistant.ErrorMessage ?? "Pi provider returned an error.",
                                "assistant"
                            );
                        }
                    }

                    foreach (var result in turnEnd.ToolResults.OfType<PiToolResultMessage>())
                    {
                        if (result.Usage != null)
                        {
                            usage += result.Usage;
                            hasUsage = true;
                        }
                    }

                    break;
                case PiCompactionEvent { Type: "compaction_end" } compaction:
                    if (compaction.Result?.Usage != null)
                    {
                        usage += compaction.Result.Usage;
                        hasUsage = true;
                    }

                    if (!string.IsNullOrWhiteSpace(compaction.ErrorMessage))
                    {
                        terminalError = new PiTerminalError(compaction.ErrorMessage, "compaction");
                    }

                    break;
                case PiRetryEvent { Type: "auto_retry_end", Success: false, FinalError: not null } retry:
                    terminalError = new PiTerminalError(retry.FinalError, "retry");
                    break;
            }
        }

        return new PiTurn
        {
            Messages = messages,
            FinalResponse = finalResponse,
            Usage = hasUsage ? usage : null,
            TerminalError = terminalError,
        };
    }

    /// <summary>Requests cancellation of the currently active Pi operation.</summary>
    /// <param name="cancellationToken">Cancels sending or awaiting the abort command.</param>
    /// <returns>A task representing the RPC abort command.</returns>
    public async Task AbortAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        await _connection.SendCommandAsync(PiCommands.Abort(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Terminates the owned Pi process, completes pending operations, and releases transport resources.</summary>
    /// <returns>A task representing asynchronous cleanup.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _faulted, 1);
        await _connection.DisposeAsync().ConfigureAwait(false);
        _startLock.Dispose();
    }

    private async Task AbortAndDrainAsync()
    {
        using var timeout = new CancellationTokenSource(_abortGracePeriod);
        try
        {
            // Start draining first so a full bounded event channel cannot prevent the stdout pump from parsing the
            // abort response that shares the same JSONL stream.
            var settledTask = DrainUntilSettledAsync(timeout.Token);
            var abortTask = _connection.SendCommandAsync(PiCommands.Abort(), timeout.Token);
            await Task.WhenAll(settledTask, abortTask).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger?.LogDebug(exception, "Pi run did not settle after abort; killing the process.");
            Volatile.Write(ref _faulted, 1);
            try
            {
                await _connection.KillAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception killException)
            {
                _logger?.LogDebug(killException, "Failed to kill the Pi process after abort timeout.");
            }
        }
    }

    private async Task DrainUntilSettledAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var evt = await _connection.Events.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (evt is PiMarkerEvent { Type: "agent_settled" })
            {
                return;
            }
        }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _faulted) != 0)
        {
            throw new InvalidOperationException("The Pi session is faulted and cannot be reused.");
        }
    }

    private static string ExtractText(IEnumerable<PiContent> contents) =>
        string.Concat(contents.OfType<PiTextContent>().Select(content => content.Text));
}
