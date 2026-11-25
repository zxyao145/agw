using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Agw.Agents.Execution.Agents.ExternalAgents.Acp.Protocol;
using Agw.Agents.ExternalAgents.Acp;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using NonConcurrentSynchronizationContext = Microsoft.VisualStudio.Threading.NonConcurrentSynchronizationContext;

namespace Agw.Agents.Execution.Agents.ExternalAgents.Acp;

internal sealed record AcpLaunchSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string Source,
    string? Version
);

internal sealed class AcpRpcException(int code, RemoteRpcException exception) : Exception(exception.Message, exception)
{
    public int Code { get; } = code;
}

internal sealed class AcpConnection : IAsyncDisposable
{
    private static readonly TimeSpan ExitDrainTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);
    private readonly TimeProvider _timeProvider;
    private readonly Process _process;
    private readonly Task _processExit;
    private readonly OrderedRpc _rpc;
    private readonly AcpStderrBuffer _stderr;
    private readonly CancellationTokenSource _readingStderr = new();
    private readonly Task _stderrTask;
    private readonly ILogger _logger;
    private volatile bool _disconnected;
    private bool _initialized;

    private AcpConnection(
        Process process,
        AcpAgentOptions options,
        IAcpClientHandler handler,
        ILogger logger,
        TimeProvider timeProvider
    )
    {
        _timeProvider = timeProvider;
        _process = process;
        _processExit = process.WaitForExitAsync();
        _logger = logger;
        _stderr = new(options.StderrTailLines);
        _stderrTask = _stderr.ReadAsync(process.StandardError, logger, _readingStderr.Token);
        var formatter = new SystemTextJsonFormatter { JsonSerializerOptions = new(AcpJson.Options) };
        var messages = new NewLineDelimitedMessageHandler(
            process.StandardInput.BaseStream,
            process.StandardOutput.BaseStream,
            formatter
        )
        {
            NewLine = NewLineDelimitedMessageHandler.NewLineStyle.Lf,
        };
        _rpc = new OrderedRpc(messages)
        {
            SynchronizationContext = new NonConcurrentSynchronizationContext(sticky: false),
            CancelLocallyInvokedMethodsWhenConnectionIsClosed = true,
        };
        _rpc.CancellationStrategy = new AcpCancellationStrategy(_rpc);
        _rpc.AddLocalRpcTarget(
            new AcpClientMethods(handler),
            new JsonRpcTargetOptions { UseSingleObjectParameterDeserialization = true }
        );
        _rpc.Disconnected += (_, _) => _disconnected = true;
        _rpc.StartListening();
    }

    public AcpInitializeResponse Initialize { get; private set; } = null!;
    public int ProcessId => _process.Id;
    public bool IsFaulted => _disconnected || _rpc.Completion.IsCompleted || _process.HasExited;
    public string StderrTail => _stderr.Tail;

    public static async Task<AcpConnection> StartAsync(
        AcpLaunchSpec spec,
        string workingDirectory,
        IReadOnlyDictionary<string, string> userEnvironment,
        AcpAgentOptions options,
        IAcpClientHandler handler,
        ILogger logger,
        TimeProvider timeProvider,
        CancellationToken ct
    )
    {
        using var activity = AcpClientDiagnostics.ActivitySource.StartActivity("acp.client.start");
        var initialized = false;
        try
        {
            var info = await ResolveStartInfoAsync(spec, workingDirectory, userEnvironment, ct);
            var process = Process.Start(info)!;
            LogProcessStart(logger, activity, spec, info, process.Id);
            var connection = new AcpConnection(process, options, handler, logger, timeProvider);
            await connection.InitializeOrStopAsync(options.InitializeTimeout, ct);
            initialized = true;
            return connection;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not start ACP command {Command}", Path.GetFileName(spec.FileName));
            throw new AgwException(
                ErrorCodes.AcpAgentStartFailed,
                $"Could not start ACP command '{spec.FileName}': {ex.Message}",
                ex
            );
        }
        finally
        {
            var outcome = initialized ? "completed" : "failed";
            activity?.SetTag("Outcome", outcome);
            AcpClientDiagnostics.ProcessStarts.Add(
                1,
                new TagList { { "outcome", outcome }, { "source", spec.Source } }
            );
        }
    }

    private static async Task<ProcessStartInfo> ResolveStartInfoAsync(
        AcpLaunchSpec spec,
        string directory,
        IReadOnlyDictionary<string, string> userEnvironment,
        CancellationToken ct
    )
    {
        var environment = AcpProcessEnvironment.Build(
            Environment.GetEnvironmentVariables(),
            spec.Environment,
            userEnvironment
        );
        var resolved = await AcpCommandResolver.ResolveAsync(spec.FileName, environment, ct);
        if (resolved.LoginShellPath is not null)
            environment["PATH"] = resolved.LoginShellPath;
        var info = new ProcessStartInfo(resolved.Path)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in spec.Arguments)
            info.ArgumentList.Add(argument);
        info.Environment.Clear();
        foreach (var (key, value) in environment)
            info.Environment.Add(key, value);
        return info;
    }

    private static void LogProcessStart(
        ILogger logger,
        Activity? activity,
        AcpLaunchSpec spec,
        ProcessStartInfo info,
        int processId
    )
    {
        var name = Path.GetFileName(info.FileName);
        activity
            ?.SetTag("Command", name)
            .SetTag("Source", spec.Source)
            .SetTag("Version", spec.Version)
            .SetTag("ProcessId", processId);
        logger.LogInformation(
            "Started ACP process {Command} from {Source} version {Version} with PID {ProcessId}",
            name,
            spec.Source,
            spec.Version,
            processId
        );
    }

    private async Task InitializeOrStopAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await InitializeAsync(timeout, ct);
        }
        finally
        {
            if (!_initialized)
            {
                await KillAsync();
                await DisposeAsync();
            }
        }
    }

    private async Task InitializeAsync(TimeSpan timeout, CancellationToken ct)
    {
        var entryAssembly = Assembly.GetEntryAssembly();
        var version =
            entryAssembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? entryAssembly?.GetName().Version?.ToString()
            ?? "0.0.0-dev";
        Initialize = await InvokeAsync<AcpInitializeResponse>(
            "initialize",
            AcpInitializeRequest.Create(version),
            timeout,
            ct
        );
        if (Initialize is not { ProtocolVersion: 1, AgentCapabilities: not null })
            throw new AgwException(
                ErrorCodes.AcpAgentStartFailed,
                $"The ACP agent does not support protocol version 1. {StderrTail}".TrimEnd()
            );
        _initialized = true;
    }

    public async Task<T> InvokeAsync<T>(string method, object parameters, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutSource = new CancellationTokenSource(timeout, _timeProvider);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutSource.Token);
        try
        {
            return await InvokeCoreAsync<T>(method, parameters, deadline.Token);
        }
        catch (OperationCanceledException ex)
            when (!ct.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "ACP process {ProcessId} timed out on {Method}", ProcessId, method);
            var stderr = _initialized ? "" : StderrTail;
            throw new AgwException(
                ErrorCodes.AcpAgentTimeout,
                $"The ACP agent did not respond to {method} in time. {stderr}".TrimEnd(),
                ex
            );
        }
    }

    public Task<T> InvokeOrderedAsync<T>(
        string method,
        object parameters,
        CancellationToken ct,
        Action? onResponse = null
    ) => _rpc.OrderAsync(() => InvokeCoreAsync<T>(method, parameters, ct), onResponse, ct);

    private async Task<T> InvokeCoreAsync<T>(string method, object parameters, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var request = _rpc.InvokeWithParameterObjectAsync<T>(method, parameters, ct).WaitAsync(ct);
            if (await Task.WhenAny(request, _processExit) == _processExit && !request.IsCompleted)
            {
                // 子进程退出后仍可能有响应等待读取。Allow buffered responses to dispatch after process exit.
                if (await Task.WhenAny(request, Task.Delay(ExitDrainTimeout, _timeProvider, ct)) != request)
                    _rpc.Dispose();
            }
            return (await request)!;
        }
        catch (RemoteMethodNotFoundException ex)
        {
            throw RemoteError(AcpRpcErrorCodes.MethodNotFound, ex);
        }
        catch (RemoteInvocationException ex)
        {
            throw RemoteError(ex.ErrorCode, ex);
        }
        catch (Exception ex)
            when (ex
                    is ConnectionLostException
                        or ObjectDisposedException
                        or IOException
                        or RemoteSerializationException
                        or JsonException
                || IsFaulted
            )
        {
            ct.ThrowIfCancellationRequested();
            throw await ConnectionFailureAsync(ex);
        }
    }

    private AgwException RemoteError(int code, RemoteRpcException exception)
    {
        _logger.LogWarning(exception, "ACP process {ProcessId} returned RPC error {Code}", ProcessId, code);
        var rpc = new AcpRpcException(code, exception);
        if (code == AcpRpcErrorCodes.AuthRequired)
        {
            var methods =
                _initialized && Initialize.AuthMethods is { Count: > 0 } offered
                    ? $" Available methods: {AcpAuthMethod.Describe(offered)}."
                    : "";
            return new AgwException(
                ErrorCodes.AcpAgentAuthenticationRequired,
                $"The ACP agent requires authentication.{methods} Set authMethodId or configure credentials in the agent environment.",
                rpc
            );
        }
        return _initialized
            ? new AgwException(ErrorCodes.AcpAgentRequestFailed, $"ACP request failed ({code}): {rpc.Message}", rpc)
            : new AgwException(
                ErrorCodes.AcpAgentStartFailed,
                $"ACP initialize failed ({code}): {rpc.Message} {StderrTail}".TrimEnd(),
                rpc
            );
    }

    private async Task<AgwException> ConnectionFailureAsync(Exception exception)
    {
        _logger.LogWarning(exception, "ACP protocol connection failed for process {ProcessId}", ProcessId);
        if (!_processExit.IsCompleted && IsFaulted)
            await Task.WhenAny(_processExit, Task.Delay(ExitDrainTimeout, _timeProvider));
        if (_processExit.IsCompleted)
            await Task.WhenAny(_stderrTask, Task.Delay(ExitDrainTimeout, _timeProvider));
        var description = _process.HasExited
            ? $"ACP process exited with code {_process.ExitCode}."
            : "ACP protocol connection failed.";
        var code = _initialized ? ErrorCodes.AcpAgentRequestFailed : ErrorCodes.AcpAgentStartFailed;
        return new AgwException(code, $"{description} {StderrTail}".TrimEnd(), exception);
    }

    public Task NotifyAsync(string method, object parameters) =>
        _rpc.NotifyWithParameterObjectAsync(method, parameters);

    public async Task KillAsync()
    {
        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException ex) when (_process.HasExited)
            {
                _logger.LogWarning(ex, "ACP process {ProcessId} exited during shutdown", ProcessId);
            }
        }
        await _processExit;
    }

    public async ValueTask DisposeAsync()
    {
        _rpc.Dispose();
        if (await Task.WhenAny(_processExit, Task.Delay(ShutdownTimeout, _timeProvider)) != _processExit)
            await KillAsync();
        await _processExit;
        await _readingStderr.CancelAsync();
        try
        {
            await _stderrTask;
        }
        catch (OperationCanceledException ex) when (_readingStderr.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Finished reading stderr for ACP process {ProcessId}", ProcessId);
        }
        _logger.LogInformation("ACP process {ProcessId} exited with code {ExitCode}", ProcessId, _process.ExitCode);
        _readingStderr.Dispose();
        _process.Dispose();
    }

    private sealed class OrderedRpc(IJsonRpcMessageHandler messages) : JsonRpc(messages)
    {
        private readonly AsyncLocal<ResponseMarker?> _issuing = new();
        private readonly ConcurrentDictionary<RequestId, ResponseMarker> _pending = new();

        public async Task<T> OrderAsync<T>(Func<Task<T>> invoke, Action? action, CancellationToken ct)
        {
            var marker = new ResponseMarker(action);
            try
            {
                _issuing.Value = marker;
                var response = invoke();
                _issuing.Value = null;
                return await response;
            }
            finally
            {
                _issuing.Value = null;
                _pending.TryRemove(marker.Id, out _);
                if (marker.Received)
                    await marker.Completion.Task.WaitAsync(ct);
            }
        }

        protected override RequestId CreateNewRequestId()
        {
            var id = base.CreateNewRequestId();
            if (_issuing.Value is { } marker)
            {
                marker.Id = id;
                _pending[id] = marker;
            }
            return id;
        }

        protected override void OnResponseReceived(JsonRpcMessage response)
        {
            // 在接收下一条消息之前安排回调。Queue the callback before dispatching the next wire message.
            if (response is IJsonRpcMessageWithId message && _pending.TryRemove(message.RequestId, out var marker))
            {
                marker.Received = true;
                SynchronizationContext!.Post(_ => marker.Complete(), null);
            }
            base.OnResponseReceived(response);
        }

        private sealed class ResponseMarker(Action? callback)
        {
            public RequestId Id { get; set; }
            public volatile bool Received;
            public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void Complete()
            {
                try
                {
                    callback?.Invoke();
                    Completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    Completion.TrySetException(ex);
                }
            }
        }
    }
}
