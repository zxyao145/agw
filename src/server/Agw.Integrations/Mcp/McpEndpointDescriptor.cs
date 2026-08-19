using System.Collections.ObjectModel;

namespace Agw.Integrations.Mcp;

public abstract class McpEndpointDescriptor
{
    protected McpEndpointDescriptor(string name)
    {
        Name = name;
    }

    public string Name { get; }

    internal static IReadOnlyDictionary<string, string> CopyValues(IReadOnlyDictionary<string, string>? values)
    {
        var copy =
            values == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(values, StringComparer.Ordinal);
        return new ReadOnlyDictionary<string, string>(copy);
    }
}

public sealed class McpStdioEndpointDescriptor : McpEndpointDescriptor
{
    public McpStdioEndpointDescriptor(
        string name,
        string? command,
        IReadOnlyList<string>? arguments = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        IReadOnlyDictionary<string, string>? credentialEnvironmentVariables = null
    )
        : base(name)
    {
        Command = command;
        Arguments = arguments?.ToArray() ?? [];
        WorkingDirectory = workingDirectory;
        EnvironmentVariables = CopyValues(environmentVariables);
        CredentialEnvironmentVariables = CopyValues(credentialEnvironmentVariables);
    }

    public string? Command { get; }

    public IReadOnlyList<string> Arguments { get; }

    public string? WorkingDirectory { get; }

    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; }

    public IReadOnlyDictionary<string, string> CredentialEnvironmentVariables { get; }
}

public sealed class McpHttpEndpointDescriptor : McpEndpointDescriptor
{
    public McpHttpEndpointDescriptor(
        string name,
        Uri? endpoint,
        IReadOnlyDictionary<string, string>? headers = null,
        IReadOnlyDictionary<string, string>? credentialHeaders = null
    )
        : base(name)
    {
        Endpoint = endpoint;
        Headers = CopyValues(headers);
        CredentialHeaders = CopyValues(credentialHeaders);
    }

    public Uri? Endpoint { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public IReadOnlyDictionary<string, string> CredentialHeaders { get; }
}

public sealed class McpSseEndpointDescriptor : McpEndpointDescriptor
{
    public McpSseEndpointDescriptor(
        string name,
        Uri? endpoint,
        IReadOnlyDictionary<string, string>? headers = null,
        IReadOnlyDictionary<string, string>? credentialHeaders = null
    )
        : base(name)
    {
        Endpoint = endpoint;
        Headers = CopyValues(headers);
        CredentialHeaders = CopyValues(credentialHeaders);
    }

    public Uri? Endpoint { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public IReadOnlyDictionary<string, string> CredentialHeaders { get; }
}

public sealed class McpRuntimeOverrides
{
    public McpRuntimeOverrides(
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        IReadOnlyDictionary<string, string>? headers = null
    )
    {
        EnvironmentVariables = McpEndpointDescriptor.CopyValues(environmentVariables);
        Headers = McpEndpointDescriptor.CopyValues(headers);
    }

    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }
}
