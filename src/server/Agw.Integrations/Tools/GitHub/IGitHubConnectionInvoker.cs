using Agw.Integrations.Tools.GitHub.Dtos;

namespace Agw.Integrations.Tools.GitHub;

public interface IGitHubConnectionInvoker
{
    Task<GitHubUserInfo> GetCurrentUserAsync(Guid connectionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<GitHubRepoInfo>> ListRepositoriesAsync(Guid connectionId, CancellationToken cancellationToken);

    Task<CloneResult> CloneRepositoryAsync(
        Guid connectionId,
        Guid projectId,
        string owner,
        string repository,
        string? relativePath,
        CancellationToken cancellationToken
    );
}

public interface IProjectWorkspaceResolver
{
    Task<string?> ResolveWorkspaceAsync(Guid projectId, CancellationToken cancellationToken);
}
