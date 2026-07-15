namespace Agw.Integrations.Domain.Plugins;

public sealed class AuthSchemeDefinition
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required AuthSchemeType Type { get; init; }

    public OAuth2AuthorizationCodeSettings? OAuth2AuthorizationCode { get; init; }

    public IReadOnlyList<FormFieldDefinition> InstallationFields { get; init; } = [];

    public IReadOnlyList<FormFieldDefinition> ConnectionFields { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuthSchemeType
{
    OAuth2,
    ApiKey,
    AkSk,
}

public sealed class OAuth2AuthorizationCodeSettings
{
    public required string AuthorizationEndpoint { get; init; }

    public required string TokenEndpoint { get; init; }

    public string? UserInfoEndpoint { get; init; }

    public required string ClientIdFieldId { get; init; }

    public string? ClientSecretFieldId { get; init; }

    public required OAuthSubjectResolutionDefinition SubjectResolution { get; init; }

    public bool UsePkce { get; init; }

    public required OAuth2ClientAuthenticationMethod ClientAuthenticationMethod { get; init; }

    public bool SupportsRefresh { get; init; }

    public IReadOnlyList<string> Scopes { get; init; } = [];

    public IReadOnlyDictionary<string, string> AdditionalAuthorizeParameters { get; init; } =
        new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> AdditionalTokenParameters { get; init; } =
        new Dictionary<string, string>();
}

public enum OAuth2ClientAuthenticationMethod
{
    Body,
    Basic,
    None,
}

public sealed class OAuthSubjectResolutionDefinition
{
    public required OAuthSubjectSource Source { get; init; }

    public required string Field { get; init; }
}

public enum OAuthSubjectSource
{
    UserInfo,
    TokenResponse,
    IdToken,
}
