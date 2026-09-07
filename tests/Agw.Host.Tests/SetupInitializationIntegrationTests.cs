using System.Net;
using System.Text.RegularExpressions;
using Agw.Auth.Extensions;
using Agw.Host.Hosting;
using Agw.Infrastructure;
using Agw.Infrastructure.Data;
using Agw.Setup.Contracts;
using Agw.Setup.Controllers;
using Agw.Setup.Services;
using Agw.Shared.Configuration;
using Agw.Shared.Contracts.Persistence;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
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
    private readonly AgwDataPaths _paths;

    public SetupInitializationIntegrationTests()
    {
        _paths = AgwDataPaths.Resolve(Path.Combine(Path.GetTempPath(), $"agw-setup-{Guid.NewGuid():N}"), "/unused");
        _paths.EnsureCreated();
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
        ServerDeploymentConfiguration.Apply(builder.Configuration, new Dictionary<string, string?>());
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
        Assert.False(File.Exists(_paths.StateFile));
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
        var state = new JsonInitializationStateStore(_paths);
        Assert.True(state.IsInitialized);
        Assert.Empty(state.GetLegacyDeploymentConfiguration());
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
        ServerDeploymentConfiguration.Apply(configuration, new Dictionary<string, string?>());
        var services = new ServiceCollection();
        ConfigureServices(services, configuration, ConfiguredSetupBootstrap.FromConfiguration(configuration));
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var initializer = scope.ServiceProvider.GetRequiredService<ConfiguredSetupInitializer>();

        Assert.True(await initializer.InitializeIfConfiguredAsync(TestContext.Current.CancellationToken));
        var state = provider.GetRequiredService<JsonInitializationStateStore>();
        await state.UpdatePasswordAsync("changed-hash", TestContext.Current.CancellationToken);
        Assert.False(await initializer.InitializeIfConfiguredAsync(TestContext.Current.CancellationToken));

        Assert.Equal("changed-hash", new JsonInitializationStateStore(_paths).GetAuthenticationSnapshot().PasswordHash);
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
        ServerDeploymentConfiguration.Apply(configuration, new Dictionary<string, string?>());
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

        Assert.Equal(
            databaseProvider == "postgres" ? DatabaseProvider.Postgres : DatabaseProvider.Sqlite,
            bootstrapper.Provider
        );
        var context = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        Assert.Equal(context.Database.GetConnectionString(), bootstrapper.ConnectionString);
        Assert.False(provider.GetRequiredService<IServerInitializationState>().IsInitialized);
        Assert.False(File.Exists(_paths.StateFile));
    }

    private void ConfigureServices(
        IServiceCollection services,
        IConfiguration configuration,
        ConfiguredSetupBootstrap? bootstrap = null
    )
    {
        services.AddLogging();
        services.AddSingleton(_paths);
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IEntityAuditUserIdProvider, AuditUserIdProvider>();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(_paths.KeysDirectory));
        services.AddInfrastructure(configuration);
        services.AddAuth();
        services.AddSetup(configuration, bootstrap);
        services.AddSingleton(new SetupCodeService("TEST-CODE"));
    }

    public void Dispose() => Directory.Delete(_paths.Root, recursive: true);

    private sealed class AuditUserIdProvider : IEntityAuditUserIdProvider
    {
        public string GetUserId() => "1001";
    }

    private sealed class FailingDatabaseBootstrapper : IDatabaseBootstrapper
    {
        public DatabaseProvider Provider { get; private set; }
        public string? ConnectionString { get; private set; }

        public Task InitializeAsync(
            DatabaseProvider provider,
            string connectionString,
            CancellationToken cancellationToken = default
        )
        {
            Provider = provider;
            ConnectionString = connectionString;
            throw new AgwException(ErrorCodes.InvalidSetupConfiguration, "Database bootstrap failed.");
        }
    }
}
