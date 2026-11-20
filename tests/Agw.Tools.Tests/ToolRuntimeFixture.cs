using Agw.Extensions;
using Agw.Extensions.Contracts;
using Agw.Extensions.Infrastructure.Packages;
using Agw.Shared.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agw.Tools.Tests;

public sealed class ToolRuntimeFixture : IAsyncLifetime
{
    private string _root = null!;
    private ServiceProvider _services = null!;

    public IExtensionGenerationCatalog Catalog { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        _root = Path.Combine(AppContext.BaseDirectory, ".scratch", "tool-runtime", Guid.CreateVersion7().ToString("N"));
        string sourceDirectory = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceDirectory);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                ApplicationName = typeof(ToolRuntimeFixture).Assembly.GetName().Name,
                ContentRootPath = _root,
            }
        );
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Extensions:Directory"] = sourceDirectory }
        );
        builder.Services.AddSingleton<IHostEnvironment>(builder.Environment);
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton(
            AgwDataPaths.Resolve(Path.Combine(_root, "data"), _root, Path.Combine(_root, "logs"))
        );
        builder.Services.AddExtensions(builder.Configuration, "Standalone");
        _services = builder.Services.BuildServiceProvider();

        await _services.GetRequiredService<ExtensionPackageCatalog>().InitializeAsync(CancellationToken.None);
        Catalog = _services.GetRequiredService<IExtensionGenerationCatalog>();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
