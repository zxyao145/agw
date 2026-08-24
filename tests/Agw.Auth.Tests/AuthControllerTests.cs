using System.Security.Claims;
using Agw.Auth.Api;
using Agw.Auth.Application;
using Agw.Auth.Contracts;
using Agw.Shared;
using Agw.Shared.Exceptions;
using Bens.Results;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agw.Auth.Tests;

public sealed class AuthControllerTests
{
    [Theory]
    [InlineData(7, false)]
    [InlineData(8, true)]
    [InlineData(257, false)]
    public async Task ChangePassword_NewPasswordLength_EnforcesEightToTwoHundredFiftySixCharacters(
        int passwordLength,
        bool shouldUpdatePassword
    )
    {
        var stateStore = new StateStoreStub();
        var controller = CreateController(stateStore, AgwAuthDefaults.LocalTrustedScheme);

        await controller.ChangePassword(
            new ChangePasswordRequest(null, new string('a', passwordLength)),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(shouldUpdatePassword, stateStore.PasswordWasUpdated);
    }

    [Fact]
    public async Task Login_ValidPassword_SignsInAdministratorWithSessionVersion()
    {
        var passwordHasher = new PasswordHasher<object>();
        var stateStore = new StateStoreStub
        {
            PasswordHash = passwordHasher.HashPassword(new object(), "correct-password"),
            SessionVersion = 7,
        };
        var authentication = new AuthenticationServiceStub();
        var controller = CreateController(stateStore, null, authentication, passwordHasher);

        await controller.Login(new LoginRequest("correct-password"));

        Assert.NotNull(authentication.SignedInPrincipal);
        Assert.Equal(Constants.AdminUserName, authentication.SignedInPrincipal.Identity?.Name);
        Assert.Equal(
            Constants.AdminUserId,
            authentication.SignedInPrincipal.FindFirst(ClaimTypes.NameIdentifier)?.Value
        );
        Assert.Equal("7", authentication.SignedInPrincipal.FindFirst(AgwAuthDefaults.SessionVersionClaimType)?.Value);
    }

    [Fact]
    public async Task Login_InvalidPassword_DoesNotSignIn()
    {
        var passwordHasher = new PasswordHasher<object>();
        var stateStore = new StateStoreStub
        {
            PasswordHash = passwordHasher.HashPassword(new object(), "correct-password"),
        };
        var authentication = new AuthenticationServiceStub();
        var controller = CreateController(stateStore, null, authentication, passwordHasher);

        await controller.Login(new LoginRequest("wrong-password"));

        Assert.Null(authentication.SignedInPrincipal);
    }

    [Fact]
    public async Task Login_AfterFiveFailures_DoesNotAuthenticateValidPassword()
    {
        var now = DateTimeOffset.Parse("2026-07-19T00:00:00Z");
        var passwordHasher = new PasswordHasher<object>();
        var stateStore = new StateStoreStub
        {
            PasswordHash = passwordHasher.HashPassword(new object(), "correct-password"),
        };
        var authentication = new AuthenticationServiceStub();
        var attemptLimiter = new AuthenticationAttemptLimiter();
        for (var i = 0; i < 5; i++)
            attemptLimiter.RecordFailure("unknown", now);
        var controller = CreateController(
            stateStore,
            null,
            authentication,
            passwordHasher,
            attemptLimiter,
            new FixedTimeProvider(now)
        );

        var result = await controller.Login(new LoginRequest("correct-password"));

        Assert.Null(authentication.SignedInPrincipal);
        var apiResult = Assert.IsAssignableFrom<IApiResult>(result);
        Assert.Equal(ErrorCodes.TooManyAuthenticationAttempts.Code, apiResult.Code);
        Assert.Equal(StatusCodes.Status429TooManyRequests, apiResult.StatusCode);
    }

    [Fact]
    public async Task CreateToken_LocalTrusted_CreatesNamedToken()
    {
        var stateStore = new StateStoreStub();
        var controller = CreateController(stateStore, AgwAuthDefaults.LocalTrustedScheme);

        await controller.CreateToken(new CreateTokenRequest("Desktop"), TestContext.Current.CancellationToken);

        Assert.Equal("Desktop", stateStore.CreatedTokenName);
    }

    [Fact]
    public async Task CreateToken_Bearer_DoesNotCreateToken()
    {
        var stateStore = new StateStoreStub();
        var controller = CreateController(stateStore, AgwAuthDefaults.BearerScheme);

        await controller.CreateToken(new CreateTokenRequest("Desktop"), TestContext.Current.CancellationToken);

        Assert.Null(stateStore.CreatedTokenName);
    }

    [Fact]
    public void Session_Bearer_ReturnsStableUserId()
    {
        var controller = CreateController(new StateStoreStub(), AgwAuthDefaults.BearerScheme, userId: "user-42");

        var response = Assert.IsType<SessionResponse>(ReadApiResultData(controller.Session()));

        Assert.True(response.Authenticated);
        Assert.Equal("bearer", response.AccessMode);
        Assert.Equal("user-42", response.UserId);
    }

    private static object ReadApiResultData(IActionResult result)
    {
        Assert.StartsWith("Bens.Results.ApiResult", result.GetType().FullName);
        var property = result.GetType().GetProperty("Data");
        Assert.NotNull(property);
        var data = property.GetValue(result);
        Assert.NotNull(data);
        return data;
    }

    private static AuthController CreateController(
        StateStoreStub stateStore,
        string? authenticationType,
        AuthenticationServiceStub? authenticationService = null,
        IPasswordHasher<object>? passwordHasher = null,
        AuthenticationAttemptLimiter? attemptLimiter = null,
        TimeProvider? timeProvider = null,
        string userId = Constants.AdminUserId
    )
    {
        var authentication = authenticationService ?? new AuthenticationServiceStub();
        var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(authentication)
            .BuildServiceProvider();
        var identity =
            authenticationType == null
                ? new ClaimsIdentity()
                : new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], authenticationType);
        var principal = new ClaimsPrincipal(identity);
        var httpContext = new DefaultHttpContext { RequestServices = services, User = principal };

