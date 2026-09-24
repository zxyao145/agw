namespace Agw.Agents.Execution.Configuration;

/// <summary>
/// 执行运行时及其共享基础设施配置。
/// </summary>
public sealed class ExecutionRuntimeOptions
{
    /// <summary>
    /// 获取配置文件中的执行运行时节名称。
    /// </summary>
    public const string SectionName = ExecutionRuntimeConfiguration.SectionName;

    /// <summary>
    /// 获取或设置当前启用的执行实现。
    /// </summary>
    public ExecutionProvider Provider { get; set; } = ExecutionProvider.InProcess;

    /// <summary>
    /// Turn 结束后回放缓冲在实例内保留的秒数。
    /// Seconds the replay buffer of a finished turn stays in the instance.
    /// </summary>
    public int TurnBroadcastRetentionSeconds { get; set; } = 300;

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
    /// Segment 租约时长秒数；租约到期后其他实例可以接管。
    /// The segment lease duration in seconds; another instance may take over once it expires.
    /// </summary>
    public int LeaseSeconds { get; set; } = 30;

    /// <summary>
    /// 续期间隔秒数，必须小于租约时长。
    /// The renewal interval in seconds, which must be shorter than the lease duration.
    /// </summary>
    public int LeaseRenewSeconds { get; set; } = 10;

    /// <summary>
    /// 获取或设置 distributed execution 的消息回放实现及其公共参数。
    /// </summary>
    public ExecutionEventStreamOptions EventStream { get; set; } = new();
}

/// <summary>
/// Distributed execution 事件的读取来源。事件总是在 PostgreSQL 中随租约检查事务提交。
/// The read source of distributed execution events. Events always commit in PostgreSQL within lease-checked transactions.
/// </summary>
public enum ExecutionEventStreamProvider
{
    /// <summary>
    /// 只从 PostgreSQL 读取已提交的事件。
    /// Reads committed events from PostgreSQL only.
    /// </summary>
    Postgres = 0,

    /// <summary>
    /// 已提交的事件同时投影到 Redis Stream，读取优先使用投影，缺少的部分从 PostgreSQL 补齐。
    /// Committed events are also projected to a Redis Stream; reads prefer the projection and fill missing parts from PostgreSQL.
    /// </summary>
    Redis = 1,
}

/// <summary>
/// Distributed execution 事件的读取来源、批量提交与读取参数。
/// The read source, batched commits and read settings of distributed execution events.
/// </summary>
public sealed class ExecutionEventStreamOptions
{
    /// <summary>
    /// 获取或设置事件读取来源；默认只使用 PostgreSQL，因此集群部署不强制引入 Redis。
    /// Gets or sets the event read source; PostgreSQL alone is the default, so cluster deployments do not require Redis.
    /// </summary>
    public ExecutionEventStreamProvider Provider { get; set; } = ExecutionEventStreamProvider.Postgres;

    /// <summary>Zero preserves immediate writes; otherwise batches start their timer at the first pending event.</summary>
    public int WriteIntervalMilliseconds { get; set; } = 250;

    public int WriteBatchSize { get; set; } = 100;

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
