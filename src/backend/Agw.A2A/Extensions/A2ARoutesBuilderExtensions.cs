using A2A;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

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
    /// <param name="agentPath">The base path for the A2A endpoints.</param>
    /// <returns>An endpoint convention builder for further configuration.</returns>
    public static IEndpointConventionBuilder MapAgwA2A(this IEndpointRouteBuilder endpoints, [StringSyntax("Route")] string agentPath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(agentPath);

        var loggerFactory = endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<IEndpointRouteBuilder>();

        if (!agentPath.Contains(PathPlaceholder))
        {
            if (!agentPath.EndsWith("/"))
            {
                agentPath += "/";
            }
            agentPath += PathPlaceholder;
        }
        var routeGroup = endpoints.MapGroup("");
        routeGroup.MapGet("/.well-known/agents.json",
            async delegate (A2AAgentService a2aService, CancellationToken cancellationToken)
            {
                var cards = await a2aService.ListAgentCardsAsync();
                return Results.Ok(cards);
            });

        //routeGroup.MapGet(agentPath + "/.well-known/agent-card.json",
        //    async delegate (HttpRequest request, string agentName, CancellationToken cancellationToken)
        //{
        //    var taskManager = await GetTaskManager(request);
        //    var agentUrl = $"{request.Scheme}://{request.Host}{agentPath}";
        //    var agentCard = await taskManager.OnAgentCardQuery(agentUrl, cancellationToken);
        //    return Results.Ok(agentCard);
        //});

        //routeGroup.MapPost(agentPath, async (HttpRequest request, CancellationToken cancellationToken) =>
        //    {
        //        var taskManager = await GetTaskManager(request);
        //        return await DA2AJsonRpcProcessor.ProcessRequestAsync(taskManager, request, cancellationToken);
        //    }
        //);

        return routeGroup;
    }

}
