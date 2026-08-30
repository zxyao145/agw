using Agw.Shared.Exceptions;
using Agw.Tools.Contracts;
using Agw.Tools.Impl.Web;
using Microsoft.Extensions.AI;

namespace Agw.Tools.ContextualTools.WebSearch;

public sealed class WebSearchContextualTool : IContextualTool
{
    public ToolInfo Descriptor { get; } =
        new()
        {
            Name = "web_search",
            DisplayName = "Web Search",
            Description = "Searches the web using a hosted provider or Agw local search.",
            Category = "Web",
            TypeName = typeof(WebSearchContextualTool).FullName!,
            Parameters = [],
        };

    public ValueTask<ToolContribution> MaterializeAsync(
        ToolDefinition definition,
        ToolMaterializationContext context,
        CancellationToken cancellationToken
    )
    {
        if (definition is not WebSearchToolDefinition)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Tool '{Descriptor.Name}' requires a {nameof(WebSearchToolDefinition)}."
            );
        }

        var contribution = new ToolContribution();
        contribution.PlanModeAllowedToolNames.Add(Descriptor.Name);

        if (context.SupportsHostedWebSearch)
        {
            contribution.Tools.Add(new HostedWebSearchTool());
            return ValueTask.FromResult(contribution);
        }

        contribution.InvocationWarnings.Add(
            Descriptor.Name,
            "Hosted web search is not supported by this provider; using local search."
        );
        contribution.Tools.Add(new WebSearchTool().ToAITool());
        return ValueTask.FromResult(contribution);
    }
}
