namespace Agw.Integrations.Mcp;

public interface IMcpToolMaterializer
{
    Task<ConnectionToolLease> MaterializeAsync(
        McpEndpointDescriptor descriptor,
        McpRuntimeOverrides? runtimeOverrides = null,
        CancellationToken cancellationToken = default);
}
