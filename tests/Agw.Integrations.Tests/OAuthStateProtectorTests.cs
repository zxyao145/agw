using Agw.Integrations.Application.OAuth;
using Agw.Integrations.Contracts.OAuth;
using Agw.Shared.Exceptions;
using Agw.Testing;

using Microsoft.AspNetCore.DataProtection;

namespace Agw.Integrations.Tests;

public sealed class OAuthStateProtectorTests
{
    private const string CallbackUri = "https://agw.test/api/integrations/oauth/callback";

    [Fact]
    public void Protect_ValidState_IsOpaqueAndRoundTrips()
    {
        var now = new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);
        var service = CreateService(new TestTimeProvider(now));
        var connectionId = Guid.CreateVersion7();

        var protectedState = service.Protect(
            connectionId,
            "verifier-secret",
            "/integrations/callback?from=settings",
            CallbackUri,
            OAuthCompletionTarget.Desktop);

        Assert.DoesNotContain(connectionId.ToString(), protectedState, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("verifier-secret", protectedState, StringComparison.Ordinal);
        Assert.DoesNotContain("integrations", protectedState, StringComparison.OrdinalIgnoreCase);
        Assert.True(service.TryUnprotect(protectedState, out var state));
        Assert.NotNull(state);
        Assert.Equal(connectionId, state.ConnectionId);
        Assert.Equal("verifier-secret", state.PkceVerifier);
        Assert.Equal("/integrations/callback?from=settings", state.ReturnPath);
        Assert.Equal(CallbackUri, state.CallbackUri);
        Assert.Equal(OAuthCompletionTarget.Desktop, state.CompletionTarget);
    }

    [Fact]
    public void TryUnprotect_TamperedState_ReturnsFalse()
    {
        var service = CreateService(new TestTimeProvider(DateTimeOffset.UtcNow));
        var protectedState = service.Protect(
            Guid.CreateVersion7(),
            "verifier",
            "/integrations",
            CallbackUri,
            OAuthCompletionTarget.Web);
        var replacement = protectedState[^1] == 'A' ? 'B' : 'A';
        var tamperedState = protectedState[..^1] + replacement;

        Assert.False(service.TryUnprotect(tamperedState, out var state));
        Assert.Null(state);
    }

    [Fact]
    public void TryUnprotect_StatePastApplicationLifetime_ReturnsFalse()
    {
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero));
        var service = CreateService(timeProvider);
        var protectedState = service.Protect(
            Guid.CreateVersion7(),
            "verifier",
            "/integrations",
            CallbackUri,
            OAuthCompletionTarget.Web);

        timeProvider.SetUtcNow(timeProvider.GetUtcNow().AddMinutes(11));

        Assert.False(service.TryUnprotect(protectedState, out var state));
        Assert.Null(state);
    }

    [Theory]
    [InlineData("")]
    [InlineData("integrations")]
    [InlineData("//evil.example/path")]
    [InlineData("/\\evil.example/path")]
    [InlineData("/safe\\unsafe")]
    [InlineData("https://evil.example/path")]
    [InlineData("/safe\nunsafe")]
    public void Protect_UnsafeReturnPath_ThrowsStableError(string returnPath)
    {
        var service = CreateService(new TestTimeProvider(DateTimeOffset.UtcNow));

        var exception = Assert.Throws<AgwException>(() =>
            service.Protect(
                Guid.CreateVersion7(),
                "verifier",
                returnPath,
                CallbackUri,
                OAuthCompletionTarget.Web));

        Assert.Equal(ErrorCodes.OAuthReturnPathInvalid.Code, exception.Code);
    }

    private static OAuthStateProtector CreateService(TimeProvider timeProvider)
    {
        return new OAuthStateProtector(new EphemeralDataProtectionProvider(), timeProvider);
    }
}
