namespace Agw.Integrations.Domain.Plugins;

public abstract class CapabilitySourceDefinition
{
    /// <summary>
    /// 一个 Connector 有多个Capability。Id 用于区分同一个 Connector 下的多个能力来源。
    /// MCP 工具调用时通过 SourceId 重新找到对应 MCP Source。
    /// </summary>
    public required string Id { get; init; }
}

/// <summary>
/// 由 Agw 内部 C# Provider 创建工具。
/// </summary>
public sealed class NativeCapabilitySourceDefinition : CapabilitySourceDefinition
{
    public required string Provider { get; init; }
}

/// <summary>
/// 连接 MCP Server 后动态加载工具。
/// </summary>
public sealed class McpCapabilitySourceDefinition : CapabilitySourceDefinition
{
    public required McpTransportDefinition Transport { get; init; }

    public IReadOnlyList<CredentialBindingDefinition> CredentialBindings { get; init; } = [];
}

public abstract class McpTransportDefinition { }

public sealed class StdioMcpTransportDefinition : McpTransportDefinition
{
    public required string Command { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];
}

public abstract class EndpointMcpTransportDefinition : McpTransportDefinition
{
    public required string Endpoint { get; init; }
}

public sealed class HttpMcpTransportDefinition : EndpointMcpTransportDefinition { }

public sealed class SseMcpTransportDefinition : EndpointMcpTransportDefinition { }

public sealed class CredentialBindingDefinition
{
    public required CredentialValueSourceDefinition ValueSource { get; init; }

    public required CredentialBindingTarget Target { get; init; }

    public required string TargetName { get; init; }

    public string? ValuePrefix { get; init; }
}

public abstract class CredentialValueSourceDefinition
{
    public required string AuthSchemeId { get; init; }
}

public sealed class ConnectionFieldCredentialValueSourceDefinition : CredentialValueSourceDefinition
{
    public required string FieldId { get; init; }
}

public sealed class InstallationFieldCredentialValueSourceDefinition : CredentialValueSourceDefinition
{
    public required string FieldId { get; init; }
}

public sealed class OAuthAccessTokenCredentialValueSourceDefinition : CredentialValueSourceDefinition { }

public enum CredentialBindingTarget
{
    EnvironmentVariable,
    HttpHeader,
}
