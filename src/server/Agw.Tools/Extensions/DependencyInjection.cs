using Agw.Domain.Services;
using Agw.Tools.ContextualTools;
using Agw.Tools.ContextualTools.Shell;
using Agw.Tools.ContextualTools.WebSearch;
using Agw.Tools.ToolBlocks;
using Agw.Tools.ToolBlocks.Blocks.BackgroundAgents;
using Agw.Tools.ToolBlocks.Blocks.FileAccess;
using Agw.Tools.ToolBlocks.Blocks.Mode;
using Agw.Tools.ToolBlocks.Blocks.ProjectMemory;
using Agw.Tools.ToolBlocks.Blocks.Todo;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Tools;

public static class DependencyInjection
{
    public static IServiceCollection AddTools(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IToolBlock, TodoToolBlock>();
        services.AddSingleton<IToolBlock, ModeToolBlock>();
        services.AddSingleton<IToolBlock, ProjectMemoryToolBlock>();
        services.AddSingleton<IToolBlock, FileAccessToolBlock>();
        services.AddSingleton<IToolBlock, BackgroundAgentsToolBlock>();
        services.AddSingleton<ToolBlockRegistry>();
        services.AddSingleton<IContextualTool, WebSearchContextualTool>();
        services.AddSingleton<IContextualTool, ShellContextualTool>();
        services.AddSingleton(serviceProvider =>
        {
            var registry = new ToolRegistryService(
                serviceProvider.GetRequiredService<ILogger<ToolRegistryService>>(),
                serviceProvider,
                serviceProvider.GetServices<IContextualTool>(),
                serviceProvider.GetRequiredService<ToolBlockRegistry>());
            registry.ValidateDefinitionCoverage();
            return registry;
        });
        return services;
    }
}
