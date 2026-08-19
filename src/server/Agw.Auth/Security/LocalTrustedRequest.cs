using System.Net;
using Microsoft.AspNetCore.Http;

namespace Agw.Auth.Security;

/// <summary>
/// 提供本机可信请求、同源请求和 Desktop 来源的安全判定。
/// </summary>
public static class LocalTrustedRequest
{
    private const string DevelopmentDesktopOrigin = "http://localhost:3000";

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

    /// <summary>
    /// 判断 Origin 是否来自 Agw Desktop；开发环境可额外允许默认的本地渲染器地址。
    /// </summary>
    /// <param name="origin">待验证的 Origin。</param>
    /// <param name="allowDevelopmentOrigin">是否允许 Desktop 开发服务器的 Origin。</param>
    /// <returns>Origin 属于受信任的 Desktop 来源时返回 <see langword="true"/>。</returns>
    public static bool IsDesktopOrigin(string origin, bool allowDevelopmentOrigin = false)
    {
        if (
            allowDevelopmentOrigin
            && string.Equals(origin, DevelopmentDesktopOrigin, StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
            return false;
        return string.Equals(originUri.Scheme, "agw", StringComparison.OrdinalIgnoreCase)
            && string.Equals(originUri.Host, "app", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(originUri.UserInfo)
            && originUri.Port == -1
            && originUri.AbsolutePath is "" or "/"
            && string.IsNullOrEmpty(originUri.Query)
            && string.IsNullOrEmpty(originUri.Fragment);
    }
}
