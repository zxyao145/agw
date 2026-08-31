using System.Diagnostics;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;

namespace PiAgentSdk.MAF;

/// <summary>Stores the serializable Pi provider-session binding used by Microsoft Agent Framework.</summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class PiAgentSession : AgentSession
{
    internal PiAgentSession(string? sessionId = null)
    {
        SessionId = sessionId;
    }

    [JsonConstructor]
    internal PiAgentSession(string? sessionId, AgentSessionStateBag? stateBag)
        : base(stateBag ?? new())
    {
        SessionId = sessionId;
    }

    /// <summary>Gets the provider-issued Pi session identifier after binding.</summary>
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; internal set; }

    [JsonIgnore]
    internal PiSession? BoundSession { get; set; }

    [JsonIgnore]
    internal SemaphoreSlim BindLock { get; } = new(1, 1);

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private string DebuggerDisplay =>
        string.IsNullOrWhiteSpace(SessionId)
            ? $"StateBag Count = {StateBag.Count}"
            : $"SessionId = {SessionId}, StateBag Count = {StateBag.Count}";
}
