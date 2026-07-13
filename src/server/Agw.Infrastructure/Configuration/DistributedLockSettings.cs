namespace Agw.Infrastructure.Configuration;

public sealed class DistributedLockSettings
{
    public const string SectionName = "DistributedLock";

    public DistributedLockProvider? Provider { get; set; }
    public string? ConnectionString { get; set; }
}
