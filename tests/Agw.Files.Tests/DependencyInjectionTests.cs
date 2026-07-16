using Agw.Files.Abstracts;
using Agw.Files.Application.Files;
using Agw.Files.Application.Storage.Local;
using Agw.Files.Application.Storage.Resolver;

using System.Reflection;

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
        var projectId = Guid.NewGuid();
        var workspace = Path.Combine(Path.GetTempPath(), "agw-files-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IProjectFileSystemConfigurationProvider>(
                new TestProjectFileSystemConfigurationProvider(workspace));
            services.AddFiles(new ConfigurationBuilder().Build());

            await using var serviceProvider = services.BuildServiceProvider();
            var resolver = serviceProvider.GetRequiredService<IAgwFileSystemResolver>();
            var fileSystem = await resolver.ResolveAsync(projectId, cancellationToken);
            var cachedFileSystem = await resolver.ResolveAsync(projectId, cancellationToken);

            var localFileSystem = Assert.IsType<LocalFileSystem>(fileSystem);
            Assert.Same(fileSystem, cachedFileSystem);
            Assert.Equal(
                Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                localFileSystem.NormalizedRoot);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private sealed class TestProjectFileSystemConfigurationProvider : IProjectFileSystemConfigurationProvider
    {
        private readonly string _workspace;

        public TestProjectFileSystemConfigurationProvider(string workspace)
        {
            _workspace = workspace;
        }

        public Task<ProjectFileSystemConfiguration?> GetAsync(
            Guid projectId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<ProjectFileSystemConfiguration?>(
                new ProjectFileSystemConfiguration("Test Project", _workspace));
        }
    }
}
