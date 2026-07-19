using System.Net;

using Agw.Auth.Api;
using Agw.Auth.Application;
using Agw.Auth.Contracts;
using Agw.Auth.Extensions;
using Agw.Shared.Configuration;
using Agw.Shared.Runtime;

using Bens.Results;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Agw.Host.Tests;

public sealed class AuthModuleCompositionTests
{
    [Fact]
    public async Task AuthApplicationPart_MapsSessionRoute()
    {
        await using var app = await CreateAppAsync();

        var response = await app.GetTestClient()
            .GetAsync("/api/auth/session", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/protected")]
    [InlineData("/a2a/agents")]
    [InlineData("/api/hubs/exec/negotiate")]
    public async Task AuthPipeline_RejectsUnauthenticatedProtectedPath(string path)
    {
        await using var app = await CreateAppAsync();

        var response = await app.GetTestClient()
            .GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<WebApplication> CreateAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services
            .AddControllers()
            .AddApplicationPart(typeof(AuthController).Assembly);
        builder.Services.AddAuth();
        builder.Services.AddApiResult();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IAuthenticationStateStore, AuthenticationStateStoreStub>();
        builder.Services.AddSingleton<IServerInitializationState, InitializationStateStub>();

        var app = builder.Build();
        app.UseAgwAuth();
        app.UseRouting();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private sealed class AuthenticationStateStoreStub : IAuthenticationStateStore
    {
        public AuthenticationSnapshot GetAuthenticationSnapshot() => new("hash", 1, []);

        public Task<CreatedApiToken> CreateTokenAsync(
            string name,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<bool> RevokeTokenAsync(
            Guid id,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public bool ValidateToken(string token) => false;

        public Task UpdatePasswordAsync(
            string passwordHash,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class InitializationStateStub : IServerInitializationState
    {
        public bool IsInitialized => true;
        public DatabaseProvider DatabaseProvider => DatabaseProvider.Sqlite;
        public string DatabaseConnectionString => string.Empty;
    }
}
