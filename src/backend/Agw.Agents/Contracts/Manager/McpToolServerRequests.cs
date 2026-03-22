namespace Agw.Manager.Api.Contracts;

public record McpToolServerCreateRequest(
    string Name,
    List<Guid>? AgentIds,
    string? Description,
    string TransportType,
    string? Command,
    List<string>? Arguments,
    string? WorkingDirectory,
    Dictionary<string, string>? EnvironmentVariables,
    string? Url,
    Dictionary<string, string>? Headers,
    bool Enabled = true);

public record McpToolServerUpdateRequest(
    string Name,
    string? Description,
    string TransportType,
    string? Command,
    List<string>? Arguments,
    string? WorkingDirectory,
    Dictionary<string, string>? EnvironmentVariables,
    string? Url,
    Dictionary<string, string>? Headers,
    bool Enabled = true);

public record McpToolServerConnectRequest(Guid McpToolServerId);

public record McpToolServerConnectResponse(string Status, List<McpToolItem> Tools);
public record McpToolItem(string Name);
