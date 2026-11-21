using Agw.Shared.Exceptions;

namespace Agw.Shared.Contracts.Events;

public sealed record EventBuiltInSource(string Name, string CeSource, IReadOnlyList<string> EventTypes);

public static class EventBuiltInSources
{
    public const string Execution = "agw.execution";
    public const string Notifications = "agw.notifications";
    public const string ExecutionCeSource = "/agw/executions";
    public const string NotificationsCeSource = "/agw/notifications";
    public const string ExecutionCompletedType = "agw.execution.completed";
    public const string NotificationAnsweredType = "agw.notification.answered";

    public static IReadOnlyList<EventBuiltInSource> All { get; } =
    [
        new(Execution, ExecutionCeSource, [ExecutionCompletedType]),
        new(Notifications, NotificationsCeSource, [NotificationAnsweredType]),
    ];

    public static EventBuiltInSource Get(string name) =>
        All.SingleOrDefault(source => source.Name == name) ?? throw new AgwException(ErrorCodes.InvalidParam);
}
