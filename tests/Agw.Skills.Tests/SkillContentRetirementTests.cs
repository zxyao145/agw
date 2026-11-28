using System.Security.Claims;
using Agw.Agents.Definitions.Facades;
using Agw.Auth.Application;
using Agw.Auth.Contracts;
using Agw.Infrastructure.Configuration;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Interceptors;
using Agw.Infrastructure.Skills;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Runtime;
using Agw.Skills.Application;
using Agw.Skills.Application.Content;
using Agw.Skills.Contracts;
using Agw.Skills.Domain.Services;
using Agw.Skills.Infrastructure;
using Agw.Skills.Infrastructure.Content;
using Agw.Skills.Infrastructure.Remote;
using Medallion.Threading.Postgres;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static Agw.Skills.Tests.SkillTestArchives;

namespace Agw.Skills.Tests;

public class SkillContentRetirementTests
{
    [Fact]
    public async Task UpdateAsync_NoLease_DeletesOldDirectory()
    {
        using var userScope = PushTestUser();
        await using var fixture = await CreateFixtureAsync(TestContext.Current.CancellationToken);
        var created = await fixture.CreateSkillAsync();
        var oldDirectory = Path.Combine(fixture.Paths.Root, created.Skill.ContentPath);

        await fixture.Service.UpdateAsync(
            created.Skill.Id,
            "test-skill",
            "description",
            CreateArchive("Updated instructions"),
            fixture.User.RequiredUserId,
            cancellationToken: fixture.CancellationToken
        );

        Assert.False(Directory.Exists(oldDirectory));
        Assert.False(File.Exists(oldDirectory + ".retired"));
    }

    [Fact]
    public async Task UpdateAsync_LeaseHeld_KeepsOldDirectoryUntilReturned()
    {
        using var userScope = PushTestUser();
        await using var fixture = await CreateFixtureAsync(TestContext.Current.CancellationToken);
        var created = await fixture.CreateSkillAsync();
        var lease = Assert.IsAssignableFrom<ILocalSkillDirectoryLease>(
            await fixture.Service.AcquireLocalDirectoryAsync(created.Skill.Id, fixture.CancellationToken)
        );
        var oldDirectory = lease.Path;

        await fixture.Service.UpdateAsync(
            created.Skill.Id,
            "test-skill",
            "description",
            CreateArchive("Updated instructions"),
            fixture.User.RequiredUserId,
            cancellationToken: fixture.CancellationToken
        );

        Assert.Equal(
            "---\nname: test-skill\ndescription: description\n---\nInstructions",
            (await File.ReadAllTextAsync(Path.Combine(oldDirectory, "SKILL.md"), fixture.CancellationToken)).Replace(
                "\r\n",
                "\n"
            )
        );
        Assert.True(File.Exists(oldDirectory + ".retired"));

        await lease.DisposeAsync();

        Assert.False(Directory.Exists(oldDirectory));
        Assert.False(File.Exists(oldDirectory + ".retired"));
    }

    [Fact]
    public async Task UpdateAsync_TwoLeases_LastReturnDeletes()
    {
        using var userScope = PushTestUser();
        await using var fixture = await CreateFixtureAsync(TestContext.Current.CancellationToken);
        var created = await fixture.CreateSkillAsync();
        var firstLease = Assert.IsAssignableFrom<ILocalSkillDirectoryLease>(
            await fixture.Service.AcquireLocalDirectoryAsync(created.Skill.Id, fixture.CancellationToken)
        );
        var secondLease = Assert.IsAssignableFrom<ILocalSkillDirectoryLease>(
            await fixture.Service.AcquireLocalDirectoryAsync(created.Skill.Id, fixture.CancellationToken)
        );
        var oldDirectory = firstLease.Path;

        await fixture.Service.UpdateAsync(
            created.Skill.Id,
            "test-skill",
            "description",
            CreateArchive("Updated instructions"),
            fixture.User.RequiredUserId,
            cancellationToken: fixture.CancellationToken
        );

        await firstLease.DisposeAsync();
        Assert.True(Directory.Exists(oldDirectory));
        Assert.True(File.Exists(oldDirectory + ".retired"));

        await secondLease.DisposeAsync();

        Assert.False(Directory.Exists(oldDirectory));
        Assert.False(File.Exists(oldDirectory + ".retired"));
    }

