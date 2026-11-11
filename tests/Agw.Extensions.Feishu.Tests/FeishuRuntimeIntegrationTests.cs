using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Agw.Auth.Contracts;
using Agw.Extensions.Application.Persistence;
using Agw.Extensions.Contracts;
using Agw.Extensions.TestSupport;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agw.Extensions.Feishu.Tests;

public sealed class FeishuRuntimeIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
