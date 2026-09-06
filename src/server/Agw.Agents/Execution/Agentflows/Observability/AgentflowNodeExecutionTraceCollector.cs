using System.Diagnostics;
using System.Threading.Channels;
using Agw.Agents.Application.Persistence;
using Agw.Shared.Data.Entities.Agentflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agentflows.Observability;

internal interface IAgentflowNodeExecutionTraceStore
{
    Task SaveAsync(AgentflowTrace trace, CancellationToken cancellationToken);
}

internal sealed class AgentflowNodeExecutionTraceStore : IAgentflowNodeExecutionTraceStore
{
    private readonly IServiceScopeFactory _scopeFactory;

    public AgentflowNodeExecutionTraceStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task SaveAsync(AgentflowTrace trace, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IAgentsDbContext>();
        await dbContext.AgentflowNodeExecutionTraces.AddAsync(trace, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

internal sealed class AgentflowNodeExecutionTraceCollector : BackgroundService
{
    private const int QueueCapacity = 8192;

    private readonly IAgentflowNodeExecutionTraceStore _store;
    private readonly ILogger<AgentflowNodeExecutionTraceCollector> _logger;
    private readonly Channel<AgentflowTrace> _channel;
    private readonly ActivityListener _listener;

    public AgentflowNodeExecutionTraceCollector(
        IAgentflowNodeExecutionTraceStore store,
        ILogger<AgentflowNodeExecutionTraceCollector> logger
    )
    {
        _store = store;
        _logger = logger;
        _channel = Channel.CreateBounded<AgentflowTrace>(
            new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            }
        );
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentflowNodeExecutionActivity.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = OnActivityStopped,
        };
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        ActivitySource.AddActivityListener(_listener);
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _listener.Dispose();
        _channel.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _listener.Dispose();
        _channel.Writer.TryComplete();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var trace in _channel.Reader.ReadAllAsync())
        {
            try
            {
                await _store.SaveAsync(trace, stoppingToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Failed to persist agentflow node execution trace for {AgentflowId}/{NodeId}",
                    trace.AgentflowId,
                    trace.NodeId
                );
            }
        }
    }

    private void OnActivityStopped(Activity activity)
    {
        if (!AgentflowNodeExecutionActivity.TryCreateTrace(activity, out var trace) || trace == null)
        {
            return;
        }

        if (!_channel.Writer.TryWrite(trace))
        {
            _logger.LogWarning(
                "Agentflow node execution trace queue is full; dropping {AgentflowId}/{NodeId}",
                trace.AgentflowId,
                trace.NodeId
            );
        }
    }
}