    [Fact]
    public async Task DeleteAsync_LeaseHeld_KeepsDirectoryUntilReturned()
    {
        using var userScope = PushTestUser();
        await using var fixture = await CreateFixtureAsync(TestContext.Current.CancellationToken);
        var created = await fixture.CreateSkillAsync();
        var lease = Assert.IsAssignableFrom<ILocalSkillDirectoryLease>(
            await fixture.Service.AcquireLocalDirectoryAsync(created.Skill.Id, fixture.CancellationToken)
        );
        var directory = lease.Path;

        fixture.Context.ChangeTracker.Clear();
        Assert.True(await fixture.Service.DeleteAsync(created.Skill.Id, fixture.CancellationToken));

        Assert.True(Directory.Exists(directory));
        Assert.True(File.Exists(directory + ".retired"));

        await lease.DisposeAsync();

        Assert.False(Directory.Exists(directory));
        Assert.False(File.Exists(directory + ".retired"));
    }

    [Fact]
    public async Task AcquireLocalDirectoryAsync_AfterUpdate_ReturnsNewDirectory()
    {
        using var userScope = PushTestUser();
        await using var fixture = await CreateFixtureAsync(TestContext.Current.CancellationToken);
        var created = await fixture.CreateSkillAsync();
        var oldDirectory = Path.Combine(fixture.Paths.Root, created.Skill.ContentPath);

        await fixture.Service.UpdateAsync(
            created.Skill.Id,
            "test-skill",
            "description",
            CreateArchive("Updated instructions"),
            fixture.User.RequiredUserId,
            cancellationToken: fixture.CancellationToken
        );

        await using var lease = Assert.IsAssignableFrom<ILocalSkillDirectoryLease>(
            await fixture.Service.AcquireLocalDirectoryAsync(created.Skill.Id, fixture.CancellationToken)
        );

        Assert.NotEqual(oldDirectory, lease.Path);
        Assert.True(Directory.Exists(lease.Path));
    }

    [Fact]
    public async Task SweepAsync_RetiredWithoutLease_DeletesDirectoryAndMarker()
    {
        using var userScope = PushTestUser();
        await using var fixture = await CreateFixtureAsync(TestContext.Current.CancellationToken);
        var created = await fixture.CreateSkillAsync();
        var directory = Path.Combine(fixture.Paths.Root, created.Skill.ContentPath);
        var marker = directory + ".retired";
        var versionDirectory = Directory.GetParent(directory)!.FullName;
        await File.WriteAllTextAsync(marker, string.Empty, fixture.CancellationToken);

        await fixture.ContentRetirement.SweepAsync(fixture.CancellationToken);

        Assert.False(Directory.Exists(directory));
        Assert.False(File.Exists(marker));
        Assert.False(Directory.Exists(versionDirectory));
    }

    [Fact]
    public async Task SweepAsync_RollingVersionLayout_DeletesSkillNamedDirectory()
    {
        using var userScope = PushTestUser();
        await using var fixture = await CreateFixtureAsync(TestContext.Current.CancellationToken);
        var versionDirectory = Path.Combine(fixture.Paths.SkillsDirectory, $"test-skill-{Guid.CreateVersion7():N}");
        var skillDirectory = Path.Combine(versionDirectory, "test-skill");
        Directory.CreateDirectory(skillDirectory);
        var marker = skillDirectory + ".retired";
        await File.WriteAllTextAsync(marker, string.Empty, fixture.CancellationToken);

        await fixture.ContentRetirement.SweepAsync(fixture.CancellationToken);

        Assert.False(Directory.Exists(skillDirectory));
        Assert.False(File.Exists(marker));
        Assert.False(Directory.Exists(versionDirectory));
    }

    [Fact]
    public async Task SweepAsync_OwnerScopedLayout_DeletesRetiredSkillAndKeepsOwnerDirectory()
    {
        using var userScope = PushTestUser();
        await using var fixture = await CreateFixtureAsync(TestContext.Current.CancellationToken);
        var ownerDirectory = Path.Combine(fixture.Paths.SkillsDirectory, "_user-" + new string('a', 64));
        var skillDirectory = Path.Combine(ownerDirectory, "test-skill");
        var liveSkillDirectory = Path.Combine(ownerDirectory, "live-skill");
        Directory.CreateDirectory(skillDirectory);
        Directory.CreateDirectory(liveSkillDirectory);
        var marker = skillDirectory + ".retired";
        await File.WriteAllTextAsync(marker, string.Empty, fixture.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(liveSkillDirectory, "SKILL.md"),
            "Live skill",
            fixture.CancellationToken
        );

        await fixture.ContentRetirement.SweepAsync(fixture.CancellationToken);

        Assert.False(Directory.Exists(skillDirectory));
        Assert.False(File.Exists(marker));
        Assert.True(Directory.Exists(ownerDirectory));
        Assert.Equal(
            "Live skill",
            await File.ReadAllTextAsync(Path.Combine(liveSkillDirectory, "SKILL.md"), fixture.CancellationToken)
        );
    }

