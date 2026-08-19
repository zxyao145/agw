using Agw.Shared.Exceptions;

namespace Agw.Integrations.Mcp;

public sealed class McpToolMaterializer : IMcpToolMaterializer
{
    private readonly IMcpMaterializerFactory _factory;

    public McpToolMaterializer(IHttpClientFactory httpClientFactory)
        : this(new DefaultMcpMaterializerFactory(httpClientFactory)) { }

    internal McpToolMaterializer(IMcpMaterializerFactory factory)
    {
        _factory = factory;
    }

    public async Task<ConnectionToolLease> MaterializeAsync(
        McpEndpointDescriptor descriptor,
        McpRuntimeOverrides? runtimeOverrides = null,
        CancellationToken cancellationToken = default
    )
    {
        if (descriptor == null)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }

        var resolvedEndpoint = ResolveEndpoint(descriptor, runtimeOverrides);
        IMcpInitialTransport? transport = null;
        IMcpMaterializerClient? client = null;
        try
        {
            transport = _factory.CreateTransport(resolvedEndpoint);
            client = await _factory.CreateClientAsync(transport, cancellationToken).ConfigureAwait(false);
            var tools = await client.ListToolsAsync(cancellationToken).ConfigureAwait(false);
            return new ConnectionToolLease(tools, [client, transport]);
        }
        catch (OperationCanceledException)
        {
            await DisposePartialAsync(client, transport).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await DisposePartialAsync(client, transport).ConfigureAwait(false);
            throw new AgwException(ErrorCodes.CannotCreateInstance, "Failed to materialize MCP tools.");
        }
    }

    private static ResolvedMcpEndpoint ResolveEndpoint(
        McpEndpointDescriptor descriptor,
        McpRuntimeOverrides? runtimeOverrides
    )
    {
        return descriptor switch
        {
            McpStdioEndpointDescriptor stdio => ResolveStdioEndpoint(stdio, runtimeOverrides),
            McpHttpEndpointDescriptor http => ResolveHttpEndpoint(
                http.Name,
                http.Endpoint,
                http.Headers,
                http.CredentialHeaders,
                runtimeOverrides,
                useSse: false
            ),
            McpSseEndpointDescriptor sse => ResolveHttpEndpoint(
                sse.Name,
                sse.Endpoint,
                sse.Headers,
                sse.CredentialHeaders,
                runtimeOverrides,
                useSse: true
            ),
            _ => throw new AgwException(ErrorCodes.UnsupportedTransportType),
        };
    }

    private static ResolvedMcpStdioEndpoint ResolveStdioEndpoint(
        McpStdioEndpointDescriptor descriptor,
        McpRuntimeOverrides? runtimeOverrides
    )
    {
        if (string.IsNullOrWhiteSpace(descriptor.Command))
        {
            throw new AgwException(ErrorCodes.McpStdioCommandRequired);
        }

        var environmentVariables = MergeValues(
            descriptor.EnvironmentVariables,
            runtimeOverrides?.EnvironmentVariables,
            descriptor.CredentialEnvironmentVariables,
            StringComparer.OrdinalIgnoreCase
        );
        return new ResolvedMcpStdioEndpoint(
            descriptor.Name,
            descriptor.Command,
            descriptor.Arguments,
            descriptor.WorkingDirectory,
            environmentVariables
        );
    }

    private static ResolvedMcpHttpEndpoint ResolveHttpEndpoint(
        string name,
        Uri? endpoint,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> credentialHeaders,
        McpRuntimeOverrides? runtimeOverrides,
        bool useSse
    )
    {
        if (endpoint == null)
        {
            throw new AgwException(ErrorCodes.McpHttpUrlRequired);
        }

        if (!endpoint.IsAbsoluteUri || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new AgwException(ErrorCodes.InvalidUrl);
        }

        var mergedHeaders = MergeValues(
            headers,
            runtimeOverrides?.Headers,
            credentialHeaders,
            StringComparer.OrdinalIgnoreCase
        );
        return new ResolvedMcpHttpEndpoint(name, endpoint, mergedHeaders, useSse);
    }

    private static IReadOnlyDictionary<string, string> MergeValues(
        IReadOnlyDictionary<string, string> configuredValues,
        IReadOnlyDictionary<string, string>? runtimeValues,
        IReadOnlyDictionary<string, string> credentialValues,
        StringComparer comparer
    )
    {
        var merged = new Dictionary<string, string>(comparer);
        AddValues(merged, configuredValues);
        AddValues(merged, runtimeValues);
        AddCredentialValues(merged, credentialValues, comparer);
        return merged;
    }

    private static void AddCredentialValues(
        IDictionary<string, string> destination,
        IReadOnlyDictionary<string, string> credentials,
        StringComparer comparer
    )
    {
        foreach (var (key, value) in credentials)
        {
            var overriddenKey = destination.Keys.FirstOrDefault(existingKey => comparer.Equals(existingKey, key));
            if (overriddenKey != null)
            {
                destination.Remove(overriddenKey);
            }

            destination[key] = value;
        }
    }

    private static void AddValues(IDictionary<string, string> destination, IReadOnlyDictionary<string, string>? source)
    {
        if (source == null)
        {
            return;
        }

        foreach (var (key, value) in source)
        {
            destination[key] = value;
        }
    }

    private static async ValueTask DisposePartialAsync(IMcpMaterializerClient? client, IMcpInitialTransport? transport)
    {
        if (client != null)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch { }
        }

        if (transport != null)
        {
            try
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
            catch { }
        }
    }
}
