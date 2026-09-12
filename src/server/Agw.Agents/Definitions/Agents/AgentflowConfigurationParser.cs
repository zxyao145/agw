using System.Text.Json;
using Agw.Agents.Definitions.Domain.Decisions;
using Agw.Shared.Data.Entities.Agentflows;

namespace Agw.Agents.Definitions.Agents;

public static class AgentflowConfigurationParser
{
    public static AgentflowConfigurationFacts Parse(
        IReadOnlyList<AgentflowNode>? nodes,
        IReadOnlyList<AgentflowEdge>? edges
    )
    {
        var nodeConfigs = (nodes ?? []).Select(n => n.ConfigJson ?? "").Distinct().ToArray();
        var edgeConfigs = (edges ?? []).Select(e => e.ConfigJson ?? "").Distinct().ToArray();
        return new AgentflowConfigurationFacts
        {
            OutputSummary = nodeConfigs.ToDictionary(
                json => json,
                json => TryReadOutputSummaryEnabled(json, out var enabled) ? (bool?)enabled : null
            ),
            SwitchOrder = edgeConfigs.ToDictionary(
                json => json,
                json => TryReadSwitchCaseOrder(json, out var order) ? (int?)order : null
            ),
            Participants = nodeConfigs.ToDictionary(
                json => json,
                json => ReadBlockParticipantNodeIds(new AgentflowNode { ConfigJson = json })
            ),
            Conditions = (edges ?? [])
                .Select(e => e.ConditionJson ?? "")
                .Distinct()
                .ToDictionary(json => json, IsValidConditionJson),
        };
    }

    public static bool TryReadSwitchCaseOrder(string? configJson, out int order)
    {
        order = 0;
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(configJson);
            if (
                document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("switchCaseOrder", out var property)
                || property.ValueKind != JsonValueKind.Number
                || !property.TryGetInt32(out order)
                || order < 0
            )
            {
                order = 0;
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryReadOutputSummaryEnabled(string? configJson, out bool enabled)
    {
        enabled = false;
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(configJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!document.RootElement.TryGetProperty("enableSummary", out var property))
            {
                return true;
            }

            if (property.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            {
                return false;
            }

            enabled = property.GetBoolean();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> ReadBlockParticipantNodeIds(AgentflowNode node)
    {
        if (string.IsNullOrWhiteSpace(node.ConfigJson))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(node.ConfigJson);
            if (
                doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("participantNodeIds", out var participants)
                || participants.ValueKind != JsonValueKind.Array
            )
            {
                return [];
            }

            return participants
                .EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => element.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool IsValidConditionJson(string? conditionJson)
    {
        if (string.IsNullOrWhiteSpace(conditionJson))
        {
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(conditionJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var hasKnownCondition = false;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                hasKnownCondition = true;
                if (!IsValidConditionProperty(property))
                {
                    return false;
                }
            }

            return hasKnownCondition;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsValidConditionProperty(JsonProperty property)
    {
        return property.Name switch
        {
            "always" => property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "contains" or "notContains" or "equals" or "author" or "role" => property.Value.ValueKind
                == JsonValueKind.String,
            "minMessages" => property.Value.ValueKind == JsonValueKind.Number,
            _ => false,
        };
    }
}
