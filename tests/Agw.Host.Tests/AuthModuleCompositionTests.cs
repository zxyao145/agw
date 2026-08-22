using System.Net;
using System.Net.WebSockets;
using System.Text;
using Agw.Auth.Api;
using Agw.Auth.Application;
using Agw.Auth.Contracts;
using Agw.Auth.Extensions;
using Agw.Shared.Configuration;
using Agw.Shared.Runtime;
using Bens.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Agw.Host.Tests;

public sealed class AuthModuleCompositionTests
{
    [Fact]
    public async Task AuthApplicationPart_MapsSessionRoute()
    {
        await using var app = await CreateAppAsync();

        var response = await app.GetTestClient().GetAsync("/api/auth/session", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/protected")]
    [InlineData("/a2a/agents")]
    [InlineData("/api/hubs/exec/negotiate")]
    public async Task AuthPipeline_RejectsUnauthenticatedProtectedPath(string path)
    {
        await using var app = await CreateAppAsync();

        var response = await app.GetTestClient().GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// 验证 Desktop 开发 Origin 能通过真实中间件管道，使用 SignalR 查询参数认证 WebSocket 握手。
    /// </summary>
    [Fact]
    public async Task AuthPipeline_KestrelDevelopmentDesktopWebSocketQueryToken_Connects()
    {
        await using var app = await CreateAppAsync(useTestServer: false, environmentName: Environments.Development);
        var server = app.Services.GetRequiredService<IServer>();
        var address = Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses);
        var uri = new UriBuilder(address)
        {
            Scheme = "ws",
            Path = "/api/hubs/exec",
            Query = "access_token=agw_valid",
        }.Uri;
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "http://localhost:3000");

        await socket.ConnectAsync(uri, TestContext.Current.CancellationToken);
        var buffer = new byte[32];
        var result = await socket.ReceiveAsync(buffer, TestContext.Current.CancellationToken);

        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        Assert.Equal("connected", Encoding.UTF8.GetString(buffer, 0, result.Count));
    }

    /// <summary>
    /// 创建并启动用于认证管道测试的 Web 应用。
    /// </summary>
    /// <param name="useTestServer">是否使用内存 TestServer；否则启动真实 Kestrel。</param>
    /// <param name="environmentName">可选的宿主环境名称。</param>
    /// <returns>已启动的测试 Web 应用。</returns>
    private static async Task<WebApplication> CreateAppAsync(bool useTestServer = true, string? environmentName = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environmentName });
        if (useTestServer)
        {
            builder.WebHost.UseTestServer();
        }
        else
        {
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        }
        builder.Services.AddControllers().AddApplicationPart(typeof(AuthController).Assembly);
        builder.Services.AddAuth();
        builder.Services.AddApiResult();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<AuthenticationStateStoreStub>();
        builder.Services.AddSingleton<IAuthenticationStateStore>(provider =>
            provider.GetRequiredService<AuthenticationStateStoreStub>()
        );
        builder.Services.AddSingleton<IAuthenticationStateReader>(provider =>
            provider.GetRequiredService<AuthenticationStateStoreStub>()
        );
        builder.Services.AddSingleton<IApiTokenStore>(provider =>
            provider.GetRequiredService<AuthenticationStateStoreStub>()
        );
        builder.Services.AddSingleton<IServerInitializationState, InitializationStateStub>();

        var app = builder.Build();
        app.UseAgwAuth();
        app.UseRouting();
        app.UseAuthorization();
        app.MapControllers();
        app.MapGet(
            "/api/hubs/exec",
            async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await socket.SendAsync(
                    "connected"u8.ToArray(),
                    WebSocketMessageType.Text,
                    true,
                    context.RequestAborted
                );
            }
        );
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private sealed class AuthenticationStateStoreStub : IAuthenticationStateStore, IApiTokenStore
    {
        public AuthenticationSnapshot GetAuthenticationSnapshot() => new("hash", 1);

        public Task<IReadOnlyList<ApiTokenSummary>> ListTokensAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ApiTokenSummary>>([]);

        public Task<CreatedApiToken> CreateTokenAsync(string name, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> RevokeTokenAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<ApiTokenIdentity?> ValidateTokenAsync(
            string token,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(
                string.Equals(token, "agw_valid", StringComparison.Ordinal)
                    ? new ApiTokenIdentity("token-creator")
                    : null
            );

        public Task UpdatePasswordAsync(string passwordHash, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class InitializationStateStub : IServerInitializationState
    {
        public bool IsInitialized => true;
        public DatabaseProvider DatabaseProvider => DatabaseProvider.Sqlite;
        public string DatabaseConnectionString => string.Empty;
    }
}