    [Fact]
    public async Task SweepAsync_LegacyVersionDirectory_DeletesRetiredSkillDirectory()
    {
        using var userScope = PushTestUser();
        await using var fixture = await CreateFixtureAsync(TestContext.Current.CancellationToken);
        var versionDirectory = Path.Combine(
            fixture.Paths.SkillsDirectory,
            $"{Guid.CreateVersion7():N}-{Guid.CreateVersion7():N}"
        );
        var skillDirectory = Path.Combine(versionDirectory, "test-skill");
        Directory.CreateDirectory(skillDirectory);
        var marker = skillDirectory + ".retired";
        await File.WriteAllTextAsync(marker, string.Empty, fixture.CancellationToken);

        await fixture.ContentRetirement.SweepAsync(fixture.CancellationToken);

        Assert.False(Directory.Exists(skillDirectory));
        Assert.False(File.Exists(marker));
        Assert.False(Directory.Exists(versionDirectory));
    }

    [Fact]
    public async Task SweepAsync_LiveLegacySkillMatchingRollingDirectoryName_PreservesSkill()
    {
        using var userScope = PushTestUser();
        await using var fixture = await CreateFixtureAsync(TestContext.Current.CancellationToken);
        var skillDirectory = Path.Combine(fixture.Paths.SkillsDirectory, $"test-skill-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(skillDirectory);
        await File.WriteAllTextAsync(Path.Combine(skillDirectory, "SKILL.md"), "Live skill", fixture.CancellationToken);
        var marker = Path.Combine(skillDirectory, "test-skill.retired");
        await File.WriteAllTextAsync(marker, string.Empty, fixture.CancellationToken);

        await fixture.ContentRetirement.SweepAsync(fixture.CancellationToken);

        Assert.Equal(
            "Live skill",
            await File.ReadAllTextAsync(Path.Combine(skillDirectory, "SKILL.md"), fixture.CancellationToken)
        );
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public async Task SweepAsync_LiveLegacySkillMatchingLegacyWrapperName_PreservesSkill()
    {
        using var userScope = PushTestUser();
        await using var fixture = await CreateFixtureAsync(TestContext.Current.CancellationToken);
        var skillDirectory = Path.Combine(
            fixture.Paths.SkillsDirectory,
            $"{Guid.CreateVersion7():N}-{Guid.CreateVersion7():N}"
        );
        Directory.CreateDirectory(skillDirectory);
        await File.WriteAllTextAsync(Path.Combine(skillDirectory, "SKILL.md"), "Live skill", fixture.CancellationToken);
        var marker = Path.Combine(skillDirectory, "legacy-resource.retired");
        await File.WriteAllTextAsync(marker, string.Empty, fixture.CancellationToken);

        await fixture.ContentRetirement.SweepAsync(fixture.CancellationToken);

        Assert.Equal(
            "Live skill",
            await File.ReadAllTextAsync(Path.Combine(skillDirectory, "SKILL.md"), fixture.CancellationToken)
        );
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public async Task SweepAsync_RetiredWithLease_KeepsDirectory()
    {
        using var userScope = PushTestUser();
        await using var fixture = await CreateFixtureAsync(TestContext.Current.CancellationToken);
        var created = await fixture.CreateSkillAsync();
        var lease = Assert.IsAssignableFrom<ILocalSkillDirectoryLease>(
            await fixture.Service.AcquireLocalDirectoryAsync(created.Skill.Id, fixture.CancellationToken)
        );
        var marker = lease.Path + ".retired";
        await File.WriteAllTextAsync(marker, string.Empty, fixture.CancellationToken);

        await fixture.ContentRetirement.SweepAsync(fixture.CancellationToken);

        Assert.True(Directory.Exists(lease.Path));
        Assert.True(File.Exists(marker));

        await lease.DisposeAsync();

        Assert.False(Directory.Exists(lease.Path));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task RetireAsync_LegacyContentPath_KeepsSkillsRoot()
    {
        using var userScope = PushTestUser();
        await using var fixture = await CreateFixtureAsync(TestContext.Current.CancellationToken);
        var directory = Path.Combine(fixture.Paths.SkillsDirectory, "legacy-skill");
        Directory.CreateDirectory(directory);

        await fixture.ContentRetirement.RetireAsync(
            new SkillContentLocation("skills/legacy-skill", directory),
            fixture.CancellationToken
        );

        Assert.False(Directory.Exists(directory));
        Assert.False(File.Exists(directory + ".retired"));
        Assert.True(Directory.Exists(fixture.Paths.SkillsDirectory));
    }

    [Fact]
    public async Task SweepAsync_LegacyResourceRetiredFile_PreservesLiveResource()
    {
        using var userScope = PushTestUser();
        await using var fixture = await CreateFixtureAsync(TestContext.Current.CancellationToken);
        var skillDirectory = Path.Combine(fixture.Paths.SkillsDirectory, "demo");
        var resourceDirectory = Path.Combine(skillDirectory, "resource");
        Directory.CreateDirectory(resourceDirectory);
        await File.WriteAllTextAsync(Path.Combine(skillDirectory, "SKILL.md"), "Live skill", fixture.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(resourceDirectory, "keep.md"),
            "Live resource",
            fixture.CancellationToken
        );
        await File.WriteAllTextAsync(
            Path.Combine(skillDirectory, "resource.retired"),
            string.Empty,
            fixture.CancellationToken
        );

        await fixture.ContentRetirement.SweepAsync(fixture.CancellationToken);

        Assert.Equal(
            "Live skill",
            await File.ReadAllTextAsync(Path.Combine(skillDirectory, "SKILL.md"), fixture.CancellationToken)
        );
        Assert.Equal(
            "Live resource",
            await File.ReadAllTextAsync(Path.Combine(resourceDirectory, "keep.md"), fixture.CancellationToken)
        );
    }

    private static IDisposable PushTestUser() =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "skill-content-retirement-test")], "Test")
            )
        );

    private static async Task<SkillContentFixture> CreateFixtureAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(AppContext.BaseDirectory, ".skill-test-data", Guid.CreateVersion7().ToString("N"));
        var paths = AgwDataPaths.Resolve(root, root);
        paths.EnsureCreated();
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var auditUser = new AuditUserIdProvider();
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(
                new EntityCreatorInterceptor(auditUser, TimeProvider.System),
                new EntityModifierInterceptor(auditUser, TimeProvider.System)
            )
            .Options;
        var context = new AgwDbContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddHttpClient();
        var serviceProvider = services.BuildServiceProvider();
        var user = new UserInfoService();
        var applicationLock = new InMemoryApplicationLock();
        var contentRetirement = new SkillContentRetirement(
            paths,
            applicationLock,
            NullLogger<SkillContentRetirement>.Instance
        );
        var remoteSkillRefreshLock = new RemoteSkillRefreshLockRouter(
            serviceProvider.GetRequiredService<IOptionsMonitor<DatabaseSettings>>(),
            serviceProvider.GetRequiredService<IOptionsMonitor<DistributedLockSettings>>(),
            (_, connectionString) => new PostgresDistributedSynchronizationProvider(connectionString),
            NullLogger<RemoteSkillRefreshLockRouter>.Instance
        );
        var service = new SkillAppService(
            context,
            new AgentCatalogFacade(context, user),
            new SkillNameUniquenessDomainService(new SkillRepository(context, user)),
            paths,
            NullLogger<SkillAppService>.Instance,
            new RemoteSkillHttpClient(serviceProvider.GetRequiredService<IHttpClientFactory>()),
            remoteSkillRefreshLock,
            applicationLock,
            TimeProvider.System,
            user,
            contentRetirement
        );
        return new SkillContentFixture(
            root,
            paths,
            connection,
            context,
            serviceProvider,
            user,
            service,
            contentRetirement,
            cancellationToken
        );
    }

    private sealed class SkillContentFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _serviceProvider;

        public SkillContentFixture(
            string root,
            AgwDataPaths paths,
            SqliteConnection connection,
            AgwDbContext context,
            ServiceProvider serviceProvider,
            UserInfoService user,
            SkillAppService service,
            ISkillContentRetirement contentRetirement,
            CancellationToken cancellationToken
        )
        {
            _root = root;
            Paths = paths;
            _connection = connection;
            Context = context;
            _serviceProvider = serviceProvider;
            User = user;
            Service = service;
            ContentRetirement = contentRetirement;
            CancellationToken = cancellationToken;
        }

        public AgwDataPaths Paths { get; }

        public AgwDbContext Context { get; }

        public UserInfoService User { get; }

        public SkillAppService Service { get; }

        public ISkillContentRetirement ContentRetirement { get; }

        public CancellationToken CancellationToken { get; }

        public Task<SkillDetails> CreateSkillAsync() =>
            Service.CreateAsync(
                new Skill
                {
                    Name = "test-skill",
                    Description = "description",
                    Kind = SkillKind.Local,
                },
                CreateArchive(),
                User.RequiredUserId,
                cancellationToken: CancellationToken
            );

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            _serviceProvider.Dispose();
            await _connection.DisposeAsync();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class AuditUserIdProvider : IEntityAuditUserIdProvider
    {
        public string GetUserId() => UserInfoUtil.RequiredUserId;
    }
}
