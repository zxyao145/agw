namespace PiAgentSdk;

/// <summary>Describes a provider-declared terminal error observed during a completed run.</summary>
public sealed class PiTerminalError
{
    /// <summary>Initializes terminal error information.</summary>
    /// <param name="message">The provider error text.</param>
    /// <param name="source">The protocol boundary that reported the error.</param>
    public PiTerminalError(string message, string source)
    {
        Message = message;
        Source = source;
    }

    /// <summary>Gets the provider error text.</summary>
    public string Message { get; }

    /// <summary>Gets the reporting boundary, such as <c>assistant</c>, <c>retry</c>, or <c>compaction</c>.</summary>
    public string Source { get; }
}

/// <summary>Represents the collected result of a settled, non-streaming Pi run.</summary>
public sealed class PiTurn
{
    /// <summary>Gets the authoritative messages emitted at completed turn boundaries.</summary>
    public IReadOnlyList<PiMessage> Messages { get; init; } = [];

    /// <summary>Gets the concatenated text from the final authoritative Assistant message.</summary>
    public string FinalResponse { get; init; } = string.Empty;

    /// <summary>Gets usage accumulated across completed Assistant, Tool, and compaction work.</summary>
    public PiUsage? Usage { get; init; }

    /// <summary>Gets a provider-declared terminal error, or <see langword="null"/> on success.</summary>
    public PiTerminalError? TerminalError { get; init; }
}
