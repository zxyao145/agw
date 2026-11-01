namespace Agw.Files.Abstracts;

public sealed record WorkspaceRepository
{
    public string Name { get; init; } = "";
    public string DefaultBranch { get; init; } = "";
    public string Branch { get; init; } = "";
    public bool Dirty { get; init; }
}

public sealed record FeatureRepository
{
    public string Name { get; init; } = "";
    public string Branch { get; init; } = "";
    public bool Dirty { get; init; }
    public string[] Branches { get; init; } = [];
}

public sealed record WorkspaceFeature(string Name, FeatureRepository[] Repositories);

public sealed record RepositoryWorkspaceState(WorkspaceRepository[] Repositories, WorkspaceFeature[] Features);

public sealed record RepositoryWorkspaceRequest
{
    public Guid ProjectId { get; init; }
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public string Feature { get; init; } = "";
    public string Branch { get; init; } = "";
    public bool CreateBranch { get; init; }
    public DateOnly? Date { get; init; }
    public string[] Repositories { get; init; } = [];
}

public interface IRepositoryWorkspace
{
    Task<RepositoryWorkspaceState> ReadAsync(string root, CancellationToken ct);
    Task<RepositoryWorkspaceState> ExecuteAsync(
        string root,
        string operation,
        RepositoryWorkspaceRequest request,
        CancellationToken ct
    );
}
