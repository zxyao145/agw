using Agw.Files;
using Agw.Shared.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Tools.Tests;

internal static class ProjectWorkspaceAccessTestServices
{
    public static ServiceProvider Create()
    {
        var configuration = new ConfigurationBuilder().Build();
        var root = Path.Combine(AppContext.BaseDirectory, ".scratch", "project-workspace-access");
        var services = new ServiceCollection();
        services.AddSingleton(AgwDataPaths.Resolve(Path.Combine(root, "data"), root, Path.Combine(root, "logs")));
        services.AddFiles(configuration);
        return services.BuildServiceProvider();
    }
}
