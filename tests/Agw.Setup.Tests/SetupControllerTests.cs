using System.Net;
using Agw.Auth.Application;
using Agw.Setup.Contracts;
using Agw.Setup.Controllers;
using Agw.Setup.Services;
using Agw.Shared.Configuration;
using Agw.Shared.Runtime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Agw.Setup.Tests;

public sealed class SetupControllerTests
{
#if DEBUG
    [Fact]
    public void Index_WhenSetupIsCompleteInDebug_ReturnsSetupView()
    {
        var controller = CreateController();

        var result = controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<SetupRequest>(view.Model);
    }
#else
    [Fact]
    public void Index_WhenSetupIsCompleteInRelease_ReturnsApiResult()
    {
        var controller = CreateController();

        var result = controller.Index();

        AssertApiResult(result);
    }
#endif

    [Fact]
    public async Task IndexPost_WhenSetupIsComplete_ReturnsApiResult()
    {
        var controller = CreateController();

        var result = await controller.Index(new SetupRequest(), TestContext.Current.CancellationToken);

        AssertApiResult(result);
    }

    [Fact]
    public void Index_WhenSetupIsRequired_PrefillsStandaloneSqlitePath()
    {
        var paths = CreatePaths();
        var controller = CreateController(isInitialized: false, paths: paths);

        var result = Assert.IsType<ViewResult>(controller.Index());
        var model = Assert.IsType<SetupRequest>(result.Model);

        Assert.Equal(DeploymentMode.Standalone, model.DeploymentMode);
        Assert.Equal(DatabaseProvider.Sqlite, model.Provider);
        Assert.Equal(paths.DatabaseFile, model.SqlitePath);
    }

    [Fact]
    public void Index_WhenControlPlaneRequiresCluster_PrefillsClusterPostgres()
    {
        var controller = CreateController(
            isInitialized: false,
            deploymentOptions: new SetupDeploymentOptions(DeploymentMode.Cluster)
        );

        var result = Assert.IsType<ViewResult>(controller.Index());
        var model = Assert.IsType<SetupRequest>(result.Model);

        Assert.Equal(DeploymentMode.Cluster, model.DeploymentMode);
        Assert.Equal(DatabaseProvider.Postgres, model.Provider);
    }

    [Fact]
    public async Task IndexPost_WhenStandaloneInitializationSucceeds_RedirectsToRoot()
    {
        var initializationService = new StubSetupInitializationService();
        var controller = CreateController(isInitialized: false, initializationService: initializationService);
        var request = CreateRequest(DeploymentMode.Standalone);

        var result = await controller.Index(request, TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/", redirect.Url);
        Assert.Same(request, initializationService.LastRequest);
    }

    [Fact]
    public async Task IndexPost_WhenClusterInitializationSucceeds_ReturnsRestartView()
    {
        var initializationService = new StubSetupInitializationService();
        var controller = CreateController(isInitialized: false, initializationService: initializationService);
        var request = CreateRequest(DeploymentMode.Cluster);

        var result = await controller.Index(request, TestContext.Current.CancellationToken);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("RestartRequired", view.ViewName);
        Assert.Same(request, initializationService.LastRequest);
    }

    [Fact]
    public async Task IndexPost_WhenControlPlaneReceivesStandalone_ReturnsFormWithoutInitializing()
    {
        var initializationService = new StubSetupInitializationService();
        var controller = CreateController(
            isInitialized: false,
            initializationService: initializationService,
            deploymentOptions: new SetupDeploymentOptions(DeploymentMode.Cluster)
        );

        var result = await controller.Index(
            CreateRequest(DeploymentMode.Standalone),
            TestContext.Current.CancellationToken
        );

        Assert.IsType<ViewResult>(result);
        Assert.Null(initializationService.LastRequest);
        Assert.True(controller.ModelState.ContainsKey(nameof(SetupRequest.DeploymentMode)));
    }

    [Fact]
    public async Task IndexPost_WhenRemoteSetupCodeIsMissing_ReturnsFormWithoutInitializing()
    {
        var initializationService = new StubSetupInitializationService();
        var controller = CreateController(isInitialized: false, initializationService: initializationService);
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
        controller.HttpContext.Request.Host = new HostString("agw.example.com");

        var result = await controller.Index(
            CreateRequest(DeploymentMode.Standalone),
            TestContext.Current.CancellationToken
        );

        Assert.IsType<ViewResult>(result);
        Assert.Null(initializationService.LastRequest);
        Assert.True(controller.ModelState.ContainsKey(nameof(SetupRequest.SetupCode)));
    }

    private static SetupController CreateController(
        bool isInitialized = true,
        StubSetupInitializationService? initializationService = null,
        AgwDataPaths? paths = null,
        SetupDeploymentOptions? deploymentOptions = null
    )
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
        httpContext.Request.Host = new HostString("localhost");
        return new SetupController(
            new StubInitializationStateStore(isInitialized),
            initializationService ?? new StubSetupInitializationService(),
            new SetupCodeService("TEST-CODE"),
            new AuthenticationAttemptLimiter(),
            TimeProvider.System,
            paths ?? CreatePaths(),
            deploymentOptions
        )
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private static SetupRequest CreateRequest(DeploymentMode deploymentMode)
    {
        return new SetupRequest
        {
            DeploymentMode = deploymentMode,
            Provider = deploymentMode == DeploymentMode.Cluster ? DatabaseProvider.Postgres : DatabaseProvider.Sqlite,
            SqlitePath = "/data/agw.db",
            PostgresHost = "db.internal",
            PostgresDatabase = "agw",
            PostgresUsername = "agw",
            PostgresPassword = "database-password",
            AdminPassword = "administrator-password",
        };
    }

    private static AgwDataPaths CreatePaths()
    {
        return AgwDataPaths.Resolve(Path.Combine(Path.GetTempPath(), "agw-controller-tests"), "/unused");
    }

    private static void AssertApiResult(IActionResult result)
    {
        Assert.StartsWith("Bens.Results.ApiResult", result.GetType().FullName);
    }

    private sealed class StubInitializationStateStore : IInitializationStateStore
    {
        public StubInitializationStateStore(bool isInitialized)
        {
            IsInitialized = isInitialized;
        }

        public bool IsInitialized { get; }

        public Task PersistAsync(
            SetupConfiguration configuration,
            string passwordHash,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;
    }

    private sealed class StubSetupInitializationService : ISetupInitializationService
    {
        public SetupRequest? LastRequest { get; private set; }

        public Task InitializeAsync(SetupRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.CompletedTask;
        }
    }
}
