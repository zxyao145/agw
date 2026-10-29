using Agw.Files.Abstracts;
using Agw.Files.Infrastructure.Storage;
using Agw.Shared.Configuration;
using Agw.Shared.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agw.Files.Tests;

internal static class TestProjectWorkspaceAccess
{
    private static readonly ServiceProvider Services = new ServiceCollection().BuildServiceProvider();
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, ".scratch", "workspace-access");

    public static IProjectWorkspaceAccess Disabled { get; } =
        new ProjectWorkspaceAccess(
            Options.Create(new ProjectWorkspaceOptions()),
            AgwDataPaths.Resolve(Path.Combine(Root, "data"), Root),
            Services.GetRequiredService<IServiceScopeFactory>()
        );
}
