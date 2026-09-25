namespace Agw.Agents.Execution.Runtimes.Contracts;

/// <summary>
/// 启动受理回执；Accepted 不代表执行成功或已经完成。
/// The start receipt; Accepted means neither success nor completion.
/// </summary>
internal readonly record struct ExecutionReceipt(Guid TurnId, bool Accepted);
