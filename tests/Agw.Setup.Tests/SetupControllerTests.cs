using System.Net;
using Agw.Auth.Security;
using Agw.Setup.Contracts;
using Agw.Setup.Controllers;
using Agw.Setup.Services;
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
    public void Index_WhenSetupIsRequired_RequestsAdministratorPassword()
    {
        var controller = CreateController(isInitialized: false);

        var result = Assert.IsType<ViewResult>(controller.Index());

        Assert.Empty(Assert.IsType<SetupRequest>(result.Model).AdminPassword);
        Assert.Equal(false, controller.ViewData["RequireSetupCode"]);
    }

    [Fact]
    public async Task IndexPost_WhenInitializationSucceeds_RedirectsToRoot()
    {
        var initializationService = new StubSetupInitializationService();
        var controller = CreateController(isInitialized: false, initializationService: initializationService);
        var request = CreateRequest();

        var result = await controller.Index(request, TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/", redirect.Url);
        Assert.Same(request, initializationService.LastRequest);
    }

    [Fact]
    public async Task IndexPost_WhenRemoteSetupCodeIsMissing_ReturnsFormWithoutInitializing()
    {
        var initializationService = new StubSetupInitializationService();
        var controller = CreateController(isInitialized: false, initializationService: initializationService);
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
        controller.HttpContext.Request.Host = new HostString("agw.example.com");

        var result = await controller.Index(CreateRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<ViewResult>(result);
        Assert.Null(initializationService.LastRequest);
        Assert.True(controller.ModelState.ContainsKey(nameof(SetupRequest.SetupCode)));
    }

    [Fact]
    public async Task IndexPost_WhenRemoteSetupCodeIsValid_InitializesAndConsumesCode()
    {
        var service = new StubSetupInitializationService();
        var controller = CreateController(isInitialized: false, initializationService: service);
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
        controller.HttpContext.Request.Host = new HostString("agw.example.com");
        var request = CreateRequest();
        request.SetupCode = "TEST-CODE";

        Assert.IsType<RedirectResult>(await controller.Index(request, TestContext.Current.CancellationToken));
        service.LastRequest = null;
        var repeated = await controller.Index(request, TestContext.Current.CancellationToken);

        Assert.IsType<ViewResult>(repeated);
        Assert.Null(service.LastRequest);
        Assert.True(controller.ModelState.ContainsKey(nameof(SetupRequest.SetupCode)));
    }

    [Fact]
    public async Task IndexPost_WhenPasswordValidationFails_DoesNotInitialize()
    {
        var service = new StubSetupInitializationService();
        var controller = CreateController(isInitialized: false, initializationService: service);
        controller.ModelState.AddModelError(nameof(SetupRequest.AdminPassword), "Password is required.");

        var result = await controller.Index(new SetupRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<ViewResult>(result);
        Assert.Null(service.LastRequest);
    }

    private static SetupController CreateController(
        bool isInitialized = true,
        StubSetupInitializationService? initializationService = null
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
            TimeProvider.System
        )
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private static SetupRequest CreateRequest() => new() { AdminPassword = "administrator-password" };

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

        public Task PersistAsync(string passwordHash, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubSetupInitializationService : ISetupInitializationService
    {
        public SetupRequest? LastRequest { get; set; }

        public Task InitializeAsync(SetupRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.CompletedTask;
        }
    }
}
