using Agw.Shared.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agw.Projects.Tests;

public sealed class ProjectWorkspaceOptionsTests
{
    [Fact]
    public void AddProjects_BindsWorkspaceOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{ProjectWorkspaceOptions.SectionName}:IsolationEnabled"] = bool.TrueString,
                }
            )
            .Build();
        var services = new ServiceCollection();
        services.AddProjects(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<ProjectWorkspaceOptions>>().Value;

        Assert.True(options.IsolationEnabled);
    }

    [Fact]
    public void AddProjects_DefaultWorkspaceOptions_DisableIsolation()
    {
        var services = new ServiceCollection();
        services.AddProjects(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<ProjectWorkspaceOptions>>().Value;

        Assert.False(options.IsolationEnabled);
    }
}
