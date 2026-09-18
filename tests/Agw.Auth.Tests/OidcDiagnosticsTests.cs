using System.Net;
using Agw.Auth.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Xunit;

namespace Agw.Auth.Tests;

public sealed class OidcDiagnosticsTests
{
    [Theory]
    [InlineData("token-exchange", "provider-unavailable")]
    [InlineData("provisioning", "provisioning-failed")]
    [InlineData("desktop-grant", "grant-creation-failed")]
    public void Failure_WrappedUpstreamException_RecordsSafeCategoryWithoutSensitivePayload(
        string stage,
        string expected
    )
    {
        var logger = new RecordingLogger();
        using var services = new ServiceCollection().AddSingleton<ILoggerFactory>(logger).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services, TraceIdentifier = "trace-test" };
        var exception = new InvalidOperationException(
            "secret-wrapper",
            new HttpRequestException("secret-token", new IOException("secret-response"))
        );
        Assert.Equal(expected, OidcDiagnostics.Failure(context, "company", "web", stage, exception));
        Assert.Null(logger.Exception);
        Assert.DoesNotContain("secret-", logger.Message);
        Assert.Contains(expected, logger.Message);
        Assert.Contains(stage, logger.Message);
        Assert.Contains(nameof(IOException), logger.Message);
        Assert.Contains("trace-test", logger.Message);
    }

    [Fact]
    public void Failure_NonceError_DistinguishesProtocolValidation()
    {
        var logger = new RecordingLogger();
        using var services = new ServiceCollection().AddSingleton<ILoggerFactory>(logger).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        Assert.Equal(
            "invalid-nonce",
            OidcDiagnostics.Failure(
                context,
                "company",
                "web",
                "token-validation",
                new OpenIdConnectProtocolInvalidNonceException("secret-nonce")
            )
        );
        Assert.DoesNotContain("secret-nonce", logger.Message);
    }

    [Fact]
    public void Failure_HttpRequestExceptionWithStatusCode_RecordsProviderRejectedAndStatus()
    {
        var logger = new RecordingLogger();
        using var services = new ServiceCollection().AddSingleton<ILoggerFactory>(logger).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services, TraceIdentifier = "trace-test" };
        var exception = new HttpRequestException("upstream", null, HttpStatusCode.Forbidden);

        Assert.Equal(
            "provider-rejected",
            OidcDiagnostics.Failure(context, "github", "web", "oauth-userinfo", exception)
        );
        Assert.Contains("provider-rejected", logger.Message);
        Assert.Contains("403", logger.Message);
        Assert.Null(logger.Exception);
    }

    [Fact]
    public void Failure_HttpRequestExceptionWithoutStatusCode_RecordsProviderUnavailable()
    {
        var logger = new RecordingLogger();
        using var services = new ServiceCollection().AddSingleton<ILoggerFactory>(logger).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services, TraceIdentifier = "trace-test" };
        var exception = new HttpRequestException("connection reset");

        Assert.Equal(
            "provider-unavailable",
            OidcDiagnostics.Failure(context, "github", "web", "oauth-userinfo", exception)
        );
        Assert.Contains("provider-unavailable", logger.Message);
    }

    private sealed class RecordingLogger : ILoggerFactory, ILogger
    {
        public string Message { get; private set; } = "";
        public Exception? Exception { get; private set; }

        public ILogger CreateLogger(string categoryName) => this;

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            Message = formatter(state, exception);
            Exception = exception;
        }
    }
}
