using System.Security.Claims;

using Agw.Host.Controllers;
using Agw.Setup.Contracts;
using Agw.Setup.Services;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Agw.Host.Tests;

public class AuthControllerTests
{
    [Theory]
    [InlineData(7, false)]
    [InlineData(8, true)]
    [InlineData(257, false)]
    public async Task ChangePassword_NewPasswordLength_EnforcesEightToTwoHundredFiftySixCharacters(
        int passwordLength,
        bool shouldUpdatePassword)
    {
        var stateStore = new StateStoreStub();
        var controller = CreateController(stateStore);

        await controller.ChangePassword(
            new AuthController.ChangePasswordRequest(null, new string('a', passwordLength)),
            TestContext.Current.CancellationToken);

        Assert.Equal(shouldUpdatePassword, stateStore.PasswordWasUpdated);
    }

    private static AuthController CreateController(StateStoreStub stateStore)
    {
        var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService, AuthenticationServiceStub>()
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity([], "LocalTrusted"))
        };

        return new AuthController(
            stateStore,
            new PasswordHasher<object>(),
            null!,
            new AuthenticationAttemptLimiter(),
            TimeProvider.System)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    private sealed class StateStoreStub : IInitializationStateStore
    {
        public bool PasswordWasUpdated { get; private set; }

        public InitializationSnapshot GetSnapshot() => new(true, "hash", 1, []);

        public Task PersistAsync(
            SetupRequest request,
            string passwordHash,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<CreatedApiToken> CreateTokenAsync(
            string name,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<bool> RevokeTokenAsync(
            Guid id,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public bool ValidateToken(string token) => false;

        public Task UpdatePasswordAsync(
            string passwordHash,
            CancellationToken cancellationToken = default)
        {
            PasswordWasUpdated = true;
            return Task.CompletedTask;
        }
    }

    private sealed class AuthenticationServiceStub : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) => Task.CompletedTask;

        public Task ForbidAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignInAsync(
            HttpContext context,
            string? scheme,
            ClaimsPrincipal principal,
            AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignOutAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) => Task.CompletedTask;
    }
}
