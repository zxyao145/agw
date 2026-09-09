using Agw.Shared.Exceptions;
using Agw.Tools.Contracts.WebSearch;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.ContextualTools.WebSearch;

[Description("Searches the web using a hosted provider or Agw local search.")]
[DisplayName("Web Search")]
public sealed class WebSearchContextualTool : IContextualTool
{
    public string Name => "web_search";

    public string Category => "Web";

    public bool AllowInPlanMode => true;

    public AgwToolPermission RequiredPermission => AgwToolPermission.ReadOnly;

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
                $"Tool '{Name}' requires a {nameof(WebSearchToolDefinition)}."
            );
        }

        var contribution = new ToolContribution();
        contribution.PlanModeAllowedToolNames.Add(Name);

        if (context.SupportsHostedWebSearch)
        {
            contribution.Tools.Add(new HostedWebSearchTool());
            return ValueTask.FromResult(contribution);
        }

        contribution.InvocationWarnings.Add(
            Name,
            "Hosted web search is not supported by this provider; using local search."
        );
        Func<WebSearchToolParams, WebSearchResult> execute = new LocalWebSearchExecutor().Execute;
        contribution.Tools.Add(AgwAIFunctionFactory.CreateParameterObjectFunction(execute, Name));
        return ValueTask.FromResult(contribution);
    }
}
