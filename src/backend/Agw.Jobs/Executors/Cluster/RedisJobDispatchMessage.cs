using Agw.Jobs.Dtos;

namespace Agw.Jobs.Executors.Cluster;

internal sealed class RedisJobDispatchMessage
{
    public string DispatchId { get; set; } = string.Empty;

    public string ResultQueueKey { get; set; } = string.Empty;

    public InMemoryJob Job { get; set; } = new();
}
