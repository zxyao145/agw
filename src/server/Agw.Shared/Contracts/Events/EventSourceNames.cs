using System.Text.RegularExpressions;
using Agw.Shared.Exceptions;

namespace Agw.Shared.Contracts.Events;

public static partial class EventSourceNames
{
    public const string AgwPrefix = "agw.";
    public const string JobTimerPrefix = "agw.timer.";
    public const string TimerPrefix = "timer.";
    public const string WebhookPrefix = "webhook.";
    public const string HttpPollPrefix = "http.";
    public const string ExtensionNamePrefix = "ext.";
    public const int MaxLength = 200;

    public static int MaxJobNameLength => MaxLength - JobTimerPrefix.Length;

    public static string PrefixFor(EventSourceKind kind, string managedBy) =>
        (kind, managedBy) switch
        {
            (EventSourceKind.Publisher, EventManagedBy.Agw) => AgwPrefix,
            (EventSourceKind.Publisher, _) when EventManagedBy.IsExtension(managedBy) => ExtensionNamePrefix,
            (EventSourceKind.Timer, EventManagedBy.Job) => JobTimerPrefix,
            (EventSourceKind.Timer, EventManagedBy.User) => TimerPrefix,
            (EventSourceKind.Webhook, EventManagedBy.User) => WebhookPrefix,
            (EventSourceKind.HttpPoll, EventManagedBy.User) => HttpPollPrefix,
            (EventSourceKind.Provider, EventManagedBy.User) => ExtensionNamePrefix,
            _ => throw new AgwException(ErrorCodes.InvalidParam),
        };

    public static bool IsValidSuffix(string suffix) =>
        !string.IsNullOrEmpty(suffix)
        && !suffix.Contains("..", StringComparison.Ordinal)
        && SuffixPattern().IsMatch(suffix);

    public static string ForJobTimer(string jobName) => JobTimerPrefix + jobName;

    public static string CeSource(Guid sourceId) => $"/agw/sources/{sourceId:N}";

    public static bool IsValidJobName(string jobName) => HasValidSuffix(ForJobTimer(jobName), JobTimerPrefix);

    public static void Validate(string name, EventSourceKind kind, string managedBy)
    {
        var prefix = PrefixFor(kind, managedBy);
        var unknownBuiltIn =
            kind == EventSourceKind.Publisher
            && managedBy == EventManagedBy.Agw
            && !EventBuiltInSources.All.Any(source => source.Name == name);
        if (!HasValidSuffix(name, prefix) || unknownBuiltIn)
            throw new AgwException(ErrorCodes.InvalidParam);
    }

    private static bool HasValidSuffix(string name, string prefix) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= MaxLength
        && name.StartsWith(prefix, StringComparison.Ordinal)
        && IsValidSuffix(name[prefix.Length..]);

    public static string LockKey(string userId) => $"events:source-name:{userId}";

    [GeneratedRegex("^[a-z0-9]([a-z0-9.-]*[a-z0-9])?$")]
    private static partial Regex SuffixPattern();
}
