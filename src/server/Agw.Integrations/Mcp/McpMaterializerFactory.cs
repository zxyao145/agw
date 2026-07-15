using Agw.Shared.Exceptions;

using Microsoft.Extensions.AI;

using ModelContextProtocol.Client;

namespace Agw.Integrations.Mcp;

internal abstract class ResolvedMcpEndpoint
{
    protected ResolvedMcpEndpoint(string name)
    {
        Name = name;
    }

    public string Name { get; }
}

internal sealed class ResolvedMcpStdioEndpoint : ResolvedMcpEndpoint
{
    public ResolvedMcpStdioEndpoint(
        string name,
        string command,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string> environmentVariables)
        : base(name)
    {
        Command = command;
        Arguments = arguments;
        WorkingDirectory = workingDirectory;
        EnvironmentVariables = environmentVariables;
    }

    public string Command { get; }

    public IReadOnlyList<string> Arguments { get; }

    public string? WorkingDirectory { get; }

    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; }
}

internal sealed class ResolvedMcpHttpEndpoint : ResolvedMcpEndpoint
{
    public ResolvedMcpHttpEndpoint(
        string name,
        Uri endpoint,
        IReadOnlyDictionary<string, string> headers,
        bool useSse)
        : base(name)
    {
        Endpoint = endpoint;
        Headers = headers;
        UseSse = useSse;
    }

    public Uri Endpoint { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public bool UseSse { get; }
}

internal interface IMcpInitialTransport : IAsyncDisposable
{
}

internal interface IMcpMaterializerClient : IAsyncDisposable
{
    Task<IReadOnlyList<AITool>> ListToolsAsync(CancellationToken cancellationToken);
}

internal interface IMcpMaterializerFactory
{
    IMcpInitialTransport CreateTransport(ResolvedMcpEndpoint endpoint);

    Task<IMcpMaterializerClient> CreateClientAsync(
        IMcpInitialTransport transport,
        CancellationToken cancellationToken);
}

internal sealed class DefaultMcpMaterializerFactory : IMcpMaterializerFactory
{
    private readonly IHttpClientFactory _httpClientFactory;

    public DefaultMcpMaterializerFactory(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public IMcpInitialTransport CreateTransport(ResolvedMcpEndpoint endpoint)
    {
        return endpoint switch
        {
            ResolvedMcpStdioEndpoint stdio => CreateStdioTransport(stdio),
            ResolvedMcpHttpEndpoint http => CreateHttpTransport(http),
            _ => throw new AgwException(ErrorCodes.UnsupportedTransportType),
        };
    }

    public async Task<IMcpMaterializerClient> CreateClientAsync(
        IMcpInitialTransport transport,
        CancellationToken cancellationToken)
    {
        var sdkTransport = (SdkInitialTransport)transport;
        var client = await McpClient.CreateAsync(
                sdkTransport.Transport,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new SdkMcpMaterializerClient(client);
    }

    private static IMcpInitialTransport CreateStdioTransport(ResolvedMcpStdioEndpoint endpoint)
    {
        var transport = new StdioClientTransport(CreateStdioTransportOptions(endpoint));
        return new SdkInitialTransport(transport, owner: null);
    }

    internal static StdioClientTransportOptions CreateStdioTransportOptions(
        ResolvedMcpStdioEndpoint endpoint)
    {
        var options = new StdioClientTransportOptions
        {
            Name = endpoint.Name,
            Command = endpoint.Command,
            Arguments = endpoint.Arguments.ToArray(),
            InheritEnvironmentVariables = false,
        };
        if (!string.IsNullOrWhiteSpace(endpoint.WorkingDirectory))
        {
            options.WorkingDirectory = endpoint.WorkingDirectory;
        }

        if (endpoint.EnvironmentVariables.Count > 0)
        {
            options.EnvironmentVariables = endpoint.EnvironmentVariables
                .ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.OrdinalIgnoreCase);
        }

        return options;
    }

    private IMcpInitialTransport CreateHttpTransport(ResolvedMcpHttpEndpoint endpoint)
    {
        var options = new HttpClientTransportOptions
        {
            Name = endpoint.Name,
            Endpoint = endpoint.Endpoint,
            TransportMode = endpoint.UseSse
                ? HttpTransportMode.Sse
                : HttpTransportMode.AutoDetect,
        };
        if (endpoint.Headers.Count > 0)
        {
            options.AdditionalHeaders = new Dictionary<string, string>(
                endpoint.Headers,
                StringComparer.OrdinalIgnoreCase);
        }

        var httpClient = _httpClientFactory.CreateClient();
        try
        {
            var transport = new HttpClientTransport(options, httpClient, ownsHttpClient: true);
            return new SdkInitialTransport(transport, transport);
        }
        catch
        {
            httpClient.Dispose();
            throw;
        }
    }

    private sealed class SdkInitialTransport : IMcpInitialTransport
    {
        private readonly IAsyncDisposable? _owner;
        private int _disposed;

        public SdkInitialTransport(IClientTransport transport, IAsyncDisposable? owner)
        {
            Transport = transport;
            _owner = owner;
        }

        public IClientTransport Transport { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0 || _owner == null)
            {
                return;
            }

            await _owner.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class SdkMcpMaterializerClient : IMcpMaterializerClient
    {
        private readonly McpClient _client;

        public SdkMcpMaterializerClient(McpClient client)
        {
            _client = client;
        }

        public async Task<IReadOnlyList<AITool>> ListToolsAsync(CancellationToken cancellationToken)
        {
            var tools = await _client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return tools.Cast<AITool>().ToArray();
        }

        public ValueTask DisposeAsync()
        {
            return _client.DisposeAsync();
        }
    }
}
