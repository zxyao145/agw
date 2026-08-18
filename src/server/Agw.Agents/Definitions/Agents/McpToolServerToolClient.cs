using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using ModelContextProtocol.Client;

namespace Agw.Agents.Definitions.Agents;

public static class McpToolServerToolClient
{
    public static async Task<IReadOnlyList<McpClientTool>> ListToolsAsync(
        McpServer server,
        CancellationToken cancellationToken = default
    )
    {
        return await ListToolsAsync(server, null, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<McpClientTool>> ListToolsAsync(
        McpServer server,
        IReadOnlyDictionary<string, string>? environmentVariables,
        CancellationToken cancellationToken = default
    )
    {
        var transport = CreateTransport(server, environmentVariables);
        var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken).ConfigureAwait(false);
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return tools.AsReadOnly();
    }

    private static IClientTransport CreateTransport(
        McpServer server,
        IReadOnlyDictionary<string, string>? environmentVariables
    )
    {
        return server.TransportType.ToLowerInvariant() switch
        {
            "stdio" => CreateStdioTransport(server, environmentVariables),
            "http" or "sse" => CreateHttpTransport(server),
            _ => throw new AgwException(
                ErrorCodes.UnsupportedTransportType,
                $"Transport type '{server.TransportType}' is not supported"
            ),
        };
    }

    private static StdioClientTransport CreateStdioTransport(
        McpServer server,
        IReadOnlyDictionary<string, string>? environmentVariables
    )
    {
        if (string.IsNullOrWhiteSpace(server.Command))
        {
            throw new AgwException(
                ErrorCodes.McpStdioCommandRequired,
                $"MCP server '{server.Id}' uses stdio transport but has no command configured"
            );
        }

        var options = new StdioClientTransportOptions
        {
            Name = server.Name,
            Command = server.Command,
            Arguments = [.. server.Arguments],
        };

        var mergedEnvironmentVariables = MergeEnvironmentVariables(server.EnvironmentVariables, environmentVariables);
        if (mergedEnvironmentVariables != null)
        {
            options.EnvironmentVariables = mergedEnvironmentVariables;
        }

        if (!string.IsNullOrWhiteSpace(server.WorkingDirectory))
        {
            options.WorkingDirectory = server.WorkingDirectory;
        }

        return new StdioClientTransport(options);
    }

    internal static Dictionary<string, string?>? MergeEnvironmentVariables(
        IReadOnlyDictionary<string, string> serverVariables,
        IReadOnlyDictionary<string, string>? effectiveAgentVariables
    )
    {
        if (serverVariables.Count == 0 && effectiveAgentVariables is not { Count: > 0 })
        {
            return null;
        }

        var merged = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in serverVariables)
        {
            merged[key] = value;
        }

        if (effectiveAgentVariables != null)
        {
            foreach (var (key, value) in effectiveAgentVariables)
            {
                merged[key] = value;
            }
        }

        return merged;
    }

    private static HttpClientTransport CreateHttpTransport(McpServer server)
    {
        if (string.IsNullOrWhiteSpace(server.Url))
        {
            throw new AgwException(
                ErrorCodes.McpHttpUrlRequired,
                $"MCP server '{server.Id}' uses HTTP/SSE transport but has no URL configured"
            );
        }

        var options = new HttpClientTransportOptions { Name = server.Name, Endpoint = new Uri(server.Url) };

        if (server.Headers is { Count: > 0 })
        {
            options.AdditionalHeaders = new Dictionary<string, string>(server.Headers);
        }

        return new HttpClientTransport(options);
    }
}
