using System.Security.Claims;
using Agw.Auth.Application;
using Agw.Infrastructure.Data;
using Agw.Integrations.Application.Persistence;
using Agw.Integrations.Contracts;
using Agw.Integrations.Domain.Plugins;
using Agw.Integrations.Extensions;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Exceptions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Integrations.Tests;

public sealed class ConnectionAuthorizationFacadeTests
{
    [Fact]
    public async Task GetAuthorizationHeaderAsync_OwnedGitHubConnection_ReadsLatestEncryptedCredential()
    {
        using var user = PushUser("owner");
        await using var host = await AuthorizationHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var connectionId = await AddConnectionAsync(db, null);
        var facade = scope.ServiceProvider.GetRequiredService<IConnectionAuthorizationFacade>();
        Assert.Equal(
            "Bearer initial-token",
            await facade.GetAuthorizationHeaderAsync(connectionId, TestContext.Current.CancellationToken)
        );
        (await db.ConnectionCredentials.SingleAsync(TestContext.Current.CancellationToken)).Value = "updated-token";
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            "Bearer updated-token",
            await facade.GetAuthorizationHeaderAsync(connectionId, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task GetAuthorizationHeaderAsync_ForeignConnection_ReturnsNotFound()
    {
        await using var host = await AuthorizationHost.CreateAsync();
        Guid id;
        using (PushUser("owner"))
        {
            await using var scope = host.Services.CreateAsyncScope();
            id = await AddConnectionAsync(scope.ServiceProvider.GetRequiredService<AgwDbContext>(), null);
        }
        using (PushUser("another"))
        {
            await using var scope = host.Services.CreateAsyncScope();
            var error = await Assert.ThrowsAsync<AgwException>(() =>
                scope
                    .ServiceProvider.GetRequiredService<IConnectionAuthorizationFacade>()
                    .GetAuthorizationHeaderAsync(id, TestContext.Current.CancellationToken)
            );
            Assert.Equal(ErrorCodes.ConnectionNotFound.Code, error.Code);
        }
    }

    [Fact]
    public async Task GetAuthorizationHeaderAsync_ExpiredNonRefreshableCredential_RejectsAuthorization()
    {
        using var user = PushUser("owner");
        await using var host = await AuthorizationHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var id = await AddConnectionAsync(
            scope.ServiceProvider.GetRequiredService<AgwDbContext>(),
            TimeProvider.System.GetUtcNow().AddMinutes(-1)
        );
        var error = await Assert.ThrowsAsync<AgwException>(() =>
            scope
                .ServiceProvider.GetRequiredService<IConnectionAuthorizationFacade>()
                .GetAuthorizationHeaderAsync(id, TestContext.Current.CancellationToken)
        );
        Assert.Equal(ErrorCodes.IntegrationCredentialUnavailable.Code, error.Code);
    }

    private static async Task<Guid> AddConnectionAsync(AgwDbContext db, DateTimeOffset? expiresAt)
    {
        var connection = new Connection
        {
            Id = Guid.CreateVersion7(),
            CreateBy = "owner",
            Alias = "github",
            DisplayName = "GitHub",
            PluginId = "github",
            ConnectorId = "github-cloud",
            AuthSchemeId = "oauth2",
            Enabled = true,
            Status = ConnectionStatus.Ready,
        };
        db.Connections.Add(connection);
        db.ConnectionCredentials.Add(
            new ConnectionCredential
            {
                Id = Guid.CreateVersion7(),
                ConnectionId = connection.Id,
                Connection = connection,
                CreateBy = "owner",
                Slot = IntegrationCredentialSlots.OAuthAccessToken,
                Value = "initial-token",
                ExpiresAtUtc = expiresAt,
            }
        );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return connection.Id;
    }

    private static IDisposable PushUser(string id) =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "authorization-test"))
        );

    private sealed class AuthorizationHost : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public ServiceProvider Services { get; }

        private AuthorizationHost(SqliteConnection connection, ServiceProvider services)
        {
            _connection = connection;
            Services = services;
        }

        public static async Task<AuthorizationHost> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHttpClient();
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.AddSingleton<IApplicationLock, InMemoryApplicationLock>();
            services.AddScoped<IUserInfoService, UserInfoService>();
            services.AddDbContext<AgwDbContext>(options =>
                options.UseSqlite(connection).UseSnakeCaseNamingConvention()
            );
            services.AddScoped<IIntegrationsDbContext>(provider => provider.GetRequiredService<AgwDbContext>());
            services.AddIntegrations(new ConfigurationBuilder().Build());
            var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            await scope
                .ServiceProvider.GetRequiredService<AgwDbContext>()
                .Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return new AuthorizationHost(connection, provider);
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
