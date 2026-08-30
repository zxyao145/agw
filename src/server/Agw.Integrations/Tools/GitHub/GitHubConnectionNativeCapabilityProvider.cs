using Agw.Integrations.Application.Capabilities;
using Agw.Integrations.Tools.GitHub.Dtos;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Integrations.Tools.GitHub;

public sealed class GitHubConnectionNativeCapabilityProvider : IConnectionNativeCapabilityProvider
{
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public GitHubConnectionNativeCapabilityProvider(IServiceScopeFactory serviceScopeFactory)
    {
        _serviceScopeFactory = serviceScopeFactory;
    }

    public string Provider => "github";

    public IReadOnlyList<AITool> CreateTools(ConnectionNativeCapabilityContext context)
    {
        var target = new GitHubConnectionToolTarget(_serviceScopeFactory, context.ConnectionId, context.ProjectId);
        return
        [
            AIFunctionFactory.Create(
                (Func<CancellationToken, Task<GitHubUserInfo>>)target.GetCurrentUserAsync,
                new AIFunctionFactoryOptions
                {
                    Name = $"{context.Alias}__current_user",
                    Description = "Get the current GitHub account profile for this integration.",
                }
            ),
            AIFunctionFactory.Create(
                (Func<CancellationToken, Task<IReadOnlyList<GitHubRepoInfo>>>)target.ListRepositoriesAsync,
                new AIFunctionFactoryOptions
                {
                    Name = $"{context.Alias}__list_repositories",
                    Description = "List repositories visible to this GitHub integration.",
                }
            ),
            AIFunctionFactory.Create(
                (Func<string, string, string?, CancellationToken, Task<CloneResult>>)target.CloneRepositoryAsync,
                new AIFunctionFactoryOptions
                {
                    Name = $"{context.Alias}__clone_repository",
                    Description = "Clone a GitHub repository into the current project workspace.",
                }
            ),
        ];
    }

    private sealed class GitHubConnectionToolTarget
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly Guid _connectionId;
        private readonly Guid _projectId;

        public GitHubConnectionToolTarget(IServiceScopeFactory serviceScopeFactory, Guid connectionId, Guid projectId)
        {
            _serviceScopeFactory = serviceScopeFactory;
            _connectionId = connectionId;
            _projectId = projectId;
        }

        public async Task<GitHubUserInfo> GetCurrentUserAsync(CancellationToken cancellationToken)
        {
            await using var scope = _serviceScopeFactory.CreateAsyncScope();
            var invoker = scope.ServiceProvider.GetRequiredService<IGitHubConnectionInvoker>();
            return await invoker.GetCurrentUserAsync(_connectionId, cancellationToken);
        }

        public async Task<IReadOnlyList<GitHubRepoInfo>> ListRepositoriesAsync(CancellationToken cancellationToken)
        {
            await using var scope = _serviceScopeFactory.CreateAsyncScope();
            var invoker = scope.ServiceProvider.GetRequiredService<IGitHubConnectionInvoker>();
            return await invoker.ListRepositoriesAsync(_connectionId, cancellationToken);
        }

        public async Task<CloneResult> CloneRepositoryAsync(
            [Description("Repository owner.")] string owner,
            [Description("Repository name, without a URL.")] string repository,
            [Description("Optional relative destination below the project workspace.")] string? relativePath,
            CancellationToken cancellationToken
        )
        {
            await using var scope = _serviceScopeFactory.CreateAsyncScope();
            var invoker = scope.ServiceProvider.GetRequiredService<IGitHubConnectionInvoker>();
            return await invoker.CloneRepositoryAsync(
                _connectionId,
                _projectId,
                owner,
                repository,
                relativePath,
                cancellationToken
            );
        }
    }
}
