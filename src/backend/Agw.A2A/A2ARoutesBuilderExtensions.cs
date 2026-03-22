using A2A;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Agw.A2A;

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

        routeGroup.MapGet(agentPath + "/.well-known/agent-card.json",
            async delegate (HttpRequest request, string agentName, CancellationToken cancellationToken)
        {
            ITaskManager taskManager = await GetTaskManager(request);
            var agentUrl = $"{request.Scheme}://{request.Host}{agentPath}";
            var agentCard = await taskManager.OnAgentCardQuery(agentUrl, cancellationToken);
            return Results.Ok(agentCard);
        });

        routeGroup.MapPost(agentPath, async (HttpRequest request, CancellationToken cancellationToken) =>
            {
                ITaskManager taskManager = await GetTaskManager(request);
                return await DA2AJsonRpcProcessor.ProcessRequestAsync(taskManager, request, cancellationToken);
            }
        );

        return routeGroup;
    }

    private static async Task<ITaskManager> GetTaskManager(HttpRequest request)
    {
        var sp = request.HttpContext.RequestServices;
        var taskFactory = sp.GetRequiredService<TaskManagerFactory>();
        var taskManager = await taskFactory.GetTaskManager(request);
        return taskManager;
    }

    /// <summary>
    /// Enables the well-known agent card endpoint for agent discovery.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder to configure.</param>
    /// <param name="taskManager">The task manager for handling A2A operations.</param>
    /// <param name="agentPath">The base path where the A2A agent is hosted.</param>
    /// <returns>An endpoint convention builder for further configuration.</returns>
    public static IEndpointConventionBuilder MapAgwWellKnownAgentCard(this IEndpointRouteBuilder endpoints, ITaskManager taskManager, [StringSyntax("Route")] string agentPath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(taskManager);
        ArgumentException.ThrowIfNullOrEmpty(agentPath);

        var routeGroup = endpoints.MapGroup("");

        routeGroup.MapGet(".well-known/{agentName}/agent-card.json", async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            var agentUrl = $"{request.Scheme}://{request.Host}{agentPath}";
            var agentCard = await taskManager.OnAgentCardQuery(agentUrl, cancellationToken);
            return Results.Ok(agentCard);
        });

        return routeGroup;
    }

    /// <summary>
    /// Enables experimental HTTP A2A endpoints for the specified path.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder to configure.</param>
    /// <param name="taskManager">The task manager for handling A2A operations.</param>
    /// <param name="path">The base path for the HTTP A2A endpoints.</param>
    /// <returns>An endpoint convention builder for further configuration.</returns>
    public static IEndpointConventionBuilder MapAgwHttpA2A(this IEndpointRouteBuilder endpoints, ITaskManager taskManager, [StringSyntax("Route")] string path)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(taskManager);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var loggerFactory = endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<IEndpointRouteBuilder>();

        var routeGroup = endpoints.MapGroup(path);

        // /v1/card endpoint - Agent discovery
        routeGroup.MapGet("/v1/{agentName}/card", async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            ITaskManager taskManager = await GetTaskManager(request);

            return await DA2AHttpProcessor
                .GetAgentCardAsync(taskManager, logger, $"{request.Scheme}://{request.Host}{path}", cancellationToken)
                .ConfigureAwait(false);
        });

        // /v1/tasks/{id} endpoint
        routeGroup.MapGet("/v1/{agentName}/tasks/{id}", async (
            string agentName,
            string id,
            [FromQuery] int? historyLength,
            [FromQuery] string? metadata,
            [FromServices] TaskManagerFactory taskManagerFactory,
            CancellationToken cancellationToken) =>
        {
            ITaskManager taskManager = await taskManagerFactory.GetTaskManager(agentName);
            return DA2AHttpProcessor
            .GetTaskAsync(taskManager, logger, id, historyLength, metadata, cancellationToken)
            .ConfigureAwait(false);
        });

        // /v1/tasks/{id}:cancel endpoint
        routeGroup.MapPost("/v1/{agentName}/tasks/{id}:cancel", async
            (
                string agentName,
                string id,
                [FromServices] TaskManagerFactory taskManagerFactory,
                CancellationToken cancellationToken
            ) =>
        {
            ITaskManager taskManager = await taskManagerFactory.GetTaskManager(agentName);
            return await DA2AHttpProcessor
            .CancelTaskAsync(taskManager, logger, id, cancellationToken)
            .ConfigureAwait(false);
        });

        // /v1/tasks/{id}:subscribe endpoint
        routeGroup.MapGet("/v1/{agentName}/tasks/{id}:subscribe", async
            (string agentName, string id, [FromServices] TaskManagerFactory taskManagerFactory, CancellationToken cancellationToken)
            =>
        {
            var taskManager = await taskManagerFactory.GetTaskManager(agentName);
            return DA2AHttpProcessor.SubscribeToTask(taskManager, logger, id, cancellationToken);
        }
        );

        // /v1/tasks/{id}/pushNotificationConfigs endpoint - POST
        routeGroup.MapPost("/v1/{agentName}/tasks/{id}/pushNotificationConfigs",
            async
            (
                string agentName,
                string id,
                [FromBody] PushNotificationConfig pushNotificationConfig,
                [FromServices] TaskManagerFactory taskManagerFactory,
                CancellationToken cancellationToken
                )
            =>
            {
                var taskManager = await taskManagerFactory.GetTaskManager(agentName);
                return await DA2AHttpProcessor.SetPushNotificationAsync(taskManager, logger, id, pushNotificationConfig, cancellationToken)
                .ConfigureAwait(false);
            });

        // /v1/tasks/{id}/pushNotificationConfigs endpoint - GET
        routeGroup.MapGet("/v1/{agentName}/tasks/{id}/pushNotificationConfigs/{notificationConfigId?}",
            async (string agentName, string id, string? notificationConfigId,
            [FromServices] TaskManagerFactory taskManagerFactory, CancellationToken cancellationToken) =>
        {
            var taskManager = await taskManagerFactory.GetTaskManager(agentName);
            return await DA2AHttpProcessor.GetPushNotificationAsync(taskManager, logger, id, notificationConfigId, cancellationToken).ConfigureAwait(false);
        });

        // /v1/message:send endpoint
        routeGroup.MapPost("/v1/{agentName}/message:send", async (string agentName, [FromBody] MessageSendParams sendParams, [FromServices] TaskManagerFactory taskManagerFactory, CancellationToken cancellationToken) =>
        {
            var taskManager = await taskManagerFactory.GetTaskManager(agentName);
            return await DA2AHttpProcessor.SendMessageAsync(taskManager, logger, sendParams, cancellationToken);
        });

        // /v1/message:stream endpoint
        routeGroup.MapPost("/v1/{agentName}/message:stream",
            async (string agentName, [FromBody] MessageSendParams sendParams, [FromServices] TaskManagerFactory taskManagerFactory, CancellationToken cancellationToken) =>
            {
                var taskManager = await taskManagerFactory.GetTaskManager(agentName);
                return DA2AHttpProcessor.SendMessageStream(taskManager, logger, sendParams, cancellationToken);
            });

        return routeGroup;
    }
}
