using Agw.Files.Abstracts;
using Agw.Files.Application.Files;

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
    public async Task AddFiles_WithoutHostTimeProvider_ResolvesProjectFileSystem()
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

            Assert.NotNull(fileSystem);
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
                new ProjectFileSystemConfiguration("Test Project", _workspace, null));
        }
    }
}
