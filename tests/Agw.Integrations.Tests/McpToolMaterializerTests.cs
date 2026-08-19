using Agw.Integrations.Mcp;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.AI;

namespace Agw.Integrations.Tests;

public class McpToolMaterializerTests
{
    [Fact]
    public void CreateStdioTransportOptions_DoesNotInheritHostEnvironment()
    {
        var endpoint = new ResolvedMcpStdioEndpoint(
            "stdio-server",
            "server-command",
            [],
            null,
            new Dictionary<string, string> { ["DECLARED_ONLY"] = "configured" }
        );

        var options = DefaultMcpMaterializerFactory.CreateStdioTransportOptions(endpoint);

        Assert.False(options.InheritEnvironmentVariables);
        Assert.Equal("configured", options.EnvironmentVariables!["DECLARED_ONLY"]);
    }

    [Fact]
    public async Task MaterializeAsync_StdioConfiguration_AppliesCredentialsAfterRuntimeOverrides()
    {
        var factory = new RecordingMcpMaterializerFactory();
        var materializer = new McpToolMaterializer(factory);
        var descriptor = new McpStdioEndpointDescriptor(
            name: "stdio-server",
            command: "server-command",
            arguments: ["--stdio"],
            workingDirectory: "/workspace",
            environmentVariables: new Dictionary<string, string>
            {
                ["SHARED"] = "configured",
                ["CONFIG_ONLY"] = "configured",
            },
            credentialEnvironmentVariables: new Dictionary<string, string> { ["TOKEN"] = "credential-token" }
        );
        var runtimeOverrides = new McpRuntimeOverrides(
            environmentVariables: new Dictionary<string, string>
            {
                ["SHARED"] = "runtime",
                ["RUNTIME_ONLY"] = "runtime",
                ["token"] = "untrusted-runtime-token",
            }
        );

        await using var lease = await materializer.MaterializeAsync(
            descriptor,
            runtimeOverrides,
            TestContext.Current.CancellationToken
        );

        var resolved = Assert.IsType<ResolvedMcpStdioEndpoint>(factory.LastEndpoint);
        Assert.Equal("runtime", resolved.EnvironmentVariables["SHARED"]);
        Assert.Equal("configured", resolved.EnvironmentVariables["CONFIG_ONLY"]);
        Assert.Equal("runtime", resolved.EnvironmentVariables["RUNTIME_ONLY"]);
        Assert.Equal("credential-token", resolved.EnvironmentVariables["TOKEN"]);
        Assert.Contains("TOKEN", resolved.EnvironmentVariables.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain("token", resolved.EnvironmentVariables.Keys, StringComparer.Ordinal);
    }

    [Fact]
    public async Task MaterializeAsync_HttpConfiguration_AppliesCredentialsAfterRuntimeOverrides()
    {
        var factory = new RecordingMcpMaterializerFactory();
        var materializer = new McpToolMaterializer(factory);
        var descriptor = new McpHttpEndpointDescriptor(
            name: "http-server",
            endpoint: new Uri("https://mcp.example.test/rpc"),
            headers: new Dictionary<string, string>
            {
                ["X-Shared"] = "configured",
                ["X-Config"] = "configured",
                ["Authorization"] = "configured-token",
            },
            credentialHeaders: new Dictionary<string, string> { ["Authorization"] = "Bearer credential-token" }
        );
        var runtimeOverrides = new McpRuntimeOverrides(
            headers: new Dictionary<string, string>
            {
                ["x-shared"] = "runtime",
                ["X-Runtime"] = "runtime",
                ["authorization"] = "Bearer untrusted-runtime-token",
            }
        );

        await using var lease = await materializer.MaterializeAsync(
            descriptor,
            runtimeOverrides,
            TestContext.Current.CancellationToken
        );

        var resolved = Assert.IsType<ResolvedMcpHttpEndpoint>(factory.LastEndpoint);
        Assert.False(resolved.UseSse);
        Assert.Equal("runtime", resolved.Headers["X-Shared"]);
        Assert.Equal("configured", resolved.Headers["X-Config"]);
        Assert.Equal("runtime", resolved.Headers["X-Runtime"]);
        Assert.Equal("Bearer credential-token", resolved.Headers["Authorization"]);
    }

    [Fact]
    public async Task MaterializeAsync_SseConfiguration_UsesSseHttpTransport()
    {
        var factory = new RecordingMcpMaterializerFactory();
        var materializer = new McpToolMaterializer(factory);
        var descriptor = new McpSseEndpointDescriptor(
            name: "sse-server",
            endpoint: new Uri("https://mcp.example.test/sse")
        );

        await using var lease = await materializer.MaterializeAsync(
            descriptor,
            cancellationToken: TestContext.Current.CancellationToken
        );

        var resolved = Assert.IsType<ResolvedMcpHttpEndpoint>(factory.LastEndpoint);
        Assert.True(resolved.UseSse);
    }

    [Fact]
    public async Task ConnectionToolLease_DisposeAsync_DisposesClientBeforeTransportOnlyOnce()
    {
        var order = new List<string>();
        var factory = new RecordingMcpMaterializerFactory(order);
        var materializer = new McpToolMaterializer(factory);

        var lease = await materializer.MaterializeAsync(
            CreateValidStdioDescriptor(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Single(lease);
        Assert.Single(lease.Tools);
        Assert.Equal("test_tool", lease[0].Name);

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        Assert.Equal(["client", "transport"], order);
        Assert.Equal(1, factory.Client.DisposeCount);
        Assert.Equal(1, factory.Transport.DisposeCount);
    }

    [Fact]
    public async Task MaterializeAsync_WhenClientCreationFails_DisposesInitialTransportAndSanitizesException()
    {
        var factory = new RecordingMcpMaterializerFactory
        {
            ClientCreationException = new InvalidOperationException("credential-token"),
        };
        var materializer = new McpToolMaterializer(factory);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            materializer.MaterializeAsync(
                CreateValidStdioDescriptor(),
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        Assert.DoesNotContain("credential-token", exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, factory.Transport.DisposeCount);
        Assert.Equal(0, factory.Client.DisposeCount);
    }

    [Fact]
    public async Task MaterializeAsync_WhenToolListingFails_DisposesClientThenTransportAndSanitizesException()
    {
        var order = new List<string>();
        var factory = new RecordingMcpMaterializerFactory(order);
        factory.Client.ListToolsException = new InvalidOperationException("credential-token");
        var materializer = new McpToolMaterializer(factory);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            materializer.MaterializeAsync(
                CreateValidStdioDescriptor(),
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        Assert.DoesNotContain("credential-token", exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(["client", "transport"], order);
        Assert.Equal(1, factory.Client.DisposeCount);
        Assert.Equal(1, factory.Transport.DisposeCount);
    }

    [Fact]
    public async Task MaterializeAsync_UnsupportedDescriptor_ThrowsAgwException()
    {
        var materializer = new McpToolMaterializer(new RecordingMcpMaterializerFactory());

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            materializer.MaterializeAsync(
                new UnsupportedEndpointDescriptor("unsupported"),
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.UnsupportedTransportType.Code, exception.Code);
    }

    [Fact]
    public async Task MaterializeAsync_StdioWithoutCommand_ThrowsAgwException()
    {
        var materializer = new McpToolMaterializer(new RecordingMcpMaterializerFactory());
        var descriptor = new McpStdioEndpointDescriptor("stdio-server", command: " ");

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            materializer.MaterializeAsync(descriptor, cancellationToken: TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.McpStdioCommandRequired.Code, exception.Code);
    }

    [Fact]
    public async Task MaterializeAsync_HttpWithoutEndpoint_ThrowsAgwException()
    {
        var materializer = new McpToolMaterializer(new RecordingMcpMaterializerFactory());
        var descriptor = new McpHttpEndpointDescriptor("http-server", endpoint: null);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            materializer.MaterializeAsync(descriptor, cancellationToken: TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.McpHttpUrlRequired.Code, exception.Code);
    }

    private static McpStdioEndpointDescriptor CreateValidStdioDescriptor()
    {
        return new McpStdioEndpointDescriptor("stdio-server", "server-command");
    }

    private sealed class UnsupportedEndpointDescriptor : McpEndpointDescriptor
    {
        public UnsupportedEndpointDescriptor(string name)
            : base(name) { }
    }

    private sealed class RecordingMcpMaterializerFactory : IMcpMaterializerFactory
    {
        private readonly IList<string> _disposeOrder;

        public RecordingMcpMaterializerFactory()
            : this(new List<string>()) { }

        public RecordingMcpMaterializerFactory(IList<string> disposeOrder)
        {
            _disposeOrder = disposeOrder;
            Transport = new TrackingInitialTransport(disposeOrder);
            Client = new TrackingMcpClient(disposeOrder);
        }

        public ResolvedMcpEndpoint? LastEndpoint { get; private set; }

        public TrackingInitialTransport Transport { get; }

        public TrackingMcpClient Client { get; }

        public Exception? ClientCreationException { get; init; }

        public IMcpInitialTransport CreateTransport(ResolvedMcpEndpoint endpoint)
        {
            LastEndpoint = endpoint;
            return Transport;
        }

        public Task<IMcpMaterializerClient> CreateClientAsync(
            IMcpInitialTransport transport,
            CancellationToken cancellationToken
        )
        {
            if (ClientCreationException != null)
            {
                return Task.FromException<IMcpMaterializerClient>(ClientCreationException);
            }

            return Task.FromResult<IMcpMaterializerClient>(Client);
        }
    }

    private sealed class TrackingInitialTransport : IMcpInitialTransport
    {
        private readonly IList<string> _disposeOrder;

        public TrackingInitialTransport(IList<string> disposeOrder)
        {
            _disposeOrder = disposeOrder;
        }

        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _disposeOrder.Add("transport");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingMcpClient : IMcpMaterializerClient
    {
        private readonly IList<string> _disposeOrder;

        public TrackingMcpClient(IList<string> disposeOrder)
        {
            _disposeOrder = disposeOrder;
        }

        public Exception? ListToolsException { get; set; }

        public int DisposeCount { get; private set; }

        public Task<IReadOnlyList<AITool>> ListToolsAsync(CancellationToken cancellationToken)
        {
            if (ListToolsException != null)
            {
                return Task.FromException<IReadOnlyList<AITool>>(ListToolsException);
            }

            AITool tool = AIFunctionFactory.Create(
                (Func<string>)(() => "ok"),
                new AIFunctionFactoryOptions { Name = "test_tool" }
            );
            return Task.FromResult<IReadOnlyList<AITool>>([tool]);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _disposeOrder.Add("client");
            return ValueTask.CompletedTask;
        }
    }
}
