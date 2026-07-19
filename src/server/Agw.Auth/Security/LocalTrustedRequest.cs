using System.Net;

using Microsoft.AspNetCore.Http;

namespace Agw.Auth.Security;

public static class LocalTrustedRequest
{
    private static readonly string[] ForwardingHeaders =
    [
        "Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto",
        "X-Original-For", "X-Original-Host", "X-Original-Proto"
    ];

    public static bool IsLocalTrusted(HttpContext context)
    {
        var remoteAddress = context.Connection.RemoteIpAddress;
        if (remoteAddress == null || !IPAddress.IsLoopback(remoteAddress)) return false;
        if (ForwardingHeaders.Any(context.Request.Headers.ContainsKey)) return false;

        var host = context.Request.Host.Host;
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var hostAddress) && IPAddress.IsLoopback(hostAddress));
    }

    public static bool IsSameOrigin(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)) return false;
        return string.Equals(originUri.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(originUri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDesktopOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)) return false;
        return string.Equals(originUri.Scheme, "agw", StringComparison.OrdinalIgnoreCase)
            && string.Equals(originUri.Host, "app", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(originUri.UserInfo)
            && originUri.Port == -1
            && originUri.AbsolutePath is "" or "/"
            && string.IsNullOrEmpty(originUri.Query)
            && string.IsNullOrEmpty(originUri.Fragment);
    }
}
