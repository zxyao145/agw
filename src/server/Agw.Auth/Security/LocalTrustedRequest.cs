using System.Net;
using Microsoft.AspNetCore.Http;

namespace Agw.Auth.Security;

/// <summary>
/// 提供本机可信请求和同源请求的安全判定。
/// </summary>
public static class LocalTrustedRequest
{
    private static readonly string[] ForwardingHeaders =
    [
        "Forwarded",
        "X-Forwarded-For",
        "X-Forwarded-Host",
        "X-Forwarded-Proto",
        "X-Original-For",
        "X-Original-Host",
        "X-Original-Proto",
    ];

    /// <summary>
    /// 判断请求是否直接来自本机回环地址，且没有经过转发代理。
    /// </summary>
    /// <param name="context">当前 HTTP 上下文。</param>
    /// <returns>请求满足本机可信条件时返回 <see langword="true"/>。</returns>
    public static bool IsLocalTrusted(HttpContext context)
    {
        var remoteAddress = context.Connection.RemoteIpAddress;
        if (remoteAddress == null || !IPAddress.IsLoopback(remoteAddress))
            return false;
        if (ForwardingHeaders.Any(context.Request.Headers.ContainsKey))
            return false;

        var host = context.Request.Host.Host;
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var hostAddress) && IPAddress.IsLoopback(hostAddress));
    }

    /// <summary>
    /// 判断请求的 Origin 是否与当前请求地址同源。
    /// </summary>
    /// <param name="context">当前 HTTP 上下文。</param>
    /// <returns>Origin 与请求的协议及主机一致时返回 <see langword="true"/>。</returns>
    public static bool IsSameOrigin(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
            return false;
        return string.Equals(originUri.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(originUri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
}
