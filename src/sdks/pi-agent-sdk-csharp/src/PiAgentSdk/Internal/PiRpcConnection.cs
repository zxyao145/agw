using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace PiAgentSdk.Internal;

internal sealed class PiRpcConnection : IAsyncDisposable
{
    private readonly IPiProcessTransport _transport;
    private readonly PiAgentOptions _options;
    private readonly Func<PiExtensionUiRequest, CancellationToken, ValueTask<PiExtensionUiResponse>>? _uiHandler;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);
    private readonly Channel<PiEvent> _events = Channel.CreateBounded<PiEvent>(
        new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        }
    );
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<Task, byte> _uiResponseTasks = new();

    private Task? _readPump;
    private Task? _exitMonitor;
    private int _started;
    private int _disposed;

    public PiRpcConnection(
        IPiProcessTransport transport,
        PiAgentOptions options,
        Func<PiExtensionUiRequest, CancellationToken, ValueTask<PiExtensionUiResponse>>? uiHandler,
        ILogger? logger
    )
    {
        _transport = transport;
        _options = options;
        _uiHandler = uiHandler;
        _logger = logger;
    }

    public ChannelReader<PiEvent> Events => _events.Reader;

    public string StandardErrorTail => _transport.StandardErrorTail;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _started) != 0)
        {
            return;
        }

        await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_started != 0)
            {
                return;
            }

            await _transport.StartAsync(cancellationToken).ConfigureAwait(false);
            _readPump = PumpOutputAsync(_lifetime.Token);
            _exitMonitor = MonitorExitAsync();
            Volatile.Write(ref _started, 1);
        }
        finally
        {
            _startLock.Release();
        }
        // Do not dispose the start gate: a StartAsync call that lost the disposal race may still need to release it.
    }

    public async Task<JsonElement?> SendCommandAsync(JsonObject command, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _started) == 0)
        {
            throw new InvalidOperationException("Pi RPC connection has not been started.");
        }

        var commandName = command["type"]?.GetValue<string>() ?? "unknown";
        var id = Guid.CreateVersion7().ToString("N");
        command["id"] = id;
        var pending = new PendingRequest(commandName);
        if (!_pending.TryAdd(id, pending))
        {
            throw new InvalidOperationException("Unable to register a Pi RPC request.");
        }

        try
        {
            var payload = command.ToJsonString(PiProtocolJson.Options);
            await _transport.WriteLineAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (_pending.TryRemove(id, out var removed))
            {
                removed.Completion.TrySetException(exception);
            }

            throw;
        }

        try
        {
            return await pending
                .Completion.Task.WaitAsync(_options.CommandTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _pending.TryRemove(id, out _);
            throw new PiCommandTimeoutException(commandName, _options.CommandTimeout);
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(id, out _);
            throw;
        }
    }

    public async ValueTask KillAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _transport.KillAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Pending commands must always complete even when the platform kill operation itself races or fails.
            FaultAll(new PiProcessExitException(null, StandardErrorTail));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _startLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            FaultAll(new ObjectDisposedException(nameof(PiRpcConnection)));
            await ObserveAsync(_readPump).ConfigureAwait(false);
            await ObserveUiResponseTasksAsync().ConfigureAwait(false);
            try
            {
                await _transport.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await ObserveAsync(_exitMonitor).ConfigureAwait(false);
                _lifetime.Dispose();
            }
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task PumpOutputAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var line in _transport.ReadLinesAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                await HandleLineAsync(line, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            FaultAll(exception);
        }
    }

    private async Task HandleLineAsync(string line, CancellationToken cancellationToken)
    {
        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(line, PiProtocolJson.Options);
        }
        catch (JsonException exception)
        {
            throw new PiProtocolException("Pi RPC emitted invalid JSON.", exception);
        }

        var type = root.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
        if (string.Equals(type, "response", StringComparison.Ordinal))
        {
            HandleResponse(root);
            return;
        }

        var evt =
            root.Deserialize<PiEvent>(PiProtocolJson.Options)
            ?? throw new PiProtocolException("Pi RPC event was empty.");
        if (evt is PiExtensionUiRequestEvent { Request.IsDialog: true } uiEvent)
        {
            TrackUiResponseTask(RespondToDialogAsync(uiEvent.Request, _lifetime.Token));
        }

        await _events.Writer.WriteAsync(evt, cancellationToken).ConfigureAwait(false);
    }

    private void HandleResponse(JsonElement root)
    {
        var id = root.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(id) || !_pending.TryRemove(id, out var pending))
        {
            return;
        }

        var success = root.TryGetProperty("success", out var successValue) && successValue.GetBoolean();
        if (success)
        {
            pending.Completion.TrySetResult(root.TryGetProperty("data", out var data) ? data.Clone() : null);
            return;
        }

        var command = root.TryGetProperty("command", out var commandValue)
            ? commandValue.GetString() ?? pending.Command
            : pending.Command;
        var error = root.TryGetProperty("error", out var errorValue)
            ? errorValue.GetString() ?? "Unknown Pi RPC error."
            : "Unknown Pi RPC error.";
        pending.Completion.TrySetException(new PiRpcException(command, error));
    }

    private async Task RespondToDialogAsync(PiExtensionUiRequest request, CancellationToken cancellationToken)
    {
        PiExtensionUiResponse response;
        try
        {
            if (_uiHandler == null)
            {
                response = PiExtensionUiResponse.Cancel(request.Id);
            }
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (request.Timeout is > 0)
                {
                    timeout.CancelAfter(TimeSpan.FromMilliseconds(request.Timeout.Value));
                }

                response = await _uiHandler(request, timeout.Token).ConfigureAwait(false);
                if (!string.Equals(response.Id, request.Id, StringComparison.Ordinal))
                {
                    response = PiExtensionUiResponse.Cancel(request.Id);
                }
            }
        }
        catch (OperationCanceledException)
        {
            response = PiExtensionUiResponse.Cancel(request.Id);
        }
        catch (Exception exception)
        {
            _logger?.LogDebug(exception, "Pi Extension UI handler failed.");
            response = PiExtensionUiResponse.Cancel(request.Id);
        }

        try
        {
            await _transport.WriteLineAsync(response.ToJson().GetRawText(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            FaultAll(exception);
        }
    }

    private async Task MonitorExitAsync()
    {
        try
        {
            var exit = await _transport.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (Volatile.Read(ref _disposed) == 0)
            {
                FaultAll(new PiProcessExitException(exit.ExitCode, StandardErrorTail));
            }
        }
        catch (Exception exception) when (Volatile.Read(ref _disposed) == 0)
        {
            FaultAll(exception);
        }
    }

    private void FaultAll(Exception exception)
    {
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var pending))
            {
                pending.Completion.TrySetException(exception);
            }
        }

        _events.Writer.TryComplete(exception);
    }

    private void TrackUiResponseTask(Task task)
    {
        _uiResponseTasks.TryAdd(task, 0);
        _ = task.ContinueWith(
            static (completed, state) =>
            {
                var tasks = (ConcurrentDictionary<Task, byte>)state!;
                tasks.TryRemove(completed, out _);
            },
            _uiResponseTasks,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    private async Task ObserveUiResponseTasksAsync()
    {
        foreach (var task in _uiResponseTasks.Keys)
        {
            await ObserveAsync(task).ConfigureAwait(false);
        }
    }

    private static async Task ObserveAsync(Task? task)
    {
        if (task == null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch { }
    }

    private sealed class PendingRequest
    {
        public PendingRequest(string command)
        {
            Command = command;
        }

        public string Command { get; }

        public TaskCompletionSource<JsonElement?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
