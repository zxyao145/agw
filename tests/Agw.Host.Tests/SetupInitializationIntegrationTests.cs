using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agw.Auth.Api;
using Agw.Auth.Contracts;
using Agw.Auth.Extensions;
using Agw.Files;
using Agw.Host.Controllers;
using Agw.Host.Data;
using Agw.Host.Hosting;
using Agw.Infrastructure;
using Agw.Infrastructure.Data;
using Agw.Projects.Application;
using Agw.Projects.Contracts;
using Agw.Projects.Contracts.History;
using Agw.Projects.Infrastructure;
using Agw.Settings;
using Agw.Setup.Contracts;
using Agw.Setup.Controllers;
using Agw.Setup.Middleware;
using Agw.Setup.Services;
using Agw.Shared;
using Agw.Shared.Contracts.Persistence;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Exceptions;
using Agw.Shared.Results;
using Agw.Shared.Runtime;
using Bens.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agw.Host.Tests;

public sealed class SetupInitializationIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory,
        ".scratch",
        "setup-initialization",
        Guid.NewGuid().ToString("N")
    );
    private readonly AgwDataPaths _paths;

    public SetupInitializationIntegrationTests()
    {
        _paths = AgwDataPaths.Resolve(Path.Combine(_root, "data"), Path.Combine(_root, "home"));
        _paths.EnsureCreated();
    }

    [Fact]
    public async Task ServerInfo_BearerTokenBeforeSetup_ReturnsUninitialized()
    {
        await using var app = await CreateAuthenticationAppAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            "agw_previous-server-token"
        );

        using var response = await client.GetAsync("/api/server-info", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        Assert.False(body.RootElement.GetProperty("data").GetProperty("initialized").GetBoolean());
    }

    [Fact]
    public async Task Authentication_SetupCompleted_ValidatesPersistedBearerToken()
    {
        await using var app = await CreateAuthenticationAppAsync();
        using var client = app.GetTestClient();
        using var beforeSetup = await client.GetAsync("/api/server-info", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, beforeSetup.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        await scope
            .ServiceProvider.GetRequiredService<ISetupInitializationService>()
            .InitializeAsync(
                new SetupRequest { AdminPassword = "administrator-password" },
                TestContext.Current.CancellationToken
            );
        using var userScope = UserInfoUtil.Push(
            new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Constants.AdminUserId)], "Test")
            )
        );
        var token = await scope
            .ServiceProvider.GetRequiredService<IApiTokenStore>()
            .CreateTokenAsync("setup-test", TestContext.Current.CancellationToken);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await client.GetAsync("/api/auth/session", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        var session = body.RootElement.GetProperty("data");
        Assert.True(session.GetProperty("authenticated").GetBoolean());
        Assert.Equal("bearer", session.GetProperty("accessMode").GetString());
        Assert.Equal(Constants.AdminUserId, session.GetProperty("userId").GetString());
    }

    [Fact]
    public async Task BrowserSetup_UsesConfiguredDatabaseAndRequiresAntiforgeryToken()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:ConnectionString"] = "Data Source=selected.db" }
        );
        ServerDeploymentConfiguration.Apply(builder.Configuration);
        ConfigureServices(builder.Services, builder.Configuration);
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(SetupController).Assembly);
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();
        var page = await client.GetAsync("/setup", TestContext.Current.CancellationToken);
        var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var fields = new Dictionary<string, string>
        {
            ["AdminPassword"] = "administrator-password",
            ["SetupCode"] = "TEST-CODE",
        };
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.DoesNotContain("name=\"Provider\"", html);
        Assert.DoesNotContain("name=\"DeploymentMode\"", html);

        using var unprotected = await client.PostAsync(
            "/setup",
            new FormUrlEncodedContent(fields),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.BadRequest, unprotected.StatusCode);
        Assert.False(File.Exists(Path.Combine(_paths.Root, "server-state.json")));
        client.DefaultRequestHeaders.Add(
            "Cookie",
            string.Join("; ", page.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]))
        );
        fields["__RequestVerificationToken"] = WebUtility.HtmlDecode(
            Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value
        );
        Assert.NotEmpty(fields["__RequestVerificationToken"]);
        using var response = await client.PostAsync(
            "/setup",
            new FormUrlEncodedContent(fields),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        using var scope = app.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        Assert.Equal(
            Path.Combine(Path.GetDirectoryName(_paths.DatabaseFile)!, "selected.db"),
            context.Database.GetDbConnection().DataSource
        );
        Assert.True(await context.Database.CanConnectAsync(TestContext.Current.CancellationToken));
        Assert.NotEmpty(await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.False(File.Exists(_paths.DatabaseFile));
        var state = new DatabaseInitializationStateStore(app.Services.GetRequiredService<IServiceScopeFactory>());
        await state.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(state.IsInitialized);
        Assert.Equal(
            PasswordVerificationResult.Success,
            new PasswordHasher<object>().VerifyHashedPassword(
                new object(),
                state.GetAuthenticationSnapshot().PasswordHash!,
                fields["AdminPassword"]
            )
        );
    }

    [Fact]
    public async Task ConfiguredSetup_InitializesOnceAndDoesNotReplacePasswordOnRestart()
    {
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = "Data Source=unattended.db",
                ["Setup:AdminPassword"] = "administrator-password",
            }
        );
        ServerDeploymentConfiguration.Apply(configuration);
        var services = new ServiceCollection();
        ConfigureServices(services, configuration, ConfiguredSetupBootstrap.FromConfiguration(configuration));
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var initializer = scope.ServiceProvider.GetRequiredService<ConfiguredSetupInitializer>();

        Assert.True(await initializer.InitializeIfConfiguredAsync(TestContext.Current.CancellationToken));
        var state = provider.GetRequiredService<DatabaseInitializationStateStore>();
        await state.UpdatePasswordAsync("changed-hash", TestContext.Current.CancellationToken);
        Assert.False(await initializer.InitializeIfConfiguredAsync(TestContext.Current.CancellationToken));

        var reloaded = new DatabaseInitializationStateStore(provider.GetRequiredService<IServiceScopeFactory>());
        await reloaded.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal("changed-hash", reloaded.GetAuthenticationSnapshot().PasswordHash);
        Assert.Equal(2, state.GetAuthenticationSnapshot().SessionVersion);
        var context = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        Assert.NotEmpty(await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("sqlite", "Data Source=selected.db")]
    [InlineData("postgres", "Host=configured;Database=selected")]
    public async Task Initialize_WhenDatabaseBootstrapFails_DoesNotPersistCompletion(
        string databaseProvider,
        string connectionString
    )
    {
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Database:Provider"] = databaseProvider,
                ["Database:ConnectionString"] = connectionString,
            }
        );
        ServerDeploymentConfiguration.Apply(configuration);
        var services = new ServiceCollection();
        ConfigureServices(services, configuration);
        var bootstrapper = new FailingDatabaseBootstrapper();
        services.AddSingleton<IDatabaseBootstrapper>(bootstrapper);
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var setup = scope.ServiceProvider.GetRequiredService<ISetupInitializationService>();

        await Assert.ThrowsAsync<AgwException>(() =>
            setup.InitializeAsync(
                new SetupRequest { AdminPassword = "administrator-password" },
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(1, bootstrapper.Invocations);
        Assert.False(provider.GetRequiredService<IServerInitializationState>().IsInitialized);
        Assert.False(File.Exists(Path.Combine(_paths.Root, "server-state.json")));
    }

    private async Task<WebApplication> CreateAuthenticationAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        ServerDeploymentConfiguration.Apply(builder.Configuration);
        ConfigureServices(builder.Services, builder.Configuration);
        builder.Services.AddScoped<IEntityAuditUserIdProvider, EntityAuditUserIdProvider>();
        builder.Services.AddApiResult();
        builder
            .Services.AddControllers()
            .AddApplicationPart(typeof(ServerInfoController).Assembly)
            .AddApplicationPart(typeof(AuthController).Assembly);
        var app = builder.Build();
        await app
            .Services.GetRequiredService<DatabaseInitializationStateStore>()
            .RefreshAsync(TestContext.Current.CancellationToken);
        app.UseMiddleware<InitializationGuardMiddleware>();
        app.UseMiddleware<AgwApiExceptionMiddleware>();
        app.UseAgwAuth();
        app.UseRouting();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private void ConfigureServices(
        IServiceCollection services,
        IConfiguration configuration,
        ConfiguredSetupBootstrap? bootstrap = null
    )
    {
        services.AddLogging();
        services.AddSingleton(_paths);
        services.AddFiles(configuration);
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IEntityAuditUserIdProvider, AuditUserIdProvider>();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(_paths.KeysDirectory));
        services.AddInfrastructure(configuration);
        services.AddAuth();
        services.AddSettings();
        services.AddScoped<IUserProjectInitializer, UserProjectInitializer>();
        services.AddScoped<IConversationTurnStore, ConversationTurnStore>();
        services.AddSetup(configuration, bootstrap);
        services.AddSingleton(new SetupCodeService("TEST-CODE"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class AuditUserIdProvider : IEntityAuditUserIdProvider
    {
        public string GetUserId() => "1001";
    }

    private sealed class FailingDatabaseBootstrapper : IDatabaseBootstrapper
    {
        public int Invocations { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            Invocations++;
            throw new AgwException(ErrorCodes.InvalidSetupConfiguration, "Database bootstrap failed.");
        }
    }
}
