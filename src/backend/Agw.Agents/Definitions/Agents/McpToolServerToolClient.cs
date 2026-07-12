using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;

using ModelContextProtocol.Client;

namespace Agw.Agents.Definitions.Agents;

public static class McpToolServerToolClient
{
    public static async Task<IReadOnlyList<McpClientTool>> ListToolsAsync(
        McpServer server,
        CancellationToken cancellationToken = default)
    {
        var transport = CreateTransport(server);
        var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken).ConfigureAwait(false);
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return tools.AsReadOnly();
    }

    private static IClientTransport CreateTransport(McpServer server)
    {
        return server.TransportType.ToLowerInvariant() switch
        {
            "stdio" => CreateStdioTransport(server),
            "http" or "sse" => CreateHttpTransport(server),
            _ => throw new AgwException(ErrorCodes.UnsupportedTransportType, $"Transport type '{server.TransportType}' is not supported")
        };
    }

    private static StdioClientTransport CreateStdioTransport(McpServer server)
    {
        if (string.IsNullOrWhiteSpace(server.Command))
        {
            throw new AgwException(ErrorCodes.McpStdioCommandRequired, $"MCP server '{server.Id}' uses stdio transport but has no command configured");
        }

        var options = new StdioClientTransportOptions
        {
            Name = server.Name,
            Command = server.Command,
            Arguments = [.. server.Arguments],
        };

        if (server.EnvironmentVariables.Count > 0)
        {
            options.EnvironmentVariables = new Dictionary<string, string?>(
                server.EnvironmentVariables.Select(kvp => new KeyValuePair<string, string?>(kvp.Key, kvp.Value)));
        }

        if (!string.IsNullOrWhiteSpace(server.WorkingDirectory))
        {
            options.WorkingDirectory = server.WorkingDirectory;
        }

        return new StdioClientTransport(options);
    }

    private static HttpClientTransport CreateHttpTransport(McpServer server)
    {
        if (string.IsNullOrWhiteSpace(server.Url))
        {
            throw new AgwException(ErrorCodes.McpHttpUrlRequired, $"MCP server '{server.Id}' uses HTTP/SSE transport but has no URL configured");
        }

        var options = new HttpClientTransportOptions
        {
            Name = server.Name,
            Endpoint = new Uri(server.Url),
        };

        if (server.Headers is { Count: > 0 })
        {
            options.AdditionalHeaders = new Dictionary<string, string>(server.Headers);
        }

        return new HttpClientTransport(options);
    }
}
