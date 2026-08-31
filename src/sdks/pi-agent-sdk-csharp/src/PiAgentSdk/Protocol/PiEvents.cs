using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiAgentSdk;

/// <summary>Defines the polymorphic base for events emitted by Pi RPC mode.</summary>
[JsonConverter(typeof(PiEventJsonConverter))]
public abstract class PiEvent
{
    /// <summary>Gets the Pi protocol event discriminator.</summary>
    public abstract string Type { get; }
}

/// <summary>Represents a lifecycle event that carries no additional payload.</summary>
public sealed class PiMarkerEvent : PiEvent
{
    /// <summary>Initializes a marker event.</summary>
    /// <param name="type">The lifecycle event discriminator.</param>
    public PiMarkerEvent(string type)
    {
        Type = type;
    }

    /// <inheritdoc />
    public override string Type { get; }
}

/// <summary>Represents completion of one low-level Pi Agent run.</summary>
/// <remarks>This is not the session-level completion boundary; wait for <c>agent_settled</c>.</remarks>
public sealed class PiAgentEndEvent : PiEvent
{
    /// <inheritdoc />
    public override string Type => "agent_end";

    /// <summary>Gets messages emitted by the completed low-level run.</summary>
    public IReadOnlyList<PiMessage> Messages { get; init; } = [];

    /// <summary>Gets a value indicating whether Pi will automatically retry.</summary>
    public bool WillRetry { get; init; }
}

/// <summary>Represents an authoritative completed turn and its Tool Results.</summary>
public sealed class PiTurnEndEvent : PiEvent
{
    /// <inheritdoc />
    public override string Type => "turn_end";

    /// <summary>Gets the authoritative message for the completed turn.</summary>
    public PiMessage? Message { get; init; }

    /// <summary>Gets authoritative Tool Result messages in Assistant source order.</summary>
    public IReadOnlyList<PiMessage> ToolResults { get; init; } = [];
}

/// <summary>Represents a Pi <c>message_start</c> or <c>message_end</c> boundary.</summary>
public sealed class PiMessageEvent : PiEvent
{
    /// <summary>Initializes a message-boundary event.</summary>
    /// <param name="type">The message-boundary discriminator.</param>
    public PiMessageEvent(string type)
    {
        Type = type;
    }

    /// <inheritdoc />
    public override string Type { get; }

    /// <summary>Gets the message snapshot carried by the boundary.</summary>
    public PiMessage? Message { get; init; }
}

/// <summary>Defines the polymorphic base for delta events within a streaming Assistant message.</summary>
[JsonConverter(typeof(PiAssistantDeltaJsonConverter))]
public abstract class PiAssistantDelta
{
    /// <summary>Gets the Assistant-delta discriminator.</summary>
    public abstract string Type { get; }

    /// <summary>Gets the zero-based content-block index targeted by the delta.</summary>
    public int ContentIndex { get; init; }
}

/// <summary>Represents an incremental Assistant text fragment.</summary>
public sealed class PiTextDelta : PiAssistantDelta
{
    /// <inheritdoc />
    public override string Type => "text_delta";

    /// <summary>Gets the text fragment to append.</summary>
    public string Delta { get; init; } = string.Empty;
}

/// <summary>Represents an incremental Assistant reasoning fragment.</summary>
public sealed class PiThinkingDelta : PiAssistantDelta
{
    /// <inheritdoc />
    public override string Type => "thinking_delta";

    /// <summary>Gets the reasoning fragment to append.</summary>
    public string Delta { get; init; } = string.Empty;
}

/// <summary>Represents the beginning of a streaming Tool Call content block.</summary>
public sealed class PiToolCallStartDelta : PiAssistantDelta
{
    /// <inheritdoc />
    public override string Type => "toolcall_start";

    /// <summary>Gets the provider-issued Tool Call identifier.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the Pi Tool name.</summary>
    public string ToolName { get; init; } = string.Empty;
}

/// <summary>Represents an incremental JSON argument fragment for a streaming Tool Call.</summary>
public sealed class PiToolCallArgumentsDelta : PiAssistantDelta
{
    /// <inheritdoc />
    public override string Type => "toolcall_delta";

    /// <summary>Gets the argument fragment to append.</summary>
    public string Delta { get; init; } = string.Empty;
}

/// <summary>Represents completion of a Tool Call content block with authoritative arguments.</summary>
public sealed class PiToolCallEndDelta : PiAssistantDelta
{
    /// <inheritdoc />
    public override string Type => "toolcall_end";

