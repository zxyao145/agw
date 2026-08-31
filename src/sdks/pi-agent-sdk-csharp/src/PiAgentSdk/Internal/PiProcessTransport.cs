using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PiAgentSdk.Internal;

internal sealed class PiProcessTransport : IPiProcessTransport
{
    private const int MaximumStandardErrorCharacters = 64 * 1024;

    private readonly PiAgentOptions _agentOptions;
    private readonly PiSessionOptions _sessionOptions;
    private readonly string? _resumeSessionId;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _stderrLock = new();
    private readonly StringBuilder _stderr = new();

    private Process? _process;
    private Task? _stderrPump;
    private int _disposed;

    public PiProcessTransport(
        PiAgentOptions agentOptions,
        PiSessionOptions sessionOptions,
        string? resumeSessionId,
        ILogger? logger
    )
    {
        _agentOptions = agentOptions;
        _sessionOptions = sessionOptions;
        _resumeSessionId = resumeSessionId;
        _logger = logger;
    }

    public string StandardErrorTail
    {
        get
        {
            lock (_stderrLock)
            {
                return _stderr.ToString();
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_process != null)
        {
            throw new InvalidOperationException("Pi process transport has already been started.");
        }

        var executable = CommandUtil.ResolvePiPath(_agentOptions.PiPathOverride);
        var arguments = PiProcessArguments.Build(_sessionOptions, _resumeSessionId);
        var startInfo = CreateStartInfo(executable, arguments);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Failed to start Pi executable '{startInfo.FileName}'.");
        }

        _process = process;
        _stderrPump = PumpStandardErrorAsync(process, _lifetime.Token);
        _logger?.LogDebug("Started Pi RPC process {ProcessId}.", process.Id);
        return Task.CompletedTask;
    }

    public async ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var payload = Encoding.UTF8.GetBytes(line + "\n");
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token
        );
        await _writeLock.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var process = GetRunningProcess();
            await process.StandardInput.BaseStream.WriteAsync(payload, linkedCancellation.Token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(linkedCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async IAsyncEnumerable<string> ReadLinesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var process = GetStartedProcess();
        await foreach (
            var line in PiJsonlReader
                .ReadLinesAsync(process.StandardOutput.BaseStream, cancellationToken: cancellationToken)
                .ConfigureAwait(false)
        )
        {
            yield return line;
        }
    }

    public async Task<PiProcessExitInfo> WaitForExitAsync(CancellationToken cancellationToken)
    {
        var process = GetStartedProcess();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (_stderrPump != null)
        {
            await _stderrPump.ConfigureAwait(false);
        }

        return new PiProcessExitInfo { ExitCode = process.ExitCode };
    }

    public async ValueTask KillAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var process = _process;
        if (process == null)
        {
            return;
        }

        KillProcessTreeIfRunning(process);
        if (!HasExited(process))
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        var process = _process;
        if (process != null)
        {
            try
            {
                KillProcessTreeIfRunning(process);
                using var timeout = new CancellationTokenSource(_agentOptions.AbortGracePeriod);
                if (!HasExited(process))
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException)
            {
                _logger?.LogDebug(exception, "Pi process did not exit cleanly during disposal.");
            }
            await _writeLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await ObserveStandardErrorPumpAsync().ConfigureAwait(false);
                process.Dispose();
            }
            finally
            {
                _writeLock.Release();
            }
        }

        _lifetime.Dispose();
        // A caller that observed the pre-dispose state may still be unwinding through Release; SemaphoreSlim has no
        // unmanaged resource until AvailableWaitHandle is requested, so leaving this private gate undisposed avoids that race.
    }

    private ProcessStartInfo CreateStartInfo(string executable, IReadOnlyList<string> arguments)
    {
        var target = PiProcessTarget.Resolve(executable, arguments, OperatingSystem.IsWindows());
        var startInfo = new ProcessStartInfo
        {
            FileName = target.FileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = string.IsNullOrWhiteSpace(_sessionOptions.WorkingDirectory)
                ? Environment.CurrentDirectory
                : _sessionOptions.WorkingDirectory,
        };

        foreach (var argument in target.ArgumentList)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        foreach (var (key, value) in PiProcessEnvironment.Build(_agentOptions, _sessionOptions))
        {
            startInfo.Environment[key] = value;
        }

        return startInfo;
    }

    private Process GetStartedProcess() =>
        _process ?? throw new InvalidOperationException("Pi process transport has not been started.");

    private Process GetRunningProcess()
    {
        var process = GetStartedProcess();
        if (process.HasExited)
        {
            throw new PiProcessExitException(process.ExitCode, StandardErrorTail);
        }

        return process;
    }

    private async Task PumpStandardErrorAsync(Process process, CancellationToken cancellationToken)
    {
        var buffer = new char[2048];
        while (true)
        {
            var read = await process.StandardError.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            lock (_stderrLock)
            {
                _stderr.Append(buffer, 0, read);
                if (_stderr.Length > MaximumStandardErrorCharacters)
                {
                    _stderr.Remove(0, _stderr.Length - MaximumStandardErrorCharacters);
                }
            }
        }
    }

    private async Task ObserveStandardErrorPumpAsync()
    {
        if (_stderrPump == null)
        {
            return;
        }

        try
        {
            await _stderrPump.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger?.LogDebug(exception, "Pi standard-error pump did not exit cleanly during disposal.");
        }
    }

    private static void KillProcessTreeIfRunning(Process process)
    {
        try
        {
            if (!HasExited(process))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (ObjectDisposedException)
        {
            // Concurrent disposal already completed the requested termination.
        }
        catch (InvalidOperationException) when (HasExited(process))
        {
            // The process exited between the HasExited check and Kill.
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