        return new AuthController(
            stateStore,
            stateStore,
            passwordHasher ?? new PasswordHasher<object>(),
            null!,
            attemptLimiter ?? new AuthenticationAttemptLimiter(),
            timeProvider ?? TimeProvider.System,
            new UserInfoServiceStub(principal, authenticationType == null ? null : userId)
        )
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private sealed class UserInfoServiceStub : IUserInfoService
    {
        public UserInfoServiceStub(ClaimsPrincipal current, string? userId)
        {
            Current = current;
            UserId = userId;
        }

        public ClaimsPrincipal? Current { get; set; }

        public string? UserId { get; }

        public bool IsAuthenticated => Current?.Identity?.IsAuthenticated == true;

        public string RequiredUserId => UserId ?? throw new AgwException(ErrorCodes.AuthenticationRequired);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class StateStoreStub : IAuthenticationStateStore, IApiTokenStore
    {
        public string? PasswordHash { get; set; } = "hash";
        public int SessionVersion { get; set; } = 1;
        public bool PasswordWasUpdated { get; private set; }
        public string? CreatedTokenName { get; private set; }

        public AuthenticationSnapshot GetAuthenticationSnapshot() => new(PasswordHash, SessionVersion);

        public Task<IReadOnlyList<ApiTokenSummary>> ListTokensAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ApiTokenSummary>>([]);

        public Task<CreatedApiToken> CreateTokenAsync(string name, CancellationToken cancellationToken = default)
        {
            CreatedTokenName = name;
            return Task.FromResult(
                new CreatedApiToken(Guid.CreateVersion7(), name, "agw_prefix", DateTimeOffset.UtcNow, "agw_token")
            );
        }

        public Task<bool> RevokeTokenAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<ApiTokenIdentity?> ValidateTokenAsync(
            string token,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<ApiTokenIdentity?>(null);

        public Task UpdatePasswordAsync(string passwordHash, CancellationToken cancellationToken = default)
        {
            PasswordWasUpdated = true;
            return Task.CompletedTask;
        }
    }

    private sealed class AuthenticationServiceStub : IAuthenticationService
    {
        public ClaimsPrincipal? SignedInPrincipal { get; private set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task SignInAsync(
            HttpContext context,
            string? scheme,
            ClaimsPrincipal principal,
            AuthenticationProperties? properties
        )
        {
            SignedInPrincipal = principal;
            return Task.CompletedTask;
        }

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;
    }
}
