namespace Agw.Agents.Execution.Runtimes.Contracts;

/// <summary>
/// 启动受理回执；Accepted 不代表执行成功或已经完成。
/// </summary>
internal readonly record struct ExecutionReceipt(Guid ExecutionId, bool Accepted);
