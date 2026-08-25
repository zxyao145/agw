namespace Agw.Agents.Contracts.Execution;

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
/// 执行运行时配置在配置文件中的位置，供无法引用实现模块的调用方共享。
/// </summary>
public static class ExecutionRuntimeConfiguration
{
    /// <summary>
    /// 配置文件中的执行运行时节名称。
    /// </summary>
    public const string SectionName = "Execution";

    /// <summary>
    /// 当前启用的执行实现配置键。
    /// </summary>
    public const string ProviderKey = SectionName + ":Provider";
}
