using System.Text.Json;
using Agw.Agents.Execution.Agents.ExternalAgents.Acp.Protocol;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;

namespace Agw.Agents.Execution.Agents.ExternalAgents.Acp;

internal sealed class AcpAgentSession : AgentSession
{
    public const int MaximumSessionIdLength = 200;

    public static bool IsValidId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= MaximumSessionIdLength;

    public string? SessionId { get; set; }
    public AcpSessionModeState? Modes { get; set; }
    public IReadOnlyList<AcpSessionConfigOption>? ConfigOptions { get; set; }
    public AcpConnection? Connection { get; set; }
    public string? Source { get; set; }
    public bool SettingsApplied { get; set; }

    public JsonElement Serialize(JsonSerializerOptions? options) =>
        JsonSerializer.SerializeToElement(new Snapshot(SessionId, Modes, ConfigOptions), options ?? AcpJson.Options);

    public static AcpAgentSession Deserialize(JsonElement json, JsonSerializerOptions? options)
    {
        var snapshot =
            json.Deserialize<Snapshot>(options ?? AcpJson.Options)
            ?? throw new AgwException(ErrorCodes.InvalidParam, "Invalid ACP session state.");
        return new()
        {
            SessionId = snapshot.SessionId,
            Modes = snapshot.Modes,
            ConfigOptions = snapshot.ConfigOptions,
        };
    }

    private sealed record Snapshot(
        string? SessionId,
        AcpSessionModeState? Modes,
        IReadOnlyList<AcpSessionConfigOption>? ConfigOptions
    );
}
