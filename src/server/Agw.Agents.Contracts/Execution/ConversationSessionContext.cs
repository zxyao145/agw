namespace Agw.Agents.Contracts.Execution;

/// <summary>Immutable execution context captured by SDK callbacks and history providers.</summary>
public static class ConversationSessionContext
{
    private sealed record Stamp(Guid ProjectId, string ContextId, int Generation);

    private static readonly AsyncLocal<Stamp?> Current = new();

    public static bool IsBound(Guid projectId, string contextId) =>
        Current.Value is { } stamp
        && stamp.ProjectId == projectId
        && string.Equals(stamp.ContextId, contextId, StringComparison.OrdinalIgnoreCase);

    public static int GetGeneration(Guid projectId, string contextId) =>
        Current.Value is { } stamp
        && stamp.ProjectId == projectId
        && string.Equals(stamp.ContextId, contextId, StringComparison.OrdinalIgnoreCase)
            ? stamp.Generation
            : 0;

    public static IDisposable Push(Guid projectId, string contextId, int generation)
    {
        var scope = new Scope(Current.Value);
        Current.Value = new Stamp(projectId, contextId, generation);
        return scope;
    }

    private sealed class Scope : IDisposable
    {
        private readonly Stamp? _previous;

        public Scope(Stamp? previous)
        {
            _previous = previous;
        }

        public void Dispose() => Current.Value = _previous;
    }
}
