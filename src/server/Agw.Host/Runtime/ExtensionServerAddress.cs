using Agw.Extensions.Contracts;
using Agw.Shared.Exceptions;

namespace Agw.Host.Runtime;

public sealed class ExtensionServerAddress : IExtensionServerAddress
{
    private readonly IConfiguration _configuration;

    public ExtensionServerAddress(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string GetPublicBaseUrl()
    {
        string? value = _configuration["Extensions:PublicBaseUrl"];
        if (
            !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            || uri.UserInfo.Length != 0
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0
        )
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "Extensions:PublicBaseUrl must be configured with a public HTTPS base URL."
            );
        return uri.AbsoluteUri.TrimEnd('/');
    }
}
