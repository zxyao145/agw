using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agw.Appliaction.Services.Agents;

public static class AgentRuntimeConfigurationMerger
{
    public static string? MergeExtraSettings(
        string? agentExtra,
        string? projectExtraSetting,
        string? requestExtraSetting,
        Action<string>? onInvalidJson = null)
    {
        JsonObject? merged = null;

        MergeExtraSetting(ref merged, agentExtra, "Agent.Extra", onInvalidJson);
        MergeExtraSetting(ref merged, projectExtraSetting, "Project.ExtraSetting", onInvalidJson);
        MergeExtraSetting(ref merged, requestExtraSetting, "SettingCommand.SettingContent", onInvalidJson);

        return merged?.ToJsonString();
    }

    private static void MergeExtraSetting(
        ref JsonObject? merged,
        string? rawSetting,
        string settingName,
        Action<string>? onInvalidJson)
    {
        if (string.IsNullOrWhiteSpace(rawSetting))
        {
            return;
        }

        if (!TryParseJsonObject(rawSetting, out var jsonObject))
        {
            onInvalidJson?.Invoke(settingName);
            return;
        }

        merged ??= new JsonObject();
        foreach (var pair in jsonObject)
        {
            merged[pair.Key] = pair.Value?.DeepClone();
        }
    }

    private static bool TryParseJsonObject(string json, [NotNullWhen(true)] out JsonObject? jsonObject)
    {
        jsonObject = null;

        try
        {
            jsonObject = JsonNode.Parse(json) as JsonObject;
            return jsonObject != null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
