using Agw.Jobs.Executors.Abstractions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agw.Jobs.Executors.StandAlone;

public sealed class LocalJobWorkerNode(
    IJobWorkerPool workerPool,
    IOptions<JobWorkerPoolOptions> options,
    ILogger<LocalJobWorkerNode> logger) : IJobWorkerNode
{
    private readonly IJobWorkerPool _workerPool = workerPool;
    private readonly JobWorkerPoolOptions _options = options.Value;
    private readonly ILogger<LocalJobWorkerNode> _logger = logger;
    private JobWorkerDescriptor? _worker;

    public async Task RegisterAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        _worker = new JobWorkerDescriptor(
            GetWorkerId(),
            GetNodeId(),
            "local",
            now,
            now,
            _options.MaxConcurrentJobs);

        await _workerPool.RegisterAsync(_worker, cancellationToken);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_worker == null)
        {
            await RegisterAsync(cancellationToken);
        }

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    public async Task UnregisterAsync(CancellationToken cancellationToken)
    {
        if (_worker == null)
        {
            return;
        }

        await _workerPool.UnregisterAsync(_worker.WorkerId, cancellationToken);
        _logger.LogInformation("Local job worker node {WorkerId} stopped.", _worker.WorkerId);
        _worker = null;
    }

    private string GetWorkerId()
    {
        if (!string.IsNullOrWhiteSpace(_options.WorkerId))
        {
            return _options.WorkerId;
        }

        return $"{Environment.MachineName}-{Environment.ProcessId}";
    }

    private string GetNodeId()
    {
        return string.IsNullOrWhiteSpace(_options.NodeId)
            ? Environment.MachineName
            : _options.NodeId;
    }
}
