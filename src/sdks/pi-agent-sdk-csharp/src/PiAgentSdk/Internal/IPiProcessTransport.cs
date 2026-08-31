namespace PiAgentSdk.Internal;

internal sealed class PiProcessExitInfo
{
    public int? ExitCode { get; init; }
}

internal interface IPiProcessTransport : IAsyncDisposable
{
    string StandardErrorTail { get; }

    Task StartAsync(CancellationToken cancellationToken);

    ValueTask WriteLineAsync(string line, CancellationToken cancellationToken);

    IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken);

    Task<PiProcessExitInfo> WaitForExitAsync(CancellationToken cancellationToken);

    ValueTask KillAsync(CancellationToken cancellationToken);
}
