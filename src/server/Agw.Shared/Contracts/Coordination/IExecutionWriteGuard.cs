namespace Agw.Shared.Contracts.Coordination;

/// <summary>
/// Durable 执行的写入入口：每次写入开启短事务，锁定并校验执行租约后，通过各模块的持久化接口完成写入，
/// 行锁持有到事务提交。校验失败时终止本地执行并拒绝写入。
/// The write entry of a durable execution: every write opens a short transaction, locks and checks the execution lease,
/// then writes through each module's persistence seam while holding the row lock until commit. A failed check stops local execution and rejects the write.
/// </summary>
public interface IExecutionWriteGuard
{
    /// <summary>
    /// 在租约检查事务中执行写入；services 属于该事务的作用域，模块从中取得自己的持久化接口。
    /// Runs a write inside the lease-checked transaction; services belong to that transaction's scope and modules resolve their own persistence seams from it.
    /// </summary>
    Task<T> RunAsync<T>(Func<IServiceProvider, CancellationToken, Task<T>> write, CancellationToken cancellationToken);
}
