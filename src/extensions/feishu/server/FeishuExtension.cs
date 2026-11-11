using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk;
using Agw.Extensions.Sdk.EntityFrameworkCore;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Extensions.Feishu;

public sealed class FeishuExtension : IServerExtension
{
    public static readonly Guid ExtensionId = Guid.Parse("019a4c17-5f00-7a80-8b3d-2b732f48f112");
    private static FeishuRuntimeState? _runtimeState;

    public void ConfigureServices(IServiceCollection services, IExtensionRegistration registration)
    {
        var runtimeState = new FeishuRuntimeState();
        _runtimeState = runtimeState;
        services.AddSingleton(runtimeState);
        services.AddSingleton<FeishuNodeBridge>();
        services.AddScoped<FeishuEndpointService>();
        registration.RegisterJsonContext(FeishuApiJsonContext.Default);

        registration.RegisterDbContext<FeishuDbContext, FeishuMigrationsMarker>();
        registration.RegisterNotificationChannel<FeishuNotificationChannel>(
            new ExtensionNotificationChannelDescriptor { Id = FeishuChannel.Id, Name = "Feishu" }
        );
        registration.RegisterOutboxResultHandler<FeishuOutboxResultHandler>(ExtensionOutboxKind.SubmitAnswer);
        registration.RegisterBackgroundService<FeishuBackgroundService>();

        foreach (FeishuEndpoint endpoint in FeishuEndpoint.All)
        {
            registration.RegisterEndpoint(
                new ExtensionEndpointDescriptor
                {
                    Id = endpoint.Id,
                    Method = endpoint.Method,
                    RelativePath = endpoint.Path,
                    Summary = endpoint.Summary,
                    IsBrowserPage = endpoint.IsBrowserPage,
                },
                (provider, context, request, cancellationToken) =>
                    provider
                        .GetRequiredService<FeishuEndpointService>()
                        .HandleAsync(endpoint.Id, context, request, provider, cancellationToken)
            );
        }
    }

    public ValueTask StartAsync(IExtensionContext context, CancellationToken cancellationToken)
    {
        (_runtimeState ?? throw new AgwException(ErrorCodes.ExtensionUnavailable)).Initialize(context);
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(IExtensionContext context, CancellationToken cancellationToken)
    {
        (_runtimeState ?? throw new AgwException(ErrorCodes.ExtensionUnavailable)).Clear();
        return ValueTask.CompletedTask;
    }
}

public sealed class FeishuMigrationsMarker { }

internal sealed record FeishuEndpoint
{
    public required string Id { get; init; }

    public required string Method { get; init; }

    public required string Path { get; init; }

    public required string Summary { get; init; }

    public bool IsBrowserPage { get; init; }

    public static IReadOnlyList<FeishuEndpoint> All { get; } =
    [
        new()
        {
            Id = "apps-list",
            Method = "GET",
            Path = "apps/list",
            Summary = "List Feishu applications",
        },
        new()
        {
            Id = "apps-get",
            Method = "GET",
            Path = "apps/get",
            Summary = "Get Feishu application configuration",
        },
        new()
        {
            Id = "apps-create",
            Method = "POST",
            Path = "apps/create",
            Summary = "Create Feishu application",
        },
        new()
        {
            Id = "apps-update",
            Method = "POST",
            Path = "apps/update",
            Summary = "Update Feishu application configuration",
        },
        new()
        {
            Id = "apps-delete",
            Method = "POST",
            Path = "apps/delete",
            Summary = "Delete a Feishu application",
        },
        new()
        {
            Id = "apps-verify",
            Method = "POST",
            Path = "apps/verify",
            Summary = "Verify Feishu application credentials",
        },
        new()
        {
            Id = "targets-list",
            Method = "GET",
            Path = "targets/list",
            Summary = "List Feishu destinations",
        },
        new()
        {
            Id = "targets-discover",
            Method = "GET",
            Path = "targets/discover",
            Summary = "Discover Feishu chats",
        },
        new()
        {
            Id = "targets-save",
            Method = "POST",
            Path = "targets/save",
            Summary = "Save a Feishu destination",
        },
        new()
        {
            Id = "targets-delete",
            Method = "POST",
            Path = "targets/delete",
            Summary = "Delete a Feishu destination",
        },
        new()
        {
            Id = "bind-start",
            Method = "POST",
            Path = "bind/start",
            Summary = "Start Feishu identity binding",
        },
        new()
        {
            Id = "bind-confirm-page",
            Method = "GET",
            Path = "bind/confirm",
            Summary = "Confirm Feishu identity binding",
            IsBrowserPage = true,
        },
        new()
        {
            Id = "bind-confirm",
            Method = "POST",
            Path = "bind/confirm",
            Summary = "Save Feishu identity binding",
        },
        new()
        {
            Id = "bind-list",
            Method = "GET",
            Path = "bind/list",
            Summary = "List linked Feishu accounts",
        },
        new()
        {
            Id = "bind-delete",
            Method = "POST",
            Path = "bind/delete",
            Summary = "Delete Feishu identity binding",
        },
        new()
        {
            Id = "test-send",
            Method = "POST",
            Path = "test-send",
            Summary = "Send a Feishu test message",
        },
    ];
}
