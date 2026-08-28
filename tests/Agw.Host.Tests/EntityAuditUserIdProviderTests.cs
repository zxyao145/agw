using System.Security.Claims;
using Agw.Auth.Contracts;
using Agw.Host.Data;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Interceptors;
using Agw.Shared;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Agw.Host.Tests;

public sealed class EntityAuditUserIdProviderTests
{
    [Fact]
    public void GetUserId_AnonymousSetupSystemScope_UsesAdministratorAuditActor()
    {
        var provider = new EntityAuditUserIdProvider(new TestUserInfoService());

        using var anonymousContext = UserInfoUtil.Push(null);
        using var systemScope = UserInfoUtil.PushSystemScope();

        Assert.Equal(Constants.AdminUserId, provider.GetUserId());
    }

    [Fact]
    public async Task SaveChanges_AnonymousSetupSystemScope_AllowsSeedWrite()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var auditUserIdProvider = new EntityAuditUserIdProvider(new TestUserInfoService());
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(
                new EntityCreatorInterceptor(auditUserIdProvider, TimeProvider.System),
                new EntityModifierInterceptor(auditUserIdProvider, TimeProvider.System),
                new EntitySoftDeleteInterceptor(auditUserIdProvider, TimeProvider.System)
            )
            .Options;
        await using var context = new AgwDbContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);

        using var anonymousContext = UserInfoUtil.Push(null);
        using var systemScope = UserInfoUtil.PushSystemScope();
        context.Projects.Add(
            new Project
            {
                Id = Guid.CreateVersion7(),
                Name = "seeded-project",
                Type = ProjectType.DefaultBuiltIn,
                CreateBy = Constants.AdminUserId,
            }
        );
        await context.SaveChangesAsync(cancellationToken);

        Assert.Equal(
            Constants.AdminUserId,
            await context.Projects.Select(project => project.CreateBy).SingleAsync(cancellationToken)
        );
    }

    private sealed class TestUserInfoService : IUserInfoService
    {
        public ClaimsPrincipal? Current { get; set; }

        public string? UserId => UserInfoUtil.UserId;

        public bool IsAuthenticated => UserInfoUtil.IsAuthenticated;

        public string RequiredUserId => UserInfoUtil.RequiredUserId;
    }
}
