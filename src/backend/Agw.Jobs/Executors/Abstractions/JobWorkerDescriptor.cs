namespace Agw.Jobs.Executors.Abstractions;

public sealed record JobWorkerDescriptor(
    string WorkerId,
    string NodeId,
    string QueueName,
    DateTimeOffset RegisteredAt,
    DateTimeOffset LastSeenAt,
    int MaxConcurrentJobs);
