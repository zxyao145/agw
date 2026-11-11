namespace Agw.Extensions.Sdk;

public enum ExtensionOutboxState
{
    Pending,
    Completed,
    Rejected,
}

public static class ExtensionOutboxKind
{
    public const string PublishEvent = "PublishEvent";
    public const string UpsertSchedule = "UpsertSchedule";
    public const string CancelSchedule = "CancelSchedule";
    public const string UpsertManagedSubscription = "UpsertManagedSubscription";
    public const string DeleteManagedSubscription = "DeleteManagedSubscription";
    public const string SubmitExecution = "SubmitExecution";
    public const string CancelExecution = "CancelExecution";
    public const string Notify = "Notify";
    public const string UpdateNotification = "UpdateNotification";
    public const string SubmitAnswer = "SubmitAnswer";

    internal static bool IsDefined(string kind) =>
        kind
            is PublishEvent
                or UpsertSchedule
                or CancelSchedule
                or UpsertManagedSubscription
                or DeleteManagedSubscription
                or SubmitExecution
                or CancelExecution
                or Notify
                or UpdateNotification
                or SubmitAnswer;
}

public sealed record ExtensionOutboxResult
{
    public required Guid Id { get; init; }

    public required string Kind { get; init; }

    public required string IdempotencyKey { get; init; }

    public required ExtensionOutboxState State { get; init; }

    public string? ResultJson { get; init; }

    public int? ErrorCode { get; init; }

    public string? Error { get; init; }
}

public interface IExtensionOutboxResultHandler
{
    /// <summary>
    /// 返回写入处理结果期间所需的租约名称。
    /// Returns the lease name to hold through the result transaction.
    /// </summary>
    string? GetResultLeaseName(IExtensionContext context) => null;

    ValueTask HandleAsync(ExtensionOutboxResult result, CancellationToken cancellationToken);
}

internal interface IExtensionOutboxWorker : IExtensionBackgroundService { }

internal interface IExtensionUserBackgroundScheduler
{
    ValueTask ExecuteAsync(
        string userId,
        Guid invocationId,
        Func<IExtensionContext, CancellationToken, ValueTask> work,
        CancellationToken cancellationToken
    );

    ValueTask<TResult> ExecuteAsync<TResult>(
        string userId,
        Guid invocationId,
        Func<IExtensionContext, CancellationToken, ValueTask<TResult>> work,
        CancellationToken cancellationToken
    );
}