    /// <summary>Gets the completed Tool Call.</summary>
    public PiToolCallContent ToolCall { get; init; } = new();
}

/// <summary>Represents the start or end boundary of a text or reasoning content block.</summary>
public sealed class PiContentBoundaryDelta : PiAssistantDelta
{
    /// <summary>Initializes a content-boundary delta.</summary>
    /// <param name="type">The boundary discriminator, such as <c>text_start</c> or <c>thinking_end</c>.</param>
    public PiContentBoundaryDelta(string type)
    {
        Type = type;
    }

    /// <inheritdoc />
    public override string Type { get; }

    /// <summary>Gets finalized block content when Pi includes it on an end boundary.</summary>
    public string? Content { get; init; }
}

/// <summary>Preserves an unrecognized Assistant delta for forward compatibility.</summary>
public sealed class PiUnknownAssistantDelta : PiAssistantDelta
{
    /// <summary>Initializes an unknown Assistant delta.</summary>
    /// <param name="type">The unrecognized delta discriminator.</param>
    /// <param name="raw">The complete raw JSON object.</param>
    public PiUnknownAssistantDelta(string type, JsonElement raw)
    {
        Type = type;
        Raw = raw;
    }

    /// <inheritdoc />
    public override string Type { get; }

    /// <summary>Gets the complete raw JSON object.</summary>
    public JsonElement Raw { get; }
}

/// <summary>Represents one delta in a streaming Assistant message.</summary>
public sealed class PiMessageUpdateEvent : PiEvent
{
    /// <inheritdoc />
    public override string Type => "message_update";

    /// <summary>Gets the latest cumulative provider usage snapshot for the in-flight message.</summary>
    public PiUsage Usage { get; init; } = new();

    /// <summary>Gets the Assistant message delta.</summary>
    public PiAssistantDelta AssistantMessageEvent { get; init; } = new PiUnknownAssistantDelta("unknown", default);
}

/// <summary>Represents content and Tool-specific details from Tool execution.</summary>
public sealed class PiToolExecutionResult
{
    /// <summary>Gets the Tool output content blocks.</summary>
    public IReadOnlyList<PiContent> Content { get; init; } = [];

    /// <summary>Gets Tool-specific execution details.</summary>
    public JsonElement? Details { get; init; }
}

/// <summary>Represents a Tool execution start, progress update, or completion event.</summary>
public sealed class PiToolExecutionEvent : PiEvent
{
    /// <summary>Initializes a Tool execution event.</summary>
    /// <param name="type">The Tool execution event discriminator.</param>
    public PiToolExecutionEvent(string type)
    {
        Type = type;
    }

    /// <inheritdoc />
    public override string Type { get; }

    /// <summary>Gets the correlated Tool Call identifier.</summary>
    public string ToolCallId { get; init; } = string.Empty;

    /// <summary>Gets the Pi Tool name.</summary>
    public string ToolName { get; init; } = string.Empty;

    /// <summary>Gets the original Tool arguments.</summary>
    public JsonElement Args { get; init; }

    /// <summary>Gets cumulative partial output for a progress event.</summary>
    public PiToolExecutionResult? PartialResult { get; init; }

    /// <summary>Gets the final output for a completion event.</summary>
    public PiToolExecutionResult? Result { get; init; }

    /// <summary>Gets a value indicating whether final Tool execution failed.</summary>
    public bool IsError { get; init; }
}

/// <summary>Represents the outcome of a Pi context-compaction operation.</summary>
public sealed class PiCompactionResult
{
    /// <summary>Gets the generated compaction summary.</summary>
    public string? Summary { get; init; }

    /// <summary>Gets the identifier of the first session entry retained after compaction.</summary>
    public string? FirstKeptEntryId { get; init; }

    /// <summary>Gets the token estimate before compaction.</summary>
    public long TokensBefore { get; init; }

    /// <summary>Gets the estimated token count after compaction.</summary>
    public long EstimatedTokensAfter { get; init; }

    /// <summary>Gets usage incurred while generating the summary.</summary>
    public PiUsage? Usage { get; init; }
}

/// <summary>Represents a context-compaction lifecycle event.</summary>
public sealed class PiCompactionEvent : PiEvent
{
    /// <summary>Initializes a compaction event.</summary>
    /// <param name="type">The compaction event discriminator.</param>
    public PiCompactionEvent(string type)
    {
        Type = type;
    }

    /// <inheritdoc />
    public override string Type { get; }

