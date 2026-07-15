namespace Agw.Integrations.Application.Management;

public static class IntegrationCredentialSlots
{
    public const string OAuthAccessToken = "oauth.access-token";
    public const string OAuthRefreshToken = "oauth.refresh-token";
    public const string OAuthIdToken = "oauth.id-token";

    public static string InstallationField(string connectorId, string authSchemeId, string fieldId) =>
        $"field:{connectorId}:{authSchemeId}:{fieldId}";

    public static string ConnectionField(string fieldId) => $"field:{fieldId}";
}
