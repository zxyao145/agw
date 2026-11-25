using System.Text.Json;
using System.Text.Json.Serialization;
using Agw.Shared.Exceptions;

namespace Agw.Agents.ExternalAgents.Acp;

public static class AcpAgentSettingsJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static AcpAgentSettings Parse(string extra)
    {
        SettingsDto dto;
        try
        {
            dto =
                JsonSerializer.Deserialize<SettingsDto>(extra, Options)
                ?? throw new AgwException(ErrorCodes.InvalidParam, "ACP Extra Settings must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Invalid ACP Extra Settings at {exception.Path}: {exception.Message}"
            );
        }

        return new(
            dto.Registry,
            dto.Command,
            dto.Args,
            dto.AuthMethodId,
            dto.ModeId,
            dto.ConfigOptions?.ToDictionary(
                entry => entry.Key,
                entry => ToOptionValue(entry.Value),
                StringComparer.Ordinal
            )
        );
    }

    private static AcpConfigOptionValue ToOptionValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => new(value.GetString(), null),
            JsonValueKind.True or JsonValueKind.False => new(null, value.GetBoolean()),
            _ => new(null, null),
        };

    private sealed record SettingsDto(
        AcpRegistryReference? Registry,
        string? Command,
        IReadOnlyList<string>? Args,
        string? AuthMethodId,
        string? ModeId,
        Dictionary<string, JsonElement>? ConfigOptions
    );
}
