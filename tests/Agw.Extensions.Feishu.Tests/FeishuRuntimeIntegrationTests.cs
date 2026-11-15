using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Agw.Auth.Contracts;
using Agw.Extensions.Application.Persistence;
using Agw.Extensions.Contracts;
using Agw.Extensions.TestSupport;
using Agw.Files;
using Agw.Host.Data;
using Agw.Infrastructure;
using Agw.Notifications;
using Agw.Notifications.Application;
using Agw.Notifications.Contracts;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Shared.Tooling;
using Agw.Tools;
using Agw.Tools.Abstractions;
using Agw.Tools.Abstractions.ToolBlocks;
using Agw.Tools.Runtime;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agw.Extensions.Feishu.Tests;

public sealed class FeishuRuntimeIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task NotificationTool_ConfiguredFeishuDestination_QueuesNoticeForSelectedChannels()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        IConfiguration configuration = new ConfigurationBuilder().Build();
        await using ExtensionRuntimeIntegrationHost host = await ExtensionRuntimeIntegrationHost.CreateAsync(
            FeishuPackageDirectory(),
            services =>
                services
                    .AddScoped<IEntityAuditUserIdProvider, EntityAuditUserIdProvider>()
                    .AddFiles(configuration)
                    .AddInfrastructure(configuration)
                    .AddTools(configuration)
                    .AddNotifications(configuration, runWorkers: false),
            hostRole: "DataPlane",
            cancellationToken: cancellationToken
        );
        await host.LoadAndActivateAsync(FeishuExtension.ExtensionId, cancellationToken: cancellationToken);
        var registry = host.Services.GetRequiredService<ToolRegistryService>();
        Assert.Contains(registry.GetListedTools(), tool => tool.Name == "notification");
        var definition = JsonSerializer.Deserialize<ToolBlockDefinition>(
            """{"name":"notification","options":{}}""",
            JsonOptions
        )!;
        await using var tools = await registry.ToolBlocks.MaterializeAsync(
            [definition],
            ToolBlockScope.Agent,
            new ToolMaterializationContext
            {
                Agent = new Agent { Id = Guid.CreateVersion7() },
                Project = new Project { Id = Guid.CreateVersion7() },
                Workspace = host.PackageDirectory,
                DefaultMode = "Build",
            },
            cancellationToken
        );
        var list = Assert.IsAssignableFrom<AIFunction>(
            Assert.Single(tools.Tools, tool => tool.Name == "list_notification_channels")
        );
        var send = Assert.IsType<ApprovalRequiredAIFunction>(
            Assert.Single(tools.Tools, tool => tool.Name == "send_notification")
        );
        var getStatus = Assert.IsAssignableFrom<AIFunction>(
            Assert.Single(tools.Tools, tool => tool.Name == "get_notification_status")
        );
        Assert.True(AgwToolMetadataBinding.GetMetadata(list)?.AllowInPlanMode);
        Assert.False(AgwToolMetadataBinding.GetMetadata(send)?.AllowInPlanMode);
        var statusMetadata = Assert.IsType<AgwToolMetadata>(AgwToolMetadataBinding.GetMetadata(getStatus));
        Assert.Equal(AgwToolPermission.ReadOnly, statusMetadata.RequiredPermission);
        Assert.True(statusMetadata.AllowInPlanMode);

        const string title = "Agent test notice";
        const string message = "The requested work has finished.";
        AIFunctionArguments statusArguments;
        string channelKey;
        using (UserInfoUtil.Push(CreateUser("alice")))
        {
            var application = await host.InvokeEndpointAsync(
                FeishuExtension.ExtensionId,
                "apps-create",
                Request(
                    "POST",
                    new
                    {
                        name = "Test App",
                        mode = "App",
                        openDomain = "https://open.feishu.cn",
                        appId = "cli_test",
                        appSecret = "secret-value",
                    }
                ),
                cancellationToken
            );
            var destination = await host.InvokeEndpointAsync(
                FeishuExtension.ExtensionId,
                "targets-save",
                Request(
                    "POST",
                    new
                    {
                        appId = application.JsonBody!.Value.GetProperty("id").GetGuid(),
                        kind = "Chat",
                        name = "Test chat",
                        receiveId = "oc_test",
                    }
                ),
                cancellationToken
            );
            Guid targetId = destination.JsonBody!.Value.GetProperty("id").GetGuid();
            var channels = Assert.IsType<JsonElement>(await list.InvokeAsync(new(), cancellationToken));
            JsonElement feishu = Assert.Single(
                channels.EnumerateArray(),
                channel => channel.GetProperty("targetId").GetString() == targetId.ToString("D")
            );
            Assert.EndsWith("Test App / Test chat", feishu.GetProperty("name").GetString());
            channelKey = feishu.GetProperty("channelKey").GetString()!;

            var error = await Assert.ThrowsAsync<AgwException>(() =>
                send.InvokeAsync(SendArguments(title, message, []), cancellationToken).AsTask()
            );
            Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
            var result = Assert.IsType<JsonElement>(
                await send.InvokeAsync(SendArguments(title, message, ["desktop", channelKey]), cancellationToken)
            );
            Guid notificationId = result.GetGuid();
            statusArguments = new AIFunctionArguments { ["notificationId"] = result };

            await using var scope = host.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<NotificationAppService>();
            var notice = Assert.IsType<NotificationDetail>(await service.GetAsync(notificationId, cancellationToken));
            Assert.Equal(NotificationKind.Notice, notice.Kind);
            Assert.Equal(title, notice.Title);
            Assert.Equal(message, notice.Text);

            var queued = Assert.IsType<JsonElement>(await getStatus.InvokeAsync(statusArguments, cancellationToken));
            Assert.Equal(notificationId, queued.GetProperty("notificationId").GetGuid());
            var dispatches = queued.GetProperty("dispatches").EnumerateArray();
            Assert.Equal(
                ["desktop", channelKey],
                dispatches.Select(dispatch => dispatch.GetProperty("channelKey").GetString())
            );
            Assert.All(dispatches, dispatch => Assert.Equal("Pending", dispatch.GetProperty("state").GetString()));

            await service.MarkDesktopDeliveredAsync(notificationId, notice.Version, cancellationToken);
            var updated = Assert.IsType<JsonElement>(await getStatus.InvokeAsync(statusArguments, cancellationToken));
            Assert.Collection(
                updated.GetProperty("dispatches").EnumerateArray(),
                desktop => Assert.Equal("Sent", desktop.GetProperty("state").GetString()),
                feishuDispatch => Assert.Equal("Pending", feishuDispatch.GetProperty("state").GetString())
            );
        }

        using (UserInfoUtil.Push(CreateUser("bob")))
        {
            var channels = Assert.IsType<JsonElement>(await list.InvokeAsync(new(), cancellationToken));
            Assert.Equal("desktop", Assert.Single(channels.EnumerateArray()).GetProperty("channelKey").GetString());
            var error = await Assert.ThrowsAsync<AgwException>(() =>
                send.InvokeAsync(SendArguments(title, message, [channelKey]), cancellationToken).AsTask()
            );
            Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
            var notFound = await Assert.ThrowsAsync<AgwException>(() =>
                getStatus.InvokeAsync(statusArguments, cancellationToken).AsTask()
            );
            Assert.Equal(ErrorCodes.NotificationNotFound.Code, notFound.Code);
        }
    }

    [Fact]
    public async Task AppsUpdate_BlankAppSecretRetainsCiphertextAndOtherUserCannotReadApp()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using ExtensionRuntimeIntegrationHost host = await CreateHostAsync(cancellationToken);
        ExtensionGenerationSnapshot generation = await host.LoadAndActivateAsync(
            FeishuExtension.ExtensionId,
            resumeBackground: true,
            cancellationToken: cancellationToken
        );
        ExtensionEnablementState enablement = await host
            .Services.GetRequiredService<IExtensionEnablementStore>()
            .GetAsync(FeishuExtension.ExtensionId, cancellationToken);
        Assert.Equal(ExtensionGenerationState.Active, Assert.Single(host.Runtime.GetSnapshots()).State);
        Assert.True(enablement.Enabled);
        Assert.Equal(generation.Epoch, enablement.Version);

        JsonElement created;
        using (UserInfoUtil.Push(CreateUser("alice")))
        {
            created = (
                await host.InvokeEndpointAsync(
                    FeishuExtension.ExtensionId,
                    "apps-create",
                    Request(
                        "POST",
                        new
                        {
                            name = "Test App",
                            mode = "App",
                            openDomain = "https://open.feishu.cn",
                            appId = "cli_test",
                            appSecret = "secret-value",
                        }
                    ),
                    cancellationToken
                )
            ).JsonBody!.Value;
        }

        Guid appId = created.GetProperty("id").GetGuid();
        JsonElement secrets = await ReadStoredSecretsAsync(host.DatabaseConnection, cancellationToken);
        Assert.NotEqual("secret-value", secrets.GetProperty("app_secret").GetString());
        Assert.True(created.GetProperty("hasAppSecret").GetBoolean());
        Assert.DoesNotContain("secret-value", created.GetRawText(), StringComparison.Ordinal);

        using (UserInfoUtil.Push(CreateUser("alice")))
        {
            await host.InvokeEndpointAsync(
                FeishuExtension.ExtensionId,
                "apps-update",
                Request(
                    "POST",
                    new
                    {
                        id = appId,
                        version = created.GetProperty("version").GetInt64(),
                        name = "Test App",
                        mode = "App",
                        openDomain = "https://open.feishu.cn",
                        appId = "cli_test",
                        appSecret = "",
                    }
                ),
                cancellationToken
            );
        }

        JsonElement retainedSecrets = await ReadStoredSecretsAsync(host.DatabaseConnection, cancellationToken);
        Assert.Equal(
            secrets.GetProperty("app_secret").GetString(),
            retainedSecrets.GetProperty("app_secret").GetString()
        );

        using (UserInfoUtil.Push(CreateUser("bob")))
        {
            AgwException exception = await Assert.ThrowsAsync<AgwException>(() =>
                host.InvokeEndpointAsync(
                    FeishuExtension.ExtensionId,
                    "apps-get",
                    Request("GET", body: null, new Dictionary<string, string[]> { ["id"] = [appId.ToString()] }),
                    cancellationToken
                )
            );
            Assert.Equal(ErrorCodes.ExtensionNotFound.Code, exception.Code);
        }
    }

    [Fact]
    public async Task CallbackEndpoint_IsNotRegistered()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using ExtensionRuntimeIntegrationHost host = await CreateHostAsync(cancellationToken, "ControlPlane");
        await host.LoadAndActivateAsync(
            FeishuExtension.ExtensionId,
            resumeBackground: false,
            cancellationToken: cancellationToken
        );

        await Assert.ThrowsAsync<AgwException>(() =>
            host.InvokeEndpointAsync(
                FeishuExtension.ExtensionId,
                "callback",
                Request("POST", body: new { }),
                cancellationToken
            )
        );
    }

    private static Task<ExtensionRuntimeIntegrationHost> CreateHostAsync(
        CancellationToken cancellationToken,
        string hostRole = "DataPlane"
    ) =>
        ExtensionRuntimeIntegrationHost.CreateAsync(
            FeishuPackageDirectory(),
            hostRole: hostRole,
            cancellationToken: cancellationToken
        );

    private static string FeishuPackageDirectory() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/extensions/feishu/dist"));

    private static ClaimsPrincipal CreateUser(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));

    private static AIFunctionArguments SendArguments(string title, string text, string[] channelKeys) =>
        new()
        {
            ["title"] = title,
            ["text"] = text,
            ["channelKeys"] = JsonSerializer.SerializeToElement(channelKeys, JsonOptions),
        };

    private static JsonElement Request(
        string method,
        object? body,
        IReadOnlyDictionary<string, string[]>? query = null
    ) =>
        JsonSerializer.SerializeToElement(
            new
            {
                method,
                query = query ?? new Dictionary<string, string[]>(),
                body,
            },
            JsonOptions
        );

    private static async Task<JsonElement> ReadStoredSecretsAsync(
        DbConnection connection,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT app_secret
            FROM zext_{FeishuExtension.ExtensionId:N}_app
            WHERE name = 'Test App'
            """;
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        return JsonSerializer.SerializeToElement(new { app_secret = reader.GetString(0) });
    }
}
