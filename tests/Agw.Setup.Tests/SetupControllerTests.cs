using Agw.Setup.Contracts;
using Agw.Setup.Controllers;
using Agw.Setup.Services;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

using Xunit;

namespace Agw.Setup.Tests;

public sealed class SetupControllerTests
{
    [Fact]
    public void Index_WhenSetupIsComplete_ReturnsApiResult()
    {
        var controller = CreateController();

        var result = controller.Index();

        AssertApiResult(result);
    }

    [Fact]
    public async Task IndexPost_WhenSetupIsComplete_ReturnsApiResult()
    {
        var controller = CreateController();

        var result = await controller.Index(new SetupRequest(), TestContext.Current.CancellationToken);

        AssertApiResult(result);
    }

    private static SetupController CreateController()
    {
        return new SetupController(
            new InitializedStateStore(),
            new StubSetupInitializationService(),
            new SetupCodeService("TEST-CODE"),
            new AuthenticationAttemptLimiter(),
            TimeProvider.System)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    private static void AssertApiResult(IActionResult result)
    {
        Assert.StartsWith("Bens.Results.ApiResult", result.GetType().FullName);
    }

    private sealed class InitializedStateStore : IInitializationStateStore
    {
        public InitializationSnapshot GetSnapshot() => new(true, null, 0, []);

        public Task PersistAsync(
            SetupRequest request,
            string passwordHash,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<CreatedApiToken> CreateTokenAsync(
            string name,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> RevokeTokenAsync(
            Guid id,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public bool ValidateToken(string token) => false;

        public Task UpdatePasswordAsync(
            string passwordHash,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubSetupInitializationService : ISetupInitializationService
    {
        public Task InitializeAsync(
            SetupRequest request,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
