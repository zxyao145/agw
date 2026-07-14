using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.A2A.Extensions;

public static class A2ARoutesBuilderExtensions
{
    private const string PathPlaceholder = "{agentName}";

    /// <summary>
    /// Activity source for tracing A2A endpoint operations.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new("Agw.A2A.Endpoints", "1.0.0");

    /// <summary>
    /// Enables JSON-RPC A2A endpoints for the specified path.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder to configure.</param>
    /// <param name="agentPathPrefix">The base path for the A2A endpoints.</param>
    /// <returns>An endpoint convention builder for further configuration.</returns>
    public static IEndpointConventionBuilder MapAgwA2A(this IEndpointRouteBuilder endpoints, [StringSyntax("Route")] string agentPathPrefix)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(agentPathPrefix);

        var agentHandlerFactory = endpoints.ServiceProvider.GetRequiredService<AgentHandlerFactory>();
        var agentRoute = BuildAgentRoute(agentPathPrefix);

        var routeGroup = endpoints.MapGroup("").WithTags("A2A");
        routeGroup.MapGet("/.well-known/agents.json",
            async delegate (A2AAgentService a2aService, CancellationToken cancellationToken)
            {
                var cards = await a2aService.ListAgentCardsAsync();
                return Results.Ok(cards);
            });

        routeGroup.MapGet(
            agentRoute + "/.well-known/agent-card.json",
            async delegate (HttpRequest request, string agentName, CancellationToken cancellationToken)
            {
                var agentHandler = await agentHandlerFactory.CreateAsync(agentName);
                if (agentHandler is CommonAgentHandler commonAgentHandler)
                {
                    var agentCard = await commonAgentHandler.GetAgentCardAsync();
                    if (agentCard is null)
                    {
                        return Results.NotFound("Agent not found");
                    }
                    return Results.Ok(agentCard);
                }
                return Results.InternalServerError("AgentHandler not found");
            }
        );

        routeGroup.MapPost(agentRoute,
            async delegate (IAgwA2ARequestHandler requestHandler, HttpRequest request, string agentName, CancellationToken cancellationToken)
            {
                return await AgwA2AJsonRpcProcessor.ProcessRequestAsync(requestHandler, request, agentName, cancellationToken);
            }
        );

        return routeGroup;
    }

    private static string BuildAgentRoute(string agentPathPrefix)
    {
        var route = agentPathPrefix.Trim();
        if (route == "/")
        {
            return "/" + PathPlaceholder;
        }

        route = route.TrimEnd('/');
        return route.Contains(PathPlaceholder, StringComparison.Ordinal)
            ? route
            : route + "/" + PathPlaceholder;
    }
}
