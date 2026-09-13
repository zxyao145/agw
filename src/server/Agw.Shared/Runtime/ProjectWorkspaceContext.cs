namespace Agw.Shared.Runtime;

/// <summary>Flows the captured workspace through one execution and its child agents.</summary>
public static class ProjectWorkspaceContext
{
    private static readonly AsyncLocal<Entry?> Current = new();

    public static ProjectWorkspaceSnapshot? Get(Guid projectId) =>
        Current.Value is { } entry && entry.ProjectId == projectId ? entry.Snapshot : null;

    public static IDisposable Push(Guid projectId, ProjectWorkspaceSnapshot snapshot)
    {
        var previous = Current.Value;
        Current.Value = new Entry(projectId, snapshot);
        return new Scope(previous);
    }

    private sealed record Entry(Guid ProjectId, ProjectWorkspaceSnapshot Snapshot);

    private sealed class Scope : IDisposable
    {
        private readonly Entry? _previous;

        public Scope(Entry? previous)
        {
            _previous = previous;
        }

        public void Dispose() => Current.Value = _previous;
    }
}
