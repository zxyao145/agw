namespace Agw.Agents.Execution.Durable;

/// <summary>
/// 选择连接内执行或集群可恢复执行；默认值保持现有进程内行为。
/// </summary>
public enum ExecutionProvider
{
    /// <summary>
    /// 在当前服务进程内执行，断线或进程重启不会恢复等待状态。
    /// </summary>
    InProcess = 0,

    /// <summary>
    /// 使用 PostgreSQL 持久化执行状态并通过分布式锁在多个 Server 间恢复。
    /// </summary>
    Distributed = 1,
}

/// <summary>
/// 执行运行时及其共享基础设施配置。
/// </summary>
public sealed class ExecutionRuntimeOptions
{
    /// <summary>
    /// 获取配置文件中的执行运行时节名称。
    /// </summary>
    public const string SectionName = "Execution";

    /// <summary>
    /// 获取或设置当前启用的执行实现。
    /// </summary>
    public ExecutionProvider Provider { get; set; } = ExecutionProvider.InProcess;

    /// <summary>
    /// 获取或设置分布式执行协调配置。
    /// </summary>
    public DistributedExecutionOptions Distributed { get; set; } = new();
}

/// <summary>
/// PostgreSQL 分布式执行循环的协调配置。
/// </summary>
public sealed class DistributedExecutionOptions
{
    /// <summary>
    /// 获取或设置 PostgreSQL 待执行记录的轮询间隔毫秒数。
    /// </summary>
    public int WorkerPollingMilliseconds { get; set; } = 250;

    /// <summary>
    /// 获取或设置单个 Server 同时运行的最大 execution 数量。
    /// </summary>
    public int MaxConcurrentExecutions { get; set; } = 4;

    /// <summary>
    /// 获取或设置 Running 状态在允许其他 Server 尝试恢复前的静默秒数。
    /// PostgreSQL 分布式锁仍是最终排他依据，因此长任务不会因超过该时间而并发执行。
    /// </summary>
    public int RecoveryProbeSeconds { get; set; } = 30;

    /// <summary>
    /// 获取或设置竞争 execution 分布式锁时的最长等待毫秒数。
    /// </summary>
    public int LockAcquireTimeoutMilliseconds { get; set; } = 500;

    /// <summary>
    /// 获取或设置 distributed execution 的消息回放实现及其公共参数。
    /// </summary>
    public ExecutionEventStreamOptions EventStream { get; set; } = new();
}

/// <summary>
/// Distributed execution 可选的消息回放实现。
/// </summary>
public enum ExecutionEventStreamProvider
{
    /// <summary>
    /// 使用 PostgreSQL append-only 表持久化并回放消息。
    /// </summary>
    Postgres = 0,

    /// <summary>
    /// 使用 Redis Stream 持久化并回放消息。
    /// </summary>
    Redis = 1,
}

/// <summary>
/// Distributed execution 消息回放的实现选择与公共读取参数。
/// </summary>
public sealed class ExecutionEventStreamOptions
{
    /// <summary>
    /// 获取或设置消息回放实现；默认使用 PostgreSQL，因此集群部署不强制引入 Redis。
    /// </summary>
    public ExecutionEventStreamProvider Provider { get; set; } = ExecutionEventStreamProvider.Postgres;

    /// <summary>
    /// 获取或设置订阅端没有新消息时的轮询间隔毫秒数。
    /// </summary>
    public int ReadPollingMilliseconds { get; set; } = 250;

    /// <summary>
    /// 获取或设置单次读取的最大消息数。
    /// </summary>
    public int ReadBatchSize { get; set; } = 100;

    /// <summary>
    /// 获取或设置 Redis Stream 实现的专属配置。
    /// </summary>
    public RedisExecutionStreamOptions Redis { get; set; } = new();
}

/// <summary>
/// Redis Stream 消息回放实现的专属配置。
/// </summary>
public sealed class RedisExecutionStreamOptions
{
    /// <summary>
    /// 获取或设置所有 Server 共享的 Redis connection string。
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置 Redis Stream 的保留分钟数。
    /// </summary>
    public int StreamTtlMinutes { get; set; } = 1440;
}
