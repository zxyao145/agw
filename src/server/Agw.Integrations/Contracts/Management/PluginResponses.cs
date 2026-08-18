namespace Agw.Integrations.Contracts.Management;

public sealed class PluginResponse
{
    public string Id { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<ConnectorResponse> Connectors { get; init; } = [];
    public IReadOnlyList<PluginSkillResponse> Skills { get; init; } = [];
}

public sealed class ConnectorResponse
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public IReadOnlyList<AuthSchemeResponse> AuthSchemes { get; init; } = [];
    public IReadOnlyList<CapabilitySourceResponse> CapabilitySources { get; init; } = [];
}

public sealed class AuthSchemeResponse
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public AuthSchemeTypeResponse Type { get; init; }
    public OAuth2AuthorizationCodeResponse? OAuth2AuthorizationCode { get; init; }
    public IReadOnlyList<FormFieldResponse> InstallationFields { get; init; } = [];
    public IReadOnlyList<FormFieldResponse> ConnectionFields { get; init; } = [];
    public PluginInstallationScopeResponse? Installation { get; init; }
}

public sealed class PluginInstallationScopeResponse
{
    public Guid Id { get; init; }
    public bool Enabled { get; init; }
    public IReadOnlyDictionary<string, string?> Configuration { get; init; } = new Dictionary<string, string?>();
    public IReadOnlyDictionary<string, SecretFieldStateResponse> Secrets { get; init; } =
        new Dictionary<string, SecretFieldStateResponse>();
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuthSchemeTypeResponse
{
    OAuth2,
    ApiKey,
    AkSk,
}

public sealed class FormFieldResponse
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public FormFieldTypeResponse Type { get; init; }
    public bool IsRequired { get; init; }
    public string? Description { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FormFieldTypeResponse
{
    Text,
    Secret,
    Url,
}

public sealed class OAuth2AuthorizationCodeResponse
{
    public string AuthorizationEndpoint { get; init; } = string.Empty;
    public string TokenEndpoint { get; init; } = string.Empty;
    public string? UserInfoEndpoint { get; init; }
    public string ClientIdFieldId { get; init; } = string.Empty;
    public string? ClientSecretFieldId { get; init; }
    public OAuthSubjectResolutionResponse SubjectResolution { get; init; } = new();
    public bool UsePkce { get; init; }
    public OAuth2ClientAuthenticationMethodResponse ClientAuthenticationMethod { get; init; }
    public bool SupportsRefresh { get; init; }
    public IReadOnlyList<string> Scopes { get; init; } = [];
    public IReadOnlyDictionary<string, string> AdditionalAuthorizeParameters { get; init; } =
        new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> AdditionalTokenParameters { get; init; } =
        new Dictionary<string, string>();
}

public sealed class OAuthSubjectResolutionResponse
{
    public OAuthSubjectSourceResponse Source { get; init; }
    public string Field { get; init; } = string.Empty;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OAuthSubjectSourceResponse
{
    UserInfo,
    TokenResponse,
    IdToken,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OAuth2ClientAuthenticationMethodResponse
{
    Body,
    Basic,
    None,
}

public sealed class CapabilitySourceResponse
{
    public string Id { get; init; } = string.Empty;
    public CapabilitySourceKindResponse Kind { get; init; }
    public string? Provider { get; init; }
    public McpTransportResponse? McpTransport { get; init; }
    public IReadOnlyList<CredentialBindingResponse> CredentialBindings { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CapabilitySourceKindResponse
{
    Native,
    Mcp,
}

public sealed class McpTransportResponse
{
    public McpTransportKindResponse Kind { get; init; }
    public string? Command { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? Endpoint { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum McpTransportKindResponse
{
    Stdio,
    Http,
    Sse,
}

public sealed class CredentialBindingResponse
{
    public CredentialValueSourceKindResponse SourceKind { get; init; }
    public string AuthSchemeId { get; init; } = string.Empty;
    public string? FieldId { get; init; }
    public CredentialBindingTargetResponse Target { get; init; }
    public string TargetName { get; init; } = string.Empty;
    public string? ValuePrefix { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CredentialValueSourceKindResponse
{
    ConnectionField,
    InstallationField,
    OAuthAccessToken,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CredentialBindingTargetResponse
{
    EnvironmentVariable,
    HttpHeader,
}

public sealed class PluginSkillResponse
{
    public string Id { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string ContentPath { get; init; } = string.Empty;
}
