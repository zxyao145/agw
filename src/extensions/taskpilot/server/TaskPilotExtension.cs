using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk;
using Agw.Extensions.Sdk.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Extensions.TaskPilot;

public sealed class TaskPilotExtension : IServerExtension
{
    public static readonly Guid ExtensionId = Guid.Parse("019a4c17-5f00-7a80-8b3d-2b732f48a101");

    public void ConfigureServices(IServiceCollection services, IExtensionRegistration registration)
    {
        services.AddScoped<TaskPilotEndpointService>();
        services.AddScoped<TaskPilotExecutionCoordinator>();
        services.AddScoped<TaskPilotDeliveryCoordinator>();
        services.AddScoped<TaskPilotGitHubClient>();
        services.AddScoped<TaskPilotEventProcessor>();
        services.AddScoped<TaskPilotRuleEventHandler>();
        services.AddScoped<TaskPilotSystemEventHandler>();
        services.AddScoped<TaskPilotOutboxResultHandler>();
        services
            .AddHttpClient("taskpilot.github", client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(static () =>
                new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }
            );
        services
            .AddHttpClient("taskpilot.health", client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(static () =>
                new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }
            );

        registration.RegisterJsonContext(TaskPilotJsonContext.Default);
        registration.RegisterJsonContext(TaskPilotDeliveryJsonContext.Default);
        registration.RegisterDbContext<TaskPilotDbContext, TaskPilotMigrationsMarker>();
        registration.RegisterEventHandler(
            new ExtensionEventHandlerDescriptor { Id = TaskPilotEventHandlers.RuleEvents },
            static (provider, context, delivery, cancellationToken) =>
                provider
                    .GetRequiredService<TaskPilotRuleEventHandler>()
                    .ProcessAsync(context, delivery, cancellationToken)
        );
        registration.RegisterEventHandler(
            new ExtensionEventHandlerDescriptor { Id = TaskPilotEventHandlers.SystemEvents },
            static (provider, context, delivery, cancellationToken) =>
                provider
                    .GetRequiredService<TaskPilotSystemEventHandler>()
                    .ProcessAsync(context, delivery, cancellationToken)
        );

        foreach (string kind in TaskPilotOutboxResultHandler.Kinds)
        {
            registration.RegisterOutboxResultHandler<TaskPilotOutboxResultHandler>(kind);
        }

        foreach (TaskPilotEndpoint endpoint in TaskPilotEndpoint.All)
        {
            registration.RegisterEndpoint(
                new ExtensionEndpointDescriptor
                {
                    Id = endpoint.Id,
                    Method = endpoint.Method,
                    RelativePath = endpoint.Path,
                    Summary = endpoint.Summary,
                },
                (provider, context, request, cancellationToken) =>
                    provider
                        .GetRequiredService<TaskPilotEndpointService>()
                        .HandleAsync(endpoint.Id, context, request, cancellationToken)
            );
        }
    }

    public ValueTask StartAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

public sealed class TaskPilotMigrationsMarker { }

internal static class TaskPilotEventHandlers
{
    public const string RuleEvents = "rule-events";

    public const string SystemEvents = "system-events";
}

internal sealed record TaskPilotEndpoint
{
    public required string Id { get; init; }

    public required string Method { get; init; }

    public required string Path { get; init; }

    public required string Summary { get; init; }

    public static IReadOnlyList<TaskPilotEndpoint> All { get; } =
    [
        new()
        {
            Id = "rules-list",
            Method = "GET",
            Path = "rules/list",
            Summary = "List rules",
        },
        new()
        {
            Id = "rules-get",
            Method = "GET",
            Path = "rules/get",
            Summary = "Get a rule",
        },
        new()
        {
            Id = "rules-create",
            Method = "POST",
            Path = "rules/create",
            Summary = "Create a rule",
        },
        new()
        {
            Id = "rules-update",
            Method = "POST",
            Path = "rules/update",
            Summary = "Update a rule",
        },
        new()
        {
            Id = "rules-delete",
            Method = "POST",
            Path = "rules/delete",
            Summary = "Delete a rule",
        },
        new()
        {
            Id = "rules-pause",
            Method = "POST",
            Path = "rules/pause",
            Summary = "Pause a rule",
        },
        new()
        {
            Id = "rules-resume",
            Method = "POST",
            Path = "rules/resume",
            Summary = "Resume a rule",
        },
        new()
        {
            Id = "matters-list",
            Method = "GET",
            Path = "matters/list",
            Summary = "List matters",
        },
        new()
        {
            Id = "matters-get",
            Method = "GET",
            Path = "matters/get",
            Summary = "Get a matter",
        },
        new()
        {
            Id = "matters-pause",
            Method = "POST",
            Path = "matters/pause",
            Summary = "Pause a matter",
        },
        new()
        {
            Id = "matters-resume",
            Method = "POST",
            Path = "matters/resume",
            Summary = "Resume a matter",
        },
        new()
        {
            Id = "matters-run",
            Method = "POST",
            Path = "matters/run",
            Summary = "Run a matter now",
        },
        new()
        {
            Id = "matters-cancel",
            Method = "POST",
            Path = "matters/cancel",
            Summary = "Cancel a matter round",
        },
        new()
        {
            Id = "rounds-list",
            Method = "GET",
            Path = "rounds/list",
            Summary = "List matter rounds",
        },
        new()
        {
            Id = "rounds-get",
            Method = "GET",
            Path = "rounds/get",
            Summary = "Get a round",
        },
        new()
        {
            Id = "reviews-list",
            Method = "GET",
            Path = "reviews/list",
            Summary = "List matter reviews",
        },
        new()
        {
            Id = "reviews-cancel",
            Method = "POST",
            Path = "reviews/cancel",
            Summary = "Cancel a review",
        },
        new()
        {
            Id = "deliveries-get",
            Method = "GET",
            Path = "deliveries/get",
            Summary = "Get delivery status",
        },
    ];
}
