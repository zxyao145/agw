using System.Text.Json;

using Agw.Shared.Exceptions;

namespace Agw.Integrations.Application.Management;

internal static class IntegrationConfigurationCodec
{
    public static Dictionary<string, string?> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string?>>(json);
            return values == null
                ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            throw new AgwException(ErrorCodes.IntegrationDataInvalid);
        }
    }

    public static string Write(IReadOnlyDictionary<string, string?> values) => JsonSerializer.Serialize(values);

    public static string InstallationKey(string connectorId, string authSchemeId, string fieldId) =>
        $"{connectorId}:{authSchemeId}:{fieldId}";

    public static Dictionary<string, string?> ReadInstallationScope(
        IReadOnlyDictionary<string, string?> allValues,
        string connectorId,
        string authSchemeId,
        IReadOnlyCollection<string> fieldIds)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var fieldId in fieldIds)
        {
            if (allValues.TryGetValue(InstallationKey(connectorId, authSchemeId, fieldId), out var value))
            {
                result[fieldId] = value;
            }
        }

        return result;
    }

    public static void ReplaceInstallationScope(
        IDictionary<string, string?> allValues,
        string connectorId,
        string authSchemeId,
        IReadOnlyCollection<string> fieldIds,
        IReadOnlyDictionary<string, string?> replacement)
    {
        foreach (var fieldId in fieldIds)
        {
            allValues.Remove(InstallationKey(connectorId, authSchemeId, fieldId));
        }

        foreach (var item in replacement)
        {
            allValues[InstallationKey(connectorId, authSchemeId, item.Key)] = item.Value;
        }
    }
}
