using System.Reflection;
using Agw.Files.Abstracts;
using Agw.Files.Application.Files;
using Agw.Files.Application.Storage.Local;
using Agw.Files.Application.Storage.Resolver;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Files.Tests;

public class DependencyInjectionTests
{
    [Fact]
    public async Task AddFiles_RegistersFileAppService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFiles(new ConfigurationBuilder().Build());

        await using var serviceProvider = services.BuildServiceProvider();

        Assert.NotNull(serviceProvider.GetService<FileAppService>());
    }

    [Fact]
    public void ProjectScopedFileSystemResolver_CacheUsesCachedEntry()
    {
        var resolverType = typeof(ProjectScopedFileSystemResolver);
        var cachedEntryType = resolverType.GetNestedType("CachedEntry", BindingFlags.NonPublic);
        var cacheField = resolverType.GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(cachedEntryType);
        Assert.NotNull(cacheField);
        Assert.Equal(cachedEntryType, cacheField.FieldType.GetGenericArguments()[1]);
        Assert.NotNull(cachedEntryType.GetProperty("FileSystem"));
        Assert.NotNull(cachedEntryType.GetProperty("CreatedAt"));
    }

    [Fact]
    public async Task AddFiles_ResolvesAndCachesLocalProjectFileSystem()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var projectId = Guid.CreateVersion7();
        var workspace = Path.Combine(Path.GetTempPath(), "agw-files-tests", Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(workspace);

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IProjectFileSystemConfigurationProvider>(
                new TestProjectFileSystemConfigurationProvider(workspace)
            );
            services.AddFiles(new ConfigurationBuilder().Build());

            await using var serviceProvider = services.BuildServiceProvider();
            var resolver = serviceProvider.GetRequiredService<IAgwFileSystemResolver>();
            var fileSystem = await resolver.ResolveAsync(projectId, cancellationToken);
            var cachedFileSystem = await resolver.ResolveAsync(projectId, cancellationToken);

            var localFileSystem = Assert.IsType<LocalFileSystem>(fileSystem);
            Assert.Same(fileSystem, cachedFileSystem);
            Assert.Equal(
                Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                localFileSystem.NormalizedRoot
            );
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_WhenWorkspaceChanges_RebuildsCachedFileSystem()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var projectId = Guid.CreateVersion7();
        var root = Path.Combine(Path.GetTempPath(), "agw-files-tests", Guid.CreateVersion7().ToString("N"));
        var firstWorkspace = Path.Combine(root, "first");
        var secondWorkspace = Path.Combine(root, "second");
        Directory.CreateDirectory(firstWorkspace);
        Directory.CreateDirectory(secondWorkspace);

        try
        {
            var configuration = new TestProjectFileSystemConfigurationProvider(firstWorkspace);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IProjectFileSystemConfigurationProvider>(configuration);
            services.AddFiles(new ConfigurationBuilder().Build());

            await using var serviceProvider = services.BuildServiceProvider();
            var resolver = serviceProvider.GetRequiredService<IAgwFileSystemResolver>();
            var first = await resolver.ResolveAsync(projectId, cancellationToken);

            configuration.Workspace = secondWorkspace;
            var second = await resolver.ResolveAsync(projectId, cancellationToken);

            Assert.NotSame(first, second);
            Assert.Equal(
                Path.GetFullPath(secondWorkspace).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                Assert.IsType<LocalFileSystem>(second).NormalizedRoot
            );
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_ProjectWithoutWorkspace_CreatesDefaultWorkspaceDirectory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var projectId = Guid.CreateVersion7();
        var projectName = $"agw-files-tests-{Guid.CreateVersion7():N}";
        var expectedWorkspace = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agw",
            "projects",
            projectId.ToString("N")
        );

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IProjectFileSystemConfigurationProvider>(
                new TestProjectFileSystemConfigurationProvider(null, projectName)
            );
            services.AddFiles(new ConfigurationBuilder().Build());

            await using var serviceProvider = services.BuildServiceProvider();
            var resolver = serviceProvider.GetRequiredService<IAgwFileSystemResolver>();

            var fileSystem = await resolver.ResolveAsync(projectId, cancellationToken);

            var localFileSystem = Assert.IsType<LocalFileSystem>(fileSystem);
            Assert.True(Directory.Exists(expectedWorkspace));
            Assert.Equal(
                Path.GetFullPath(expectedWorkspace).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                localFileSystem.NormalizedRoot
            );
        }
        finally
        {
            if (Directory.Exists(expectedWorkspace))
            {
                Directory.Delete(expectedWorkspace, recursive: true);
            }
        }
    }

    private sealed class TestProjectFileSystemConfigurationProvider : IProjectFileSystemConfigurationProvider
    {
        public string? Workspace { get; set; }
        private readonly string _projectName;

        public TestProjectFileSystemConfigurationProvider(string? workspace, string projectName = "Test Project")
        {
            Workspace = workspace;
            _projectName = projectName;
        }

        public Task<ProjectFileSystemConfiguration?> GetAsync(Guid projectId, CancellationToken cancellationToken)
        {
            return Task.FromResult<ProjectFileSystemConfiguration?>(
                new ProjectFileSystemConfiguration(_projectName, Workspace, "test-user")
            );
        }
    }
}
