namespace PiAgentSdk;

/// <summary>Configures Pi executable discovery, RPC timeouts, and process environment overrides.</summary>
public sealed record PiAgentOptions
{
    /// <summary>Gets an optional absolute or PATH-resolvable Pi executable override.</summary>
    public string? PiPathOverride { get; init; }

    /// <summary>Gets the timeout applied independently to each RPC command. The default is 30 seconds.</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the time allowed for abort-and-drain cleanup before the process tree is killed.</summary>
    public TimeSpan AbortGracePeriod { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets explicit environment variables applied after the SDK's sanitized host environment.
    /// </summary>
    /// <remarks>Host API keys and other arbitrary variables are not inherited unless supplied explicitly.</remarks>
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }

    internal void Validate()
    {
        if (CommandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeout), "Command timeout must be positive.");
        }

        if (AbortGracePeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(AbortGracePeriod), "Abort grace period must be positive.");
        }
    }
}
