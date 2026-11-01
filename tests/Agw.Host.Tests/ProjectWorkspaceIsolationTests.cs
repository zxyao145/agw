using System.Security.Claims;
using Agw.Agents;
using Agw.Auth.Contracts;
using Agw.Auth.Extensions;
using Agw.Files;
using Agw.Files.Abstracts;
using Agw.Host.Data;
using Agw.Infrastructure;
using Agw.Infrastructure.Data;
using Agw.Integrations.Extensions;
using Agw.Jobs;
using Agw.Projects;
using Agw.Projects.Application;
using Agw.Providers;
using Agw.Settings;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Skills;
using Agw.Tools;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agw.Host.Tests;

public sealed class ProjectWorkspaceIsolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateAsync_SameNameAcrossUsers_UsesConfiguredDefaultAndEnforcesProjectOwnership(
        bool isolationEnabled
    )
    {
        await using var fixture = await WorkspaceFixture.CreateAsync(isolationEnabled);
        var first = await fixture.CreateProjectAsync("10001", new Project { Name = "Shared Project" });
        var second = await fixture.CreateProjectAsync("10002", new Project { Name = "Shared Project" });

        Assert.Equal("Shared_Project", first.Name);
        Assert.Equal("10001", first.CreateBy);
        Assert.Equal("10002", second.CreateBy);
        Assert.Equal(
            isolationEnabled ? fixture.ManagedPath(first.Id) : Path.Combine(fixture.Home, ".agw", "Shared_Project"),
            first.Workspace
        );
        Assert.Equal(
            isolationEnabled ? fixture.ManagedPath(second.Id) : Path.Combine(fixture.Home, ".agw", "Shared_Project"),
            second.Workspace
        );
        Assert.True(Directory.Exists(first.Workspace));
        Assert.True(Directory.Exists(second.Workspace));

        var resolver = fixture.Services.GetRequiredService<IAgwFileSystemResolver>();
        using (WorkspaceFixture.PushUser("10001"))
        {
            var fileSystem = await resolver.ResolveAsync(first.Id, TestContext.Current.CancellationToken);
            Assert.NotNull(fileSystem);
            await fileSystem.WriteAllTextAsync("owner.txt", "first", TestContext.Current.CancellationToken);
        }
        using (WorkspaceFixture.PushUser("10002"))
        {
            Assert.Null(await resolver.ResolveAsync(first.Id, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task CreateAsync_ForeignAndUnknownAdditionalRoots_RejectsBeforeCreatingAnyDirectory()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync(true);
        var foreign = await fixture.CreateProjectAsync("10002", new Project { Name = "foreign" });
        var candidates = new[] { foreign.Id, Guid.CreateVersion7() };
        foreach (var targetId in candidates)
        {
            var target = Path.Combine(fixture.ManagedPath(targetId), "new-directory");
            var project = new Project
            {
                Name = "attempt",
                AdditionalDirectories = [new ProjectDirectory { Path = target }],
            };

            var exception = await Assert.ThrowsAsync<AgwException>(() => fixture.CreateProjectAsync("10001", project));

            Assert.Equal(ErrorCodes.ResourceNotFound.Code, exception.Code);
            Assert.False(Directory.Exists(target));
            Assert.False(Directory.Exists(fixture.ManagedPath(project.Id)));
            using var user = WorkspaceFixture.PushUser("10001");
            using var scope = fixture.Services.CreateScope();
            Assert.False(
                await scope
                    .ServiceProvider.GetRequiredService<AgwDbContext>()
                    .Projects.AnyAsync(item => item.Id == project.Id, TestContext.Current.CancellationToken)
            );
        }
    }

    [Fact]
    public async Task CreateAsync_SameOwnerManagedRoot_AllowsSharedDirectory()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync(true);
        var owner = await fixture.CreateProjectAsync("10001", new Project { Name = "owner" });
        var shared = Path.Combine(owner.Workspace!, "shared");

        var created = await fixture.CreateProjectAsync("10001", new Project { Name = "consumer", Workspace = shared });

        Assert.Equal(shared, created.Workspace);
        Assert.True(Directory.Exists(shared));
        using var user = WorkspaceFixture.PushUser("10001");
        var fileSystem = await fixture
            .Services.GetRequiredService<IAgwFileSystemResolver>()
            .ResolveAsync(created.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(fileSystem);
        await fileSystem.WriteAllTextAsync("shared.txt", "content", TestContext.Current.CancellationToken);
        Assert.Equal(
            "content",
            await File.ReadAllTextAsync(Path.Combine(shared, "shared.txt"), TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task UpdateAsync_ForeignWorkspace_RejectsWithoutPersistingChanges()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync(true);
        var own = await fixture.CreateProjectAsync("10001", new Project { Name = "own" });
        var foreign = await fixture.CreateProjectAsync("10002", new Project { Name = "foreign" });
        var target = Path.Combine(foreign.Workspace!, "new-directory");
        using var user = WorkspaceFixture.PushUser("10001");
        using (var scope = fixture.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IProjectAppService>();
            var exception = await Assert.ThrowsAsync<AgwException>(() =>
                service.UpdateAsync(own.Id, project => project.Workspace = target, null, null, null)
            );
            Assert.Equal(ErrorCodes.ResourceNotFound.Code, exception.Code);
        }
        using var verificationScope = fixture.Services.CreateScope();
        var persisted = await verificationScope
            .ServiceProvider.GetRequiredService<IProjectAppService>()
            .GetAsync(own.Id);
        Assert.Equal(own.Workspace, persisted!.Workspace);
        Assert.False(Directory.Exists(target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateAsync_CustomPathOutsideManagedRoot_PreservesPath(bool isolationEnabled)
    {
        await using var fixture = await WorkspaceFixture.CreateAsync(isolationEnabled);
        var custom = Path.Combine(fixture.Root, "custom", "workspace");

        var created = await fixture.CreateProjectAsync(
            "10001",
            new Project { Name = "custom", Workspace = $"  {custom}  " }
        );

        Assert.Equal(custom, created.Workspace);
        Assert.True(Directory.Exists(custom));
    }

    [Fact]
    public async Task CreateAsync_IsolationDisabled_AllowsCustomManagedPath()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync(false);
        var custom = fixture.ManagedPath(Guid.CreateVersion7());

        var created = await fixture.CreateProjectAsync("10001", new Project { Name = "custom", Workspace = custom });

        Assert.Equal(custom, created.Workspace);
        Assert.True(Directory.Exists(custom));
    }

    [Fact]
    public async Task CreateAsync_SymlinkToForeignProject_RejectsPhysicalTarget()
    {
        if (OperatingSystem.IsWindows())
            return;
        await using var fixture = await WorkspaceFixture.CreateAsync(true);
        var foreign = await fixture.CreateProjectAsync("10002", new Project { Name = "foreign" });
        var alias = Path.Combine(fixture.Root, "alias");
        Directory.CreateSymbolicLink(alias, foreign.Workspace!);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.CreateProjectAsync(
                "10001",
                new Project { Name = "alias", Workspace = Path.Combine(alias, "new-directory") }
            )
        );

        Assert.Equal(ErrorCodes.ResourceNotFound.Code, exception.Code);
        Assert.False(Directory.Exists(Path.Combine(foreign.Workspace!, "new-directory")));
    }

    private sealed class WorkspaceFixture : IAsyncDisposable
    {
        public string Root { get; } =
            Path.Combine(
                AppContext.BaseDirectory,
                ".scratch",
                "workspace-isolation",
                Guid.CreateVersion7().ToString("N")
            );
        public string Home => Path.Combine(Root, "home");
        public ServiceProvider Services { get; private set; } = null!;

        public static async Task<WorkspaceFixture> CreateAsync(bool isolationEnabled)
        {
            var fixture = new WorkspaceFixture();
            var paths = AgwDataPaths.Resolve(Path.Combine(fixture.Root, "data"), fixture.Home);
            paths.EnsureCreated();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Database:Provider"] = "sqlite",
                        ["Database:ConnectionString"] = new SqliteConnectionStringBuilder
                        {
                            DataSource = paths.DatabaseFile,
                            Pooling = false,
                        }.ToString(),
                        ["Projects:Workspace:IsolationEnabled"] = isolationEnabled.ToString(),
                    }
                )
                .Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton(paths);
            services.AddSingleton(TimeProvider.System);
            services.AddScoped<IEntityAuditUserIdProvider, EntityAuditUserIdProvider>();
            services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(paths.KeysDirectory));
            services
                .AddTools(configuration)
                .AddAgents(configuration)
                .AddFiles(configuration)
                .AddInfrastructure(configuration)
                .AddJobs(configuration, new Agw.Jobs.DependencyInjection.RegistrationOptions(AddScheduler: false))
                .AddProviders(configuration)
                .AddSkills(configuration)
                .AddProjects(configuration)
                .AddAuth()
                .AddSettings()
                .AddIntegrations(configuration);
            fixture.Services = services.BuildServiceProvider();
            using var scope = fixture.Services.CreateScope();
            await scope
                .ServiceProvider.GetRequiredService<AgwDbContext>()
                .Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return fixture;
        }

        public string ManagedPath(Guid projectId) => Path.Combine(Home, ".agw", "projects", projectId.ToString("N"));

        public async Task<Project> CreateProjectAsync(string userId, Project project)
        {
            using var user = PushUser(userId);
            using var scope = Services.CreateScope();
            var created = await scope
                .ServiceProvider.GetRequiredService<IProjectAppService>()
                .CreateAsync(project, null, null, null);
            Assert.NotNull(created);
            return created;
        }

        public static IDisposable PushUser(string userId) =>
            UserInfoUtil.Push(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "Test"))
            );

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }
    }
}
