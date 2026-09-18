using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Agw.Auth.Application;

public static class OidcDiagnostics
{
    public const string StageKey = "agw:oidc_stage";

    public static string Failure(HttpContext context, string provider, string client, string stage, Exception? failure)
    {
        var category = stage switch
        {
            "provisioning" => "provisioning-failed",
            "desktop-grant" => "grant-creation-failed",
            "session" => "session-creation-failed",
            _ => "protocol-validation-failed",
        };
        var cause = failure;
        int? status = null;
        for (var depth = 0; depth < 8 && cause != null; depth++)
        {
            // Local persistence failures must not be mistaken for an unavailable IdP.
            if (stage is not ("provisioning" or "desktop-grant" or "session"))
                category = cause switch
                {
                    // A status code means the provider answered (e.g. 403); a status-less
                    // HttpRequestException means the request never completed (network failure).
                    HttpRequestException { StatusCode: not null } => "provider-rejected",
                    HttpRequestException => "provider-unavailable",
                    OperationCanceledException => "provider-timeout",
                    OpenIdConnectProtocolInvalidNonceException => "invalid-nonce",
                    OpenIdConnectProtocolInvalidStateException => "invalid-state",
                    SecurityTokenException => "invalid-token",
                    OpenIdConnectProtocolException => "protocol-rejected",
                    _ => category,
                };
            if (status == null && cause is HttpRequestException { StatusCode: { } code })
                status = (int)code;
            if (cause.InnerException == null)
                break;
            cause = cause.InnerException;
        }
        // Never attach the exception object/message: upstream text may contain credentials.
        context
            .RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Agw.Auth.Oidc")
            .LogWarning(
                "OIDC/OAuth2 failure for {ProviderId}, {Client}, stage {Stage}, category {FailureCategory}, exception {ExceptionType}, status {StatusCode}, trace {TraceId}.",
                provider,
                client,
                stage,
                category,
                cause?.GetType().FullName ?? "none",
                status,
                Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier
            );
        OidcTelemetry.Failure(provider, client, stage, category);
        return category;
    }
}
