using System.Linq.Expressions;
using Agw.Shared.Data.Entities.Projects;
using Agw.Testing;

namespace Agw.Projects.Tests;

public sealed class ProjectFileSystemConfigurationProviderTests
{
    private const string OwnerUserId = "owner";
    private static readonly Guid ProjectId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetOwnerUserIdAsync_RepeatedChecksWithinLifetime_QueriesOwnershipOnce()
    {
        var clock = new TestTimeProvider(Now);
        var userInfo = new TestUserInfoService(OwnerUserId);
        var projectAppService = new OwnershipProjectAppService(userInfo);
        var provider = CreateProvider(projectAppService, userInfo, clock);

        Assert.Equal(OwnerUserId, await provider.GetOwnerUserIdAsync(ProjectId, TestContext.Current.CancellationToken));
        clock.SetUtcNow(Now.Add(ProjectOwnershipCache.Lifetime - TimeSpan.FromMilliseconds(1)));
        Assert.Equal(OwnerUserId, await provider.GetOwnerUserIdAsync(ProjectId, TestContext.Current.CancellationToken));

        Assert.Equal(1, projectAppService.OwnerLookupCount);
    }

    [Fact]
    public async Task GetOwnerUserIdAsync_AfterLifetime_QueriesOwnershipAgain()
    {
        var clock = new TestTimeProvider(Now);
        var userInfo = new TestUserInfoService(OwnerUserId);
        var projectAppService = new OwnershipProjectAppService(userInfo);
        var provider = CreateProvider(projectAppService, userInfo, clock);

        await provider.GetOwnerUserIdAsync(ProjectId, TestContext.Current.CancellationToken);
        clock.SetUtcNow(Now.Add(ProjectOwnershipCache.Lifetime));
        await provider.GetOwnerUserIdAsync(ProjectId, TestContext.Current.CancellationToken);

        Assert.Equal(2, projectAppService.OwnerLookupCount);
    }

    [Fact]
    public async Task GetOwnerUserIdAsync_OtherUser_DoesNotReuseVerifiedOwner()
    {
        var clock = new TestTimeProvider(Now);
        var userInfo = new TestUserInfoService(OwnerUserId);
        var projectAppService = new OwnershipProjectAppService(userInfo);
        var provider = CreateProvider(projectAppService, userInfo, clock);

        Assert.Equal(OwnerUserId, await provider.GetOwnerUserIdAsync(ProjectId, TestContext.Current.CancellationToken));
        userInfo.UserId = "intruder";
        Assert.Null(await provider.GetOwnerUserIdAsync(ProjectId, TestContext.Current.CancellationToken));

        Assert.Equal(2, projectAppService.OwnerLookupCount);
    }

    [Fact]
    public async Task GetOwnerUserIdAsync_RejectedOwnership_IsNotCached()
    {
        var clock = new TestTimeProvider(Now);
        var userInfo = new TestUserInfoService("intruder");
        var projectAppService = new OwnershipProjectAppService(userInfo);
        var provider = CreateProvider(projectAppService, userInfo, clock);

        Assert.Null(await provider.GetOwnerUserIdAsync(ProjectId, TestContext.Current.CancellationToken));
        Assert.Null(await provider.GetOwnerUserIdAsync(ProjectId, TestContext.Current.CancellationToken));

        Assert.Equal(2, projectAppService.OwnerLookupCount);
    }

    private static ProjectFileSystemConfigurationProvider CreateProvider(
        IProjectAppService projectAppService,
        IUserInfoService userInfoService,
        TimeProvider timeProvider
    ) => new(projectAppService, userInfoService, TestProjectOwnershipCache.Create(timeProvider));

    private sealed class OwnershipProjectAppService : IProjectAppService
    {
        private readonly IUserInfoService _userInfoService;

        public OwnershipProjectAppService(IUserInfoService userInfoService)
        {
            _userInfoService = userInfoService;
        }

        public int OwnerLookupCount { get; private set; }

        public Task<string?> GetOwnerUserIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            OwnerLookupCount++;
            return Task.FromResult<string?>(
                id == ProjectId && _userInfoService.UserId == OwnerUserId ? OwnerUserId : null
            );
        }

        public Task<IReadOnlyList<Project>> ListAsync(Expression<Func<Project, bool>>? predicate = null) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Project>> ListForCurrentUserAsync() => throw new NotSupportedException();

        public Task<string?> GetProjectExtraSettingAsync(Guid? projectId) => throw new NotSupportedException();

        public Task<Guid?> ResolveProjectIdAsync(Guid? projectId) => throw new NotSupportedException();

        public Task<Project?> CreateAsync(
            Project project,
            IEnumerable<Guid>? mcpToolServerIds,
            IEnumerable<Guid>? skillIds,
            IEnumerable<Guid>? connectionIds
        ) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id) => throw new NotSupportedException();

        public Task<Project?> GetAsync(Guid id) => throw new NotSupportedException();

        public Task<Project?> GetForCurrentUserAsync(Guid id) => throw new NotSupportedException();

        public Task<Project?> UpdateAsync(
            Guid id,
            Action<Project> updateAction,
            IEnumerable<Guid>? mcpToolServerIds,
            IEnumerable<Guid>? skillIds,
            IEnumerable<Guid>? connectionIds
        ) => throw new NotSupportedException();
    }
}