    /// <summary>Gets the reason Pi initiated compaction.</summary>
    public string? Reason { get; init; }

    /// <summary>Gets the completed compaction result.</summary>
    public PiCompactionResult? Result { get; init; }

    /// <summary>Gets a value indicating whether compaction was aborted.</summary>
    public bool Aborted { get; init; }

    /// <summary>Gets a value indicating whether Pi will retry failed compaction.</summary>
    public bool WillRetry { get; init; }

    /// <summary>Gets the final compaction error text.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>Represents an automatic provider retry lifecycle event.</summary>
public sealed class PiRetryEvent : PiEvent
{
    /// <summary>Initializes an automatic retry event.</summary>
    /// <param name="type">The retry event discriminator.</param>
    public PiRetryEvent(string type)
    {
        Type = type;
    }

    /// <inheritdoc />
    public override string Type { get; }

    /// <summary>Gets the current one-based retry attempt.</summary>
    public int Attempt { get; init; }

    /// <summary>Gets the maximum retry-attempt count.</summary>
    public int MaxAttempts { get; init; }

    /// <summary>Gets the scheduled delay in milliseconds.</summary>
    public int DelayMs { get; init; }

    /// <summary>Gets a value indicating whether the retry sequence succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Gets the error associated with the current attempt.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Gets the terminal error after retry attempts are exhausted.</summary>
    public string? FinalError { get; init; }
}

/// <summary>Represents an exception raised by a loaded Pi extension.</summary>
public sealed class PiExtensionErrorEvent : PiEvent
{
    /// <inheritdoc />
    public override string Type => "extension_error";

    /// <summary>Gets the extension path reported by Pi.</summary>
    public string? ExtensionPath { get; init; }

    /// <summary>Gets the extension event being handled when the error occurred.</summary>
    public string? Event { get; init; }

    /// <summary>Gets the extension error text.</summary>
    public string? Error { get; init; }
}

/// <summary>Represents the current Pi steering and follow-up queues.</summary>
public sealed class PiQueueUpdateEvent : PiEvent
{
    /// <inheritdoc />
    public override string Type => "queue_update";

    /// <summary>Gets pending steering messages.</summary>
    public IReadOnlyList<string> Steering { get; init; } = [];

    /// <summary>Gets pending follow-up messages.</summary>
    public IReadOnlyList<string> FollowUp { get; init; } = [];
}

/// <summary>Represents a typed user-interface request from a Pi extension.</summary>
public sealed class PiExtensionUiRequestEvent : PiEvent
{
    /// <inheritdoc />
    public override string Type => "extension_ui_request";

    /// <summary>Gets the Extension UI request.</summary>
    public PiExtensionUiRequest Request { get; init; } = new();
}

/// <summary>Preserves an unrecognized Pi event for forward compatibility.</summary>
public sealed class PiUnknownEvent : PiEvent
{
    /// <summary>Initializes an unknown Pi event.</summary>
    /// <param name="type">The unrecognized event discriminator.</param>
    /// <param name="raw">The complete raw JSON object.</param>
    public PiUnknownEvent(string type, JsonElement raw)
    {
        Type = type;
        Raw = raw;
    }

    /// <inheritdoc />
    public override string Type { get; }

    /// <summary>Gets the complete raw JSON object.</summary>
    public JsonElement Raw { get; }
}

/// <summary>Represents the state returned by the Pi RPC <c>get_state</c> command.</summary>
public sealed class PiSessionState
{
    /// <summary>Gets the selected model descriptor.</summary>
    public JsonElement? Model { get; init; }

    /// <summary>Gets the active thinking level.</summary>
    public string? ThinkingLevel { get; init; }

    /// <summary>Gets a value indicating whether Pi is currently streaming a response.</summary>
    public bool IsStreaming { get; init; }

    /// <summary>Gets a value indicating whether Pi is currently compacting context.</summary>
    public bool IsCompacting { get; init; }

    /// <summary>Gets the persistent session-file path, when enabled.</summary>
    public string? SessionFile { get; init; }

    /// <summary>Gets the provider-issued session identifier.</summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>Gets the optional session display name.</summary>
    public string? SessionName { get; init; }

    /// <summary>Gets a value indicating whether automatic compaction is enabled.</summary>
    public bool AutoCompactionEnabled { get; init; }

    /// <summary>Gets the number of messages in session state.</summary>
    public int MessageCount { get; init; }

    /// <summary>Gets the number of queued messages awaiting processing.</summary>
    public int PendingMessageCount { get; init; }
}
