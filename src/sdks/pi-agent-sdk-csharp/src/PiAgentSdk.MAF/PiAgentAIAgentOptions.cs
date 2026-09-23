using System.Text.Json.Serialization;
using Microsoft.Agents.AI;

namespace PiAgentSdk.MAF;

/// <summary>Configures the Microsoft Agent Framework adapter for Pi.</summary>
public sealed record PiAgentAIAgentOptions
{
    /// <summary>Gets process-wide Pi SDK options.</summary>
    public PiAgentOptions GlobalOptions { get; init; } = new();

    /// <summary>Gets options for the live Pi session bound to the MAF session.</summary>
    public PiSessionOptions SessionOptions { get; init; } = new();

    /// <summary>Gets the provider-issued Pi session identifier to bind or resume.</summary>
    public string? SessionId { get; init; }

    /// <summary>Gets a value indicating whether <see cref="SessionId"/> must be resumed.</summary>
    public bool IsResume { get; init; }

    /// <summary>
    /// Gets the maximum time allowed for one history write or session-start callback. The default is 30 seconds.
    /// </summary>
    /// <remarks>
    /// Cleanup persistence is deliberately independent of caller cancellation so completed turns can be flushed, but it
    /// remains bounded by this timeout.
    /// </remarks>
    public TimeSpan HistoryPersistenceTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the callback invoked once after a new Pi session reports its identifier.</summary>
    /// <remarks>The callback is runtime-only and is not included in serialized options.</remarks>
    [JsonIgnore]
    public Func<string, CancellationToken, ValueTask>? OnSessionStartedAsync { get; init; }

    /// <summary>Gets the MAF history provider used to persist requests and authoritative completed turns.</summary>
    /// <remarks>History returned before invocation is not resent because Pi maintains provider-side history.</remarks>
    [JsonIgnore]
    public ChatHistoryProvider? ChatHistoryProvider { get; init; }
}
