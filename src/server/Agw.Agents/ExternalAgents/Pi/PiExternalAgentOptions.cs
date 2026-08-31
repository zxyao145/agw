namespace Agw.Agents.ExternalAgents.Pi;

/// <summary>Configures server-owned Pi integration policy.</summary>
public sealed class PiExternalAgentOptions
{
    /// <summary>Gets the configuration section used by the Pi integration.</summary>
    public const string SectionName = "ExternalAgents:Pi";

    /// <summary>
    /// Gets trusted extension files loaded explicitly while automatic extension discovery remains disabled.
    /// </summary>
    public string[] Extensions { get; init; } = [];

    /// <summary>Gets the independent timeout for Pi history and provider-session persistence.</summary>
    public TimeSpan HistoryPersistenceTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
