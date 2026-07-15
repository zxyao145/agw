namespace Agw.Integrations.Contracts.Management;

public sealed class PluginInstallationUpsertRequest
{
    public string PluginId { get; set; } = string.Empty;
    public string ConnectorId { get; set; } = string.Empty;
    public string AuthSchemeId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public Dictionary<string, string?> Configuration { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, SecretFieldUpdateRequest> Secrets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PluginInstallationResponse
{
    public Guid Id { get; init; }
    public string PluginId { get; init; } = string.Empty;
    public string ConnectorId { get; init; } = string.Empty;
    public string AuthSchemeId { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public IReadOnlyDictionary<string, string?> Configuration { get; init; } =
        new Dictionary<string, string?>();
    public IReadOnlyDictionary<string, SecretFieldStateResponse> Secrets { get; init; } =
        new Dictionary<string, SecretFieldStateResponse>();
}

public sealed class ConnectionCreateRequest
{
    public string PluginId { get; set; } = string.Empty;
    public string ConnectorId { get; set; } = string.Empty;
    public string AuthSchemeId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public Dictionary<string, string?> Configuration { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, SecretFieldUpdateRequest> Secrets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ConnectionUpdateRequest
{
    public Guid Id { get; set; }
    public string PluginId { get; set; } = string.Empty;
    public string ConnectorId { get; set; } = string.Empty;
    public string AuthSchemeId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public Dictionary<string, string?> Configuration { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, SecretFieldUpdateRequest> Secrets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ConnectionValidateRequest
{
    public Guid Id { get; set; }
}

public sealed class ConnectionResponse
{
    public Guid Id { get; init; }
    public string PluginId { get; init; } = string.Empty;
    public string ConnectorId { get; init; } = string.Empty;
    public string AuthSchemeId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Alias { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public ConnectionStatusResponse Status { get; init; }
    public string? Subject { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    public DateTimeOffset? LastValidatedAtUtc { get; init; }
    public string? LastValidationErrorCode { get; init; }
    public IReadOnlyDictionary<string, string?> Configuration { get; init; } =
        new Dictionary<string, string?>();
    public IReadOnlyDictionary<string, SecretFieldStateResponse> Secrets { get; init; } =
        new Dictionary<string, SecretFieldStateResponse>();
}

public sealed class SecretFieldUpdateRequest
{
    public SecretUpdateAction Action { get; set; }
    public string? SecretValue { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecretUpdateAction
{
    Keep,
    Set,
    Clear
}

public sealed class SecretFieldStateResponse
{
    public bool Configured { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConnectionStatusResponse
{
    NeedsConfiguration,
    PendingAuthorization,
    Unverified,
    Ready,
    Expired,
    Invalid,
    Disabled,
    DefinitionUnavailable
}
