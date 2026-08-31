using Microsoft.Extensions.Logging;
using PiAgentSdk.Internal;

namespace PiAgentSdk;

/// <summary>Creates new or resumed Pi RPC sessions using shared process-level options.</summary>
/// <remarks>Constructing an instance does not locate or start the Pi CLI.</remarks>
public sealed class PiAgent
{
    private readonly PiAgentOptions _options;
    private readonly ILogger? _logger;
    private readonly Func<PiSessionOptions, string?, IPiProcessTransport> _transportFactory;

    /// <summary>Initializes a Pi Agent client.</summary>
    /// <param name="options">Process-wide Pi options, or <see langword="null"/> for defaults.</param>
    /// <param name="logger">An optional logger for sanitized diagnostics.</param>
    public PiAgent(PiAgentOptions? options = null, ILogger? logger = null)
        : this(options ?? new PiAgentOptions(), logger, null) { }

    internal PiAgent(
        PiAgentOptions options,
        ILogger? logger,
        Func<PiSessionOptions, string?, IPiProcessTransport>? transportFactory
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _logger = logger;
        _transportFactory =
            transportFactory
            ?? ((sessionOptions, resumeId) => new PiProcessTransport(_options, sessionOptions, resumeId, _logger));
    }

    /// <summary>Creates a lazy session that starts a new Pi conversation on first use.</summary>
    /// <param name="options">Session options, or <see langword="null"/> for defaults.</param>
    /// <returns>A new, not-yet-started Pi session.</returns>
    public PiSession StartSession(PiSessionOptions? options = null)
    {
        var sessionOptions = options ?? new PiSessionOptions();
        sessionOptions.Validate(isResume: false);
        return CreateSession(sessionOptions, resumeSessionId: null);
    }

    /// <summary>Creates a lazy session that resumes a persistent Pi conversation on first use.</summary>
    /// <param name="sessionId">The provider-issued Pi session identifier.</param>
    /// <param name="options">Session options matching the original session environment.</param>
    /// <returns>A new, not-yet-started Pi session configured for resume.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    public PiSession ResumeSession(string sessionId, PiSessionOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var sessionOptions = options ?? new PiSessionOptions();
        sessionOptions.Validate(isResume: true);
        return CreateSession(sessionOptions, sessionId.Trim());
    }

    private PiSession CreateSession(PiSessionOptions options, string? resumeSessionId)
    {
        var transport = _transportFactory(options, resumeSessionId);
        var connection = new PiRpcConnection(transport, _options, options.ExtensionUiHandler, _logger);
        return new PiSession(connection, options, resumeSessionId, _options.AbortGracePeriod, _logger);
    }
}
