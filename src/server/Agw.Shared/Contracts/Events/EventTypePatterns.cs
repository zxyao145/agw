namespace Agw.Shared.Contracts.Events;

public static class EventTypePatterns
{
    public static bool IsValidPattern(string pattern) =>
        !string.IsNullOrEmpty(pattern)
        && !pattern.Any(char.IsWhiteSpace)
        && (pattern.IndexOf('*') < 0 || IsPrefixPattern(pattern));

    public static bool IsExactType(string eventType) => IsValidPattern(eventType) && !eventType.Contains('*');

    public static bool IsDeclaredExternalPattern(string pattern) => IsValidPattern(pattern) && !IsReserved(pattern);

    public static bool IsReserved(string eventType) =>
        eventType.StartsWith(EventSourceNames.AgwPrefix, StringComparison.Ordinal);

    public static bool Matches(string pattern, string eventType) =>
        pattern == "*"
        || pattern == eventType
        || pattern.EndsWith(".*", StringComparison.Ordinal)
            && eventType.StartsWith(pattern[..^1], StringComparison.Ordinal);

    public static bool MatchesAny(IEnumerable<string> patterns, string eventType) =>
        patterns.Any(pattern => Matches(pattern, eventType));

    private static bool IsPrefixPattern(string pattern) =>
        pattern.Length > 2 && pattern.EndsWith(".*", StringComparison.Ordinal) && pattern[..^1].IndexOf('*') < 0;
}
