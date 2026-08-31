using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiAgentSdk;

/// <summary>Represents provider-reported monetary cost components for a Pi message.</summary>
public sealed class PiCost
{
    /// <summary>Gets the input-token cost.</summary>
    public double Input { get; init; }

    /// <summary>Gets the output-token cost.</summary>
    public double Output { get; init; }

    /// <summary>Gets the cache-read cost.</summary>
    public double CacheRead { get; init; }

    /// <summary>Gets the cache-write cost.</summary>
    public double CacheWrite { get; init; }

    /// <summary>Gets the total provider-reported cost.</summary>
    public double Total { get; init; }
}

/// <summary>Represents provider-reported token usage and optional cost information.</summary>
public sealed class PiUsage
{
    /// <summary>Gets the number of input tokens.</summary>
    public long Input { get; init; }

    /// <summary>Gets the number of output tokens.</summary>
    public long Output { get; init; }

    /// <summary>Gets the number of cached input tokens read.</summary>
    public long CacheRead { get; init; }

    /// <summary>Gets the number of input tokens written to cache.</summary>
    public long CacheWrite { get; init; }

    /// <summary>Gets the provider-reported total token count.</summary>
    public long TotalTokens { get; init; }

    /// <summary>Gets optional provider cost information.</summary>
    public PiCost? Cost { get; init; }

    /// <summary>Adds two usage snapshots component by component.</summary>
    /// <param name="left">The first usage value.</param>
    /// <param name="right">The second usage value.</param>
    /// <returns>The combined usage value.</returns>
    public static PiUsage operator +(PiUsage left, PiUsage right) =>
        new()
        {
            Input = left.Input + right.Input,
            Output = left.Output + right.Output,
            CacheRead = left.CacheRead + right.CacheRead,
            CacheWrite = left.CacheWrite + right.CacheWrite,
            TotalTokens = left.TotalTokens + right.TotalTokens,
            Cost = AddCost(left.Cost, right.Cost),
        };

    private static PiCost? AddCost(PiCost? left, PiCost? right)
    {
        if (left == null && right == null)
        {
            return null;
        }

        left ??= new PiCost();
        right ??= new PiCost();
        return new PiCost
        {
            Input = left.Input + right.Input,
            Output = left.Output + right.Output,
            CacheRead = left.CacheRead + right.CacheRead,
            CacheWrite = left.CacheWrite + right.CacheWrite,
            Total = left.Total + right.Total,
        };
    }
}

/// <summary>Defines the polymorphic base for content blocks in Pi messages.</summary>
[JsonConverter(typeof(PiContentJsonConverter))]
public abstract class PiContent
{
    /// <summary>Gets the Pi protocol content discriminator.</summary>
    public abstract string Type { get; }
}

/// <summary>Represents a text content block.</summary>
public sealed class PiTextContent : PiContent
{
    /// <inheritdoc />
    public override string Type => "text";

    /// <summary>Gets the text value.</summary>
    public string Text { get; init; } = string.Empty;
}

/// <summary>Represents an inline base64 image content block.</summary>
public sealed class PiImageContent : PiContent
{
    /// <inheritdoc />
    public override string Type => "image";

    /// <summary>Gets the base64-encoded image bytes.</summary>
    public string Data { get; init; } = string.Empty;

    /// <summary>Gets the image media type.</summary>
    public string MimeType { get; init; } = string.Empty;
}

/// <summary>Represents a provider reasoning content block.</summary>
public sealed class PiThinkingContent : PiContent
{
    /// <inheritdoc />
    public override string Type => "thinking";

    /// <summary>Gets the reasoning text.</summary>
    public string Thinking { get; init; } = string.Empty;
}

/// <summary>Represents a completed Tool Call emitted by a Pi Assistant message.</summary>
public sealed class PiToolCallContent : PiContent
{
    /// <inheritdoc />
    public override string Type => "toolCall";

    /// <summary>Gets the provider-issued Tool Call identifier.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the Pi Tool name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the Tool arguments as their original JSON value.</summary>
    public JsonElement Arguments { get; init; }
}

/// <summary>Preserves an unrecognized Pi content block for forward compatibility.</summary>
public sealed class PiUnknownContent : PiContent
{
    /// <summary>Initializes an unknown content block.</summary>
    /// <param name="type">The unrecognized protocol discriminator.</param>
    /// <param name="raw">The complete raw JSON object.</param>
    public PiUnknownContent(string type, JsonElement raw)
    {
        Type = type;
        Raw = raw;
    }

    /// <inheritdoc />
    public override string Type { get; }

    /// <summary>Gets the complete raw JSON object.</summary>
    public JsonElement Raw { get; }
}

/// <summary>Defines the polymorphic base for messages stored or emitted by Pi.</summary>
[JsonConverter(typeof(PiMessageJsonConverter))]
public abstract class PiMessage
{
    /// <summary>Gets the Pi protocol message-role discriminator.</summary>
    public abstract string Role { get; }

    /// <summary>Gets the provider timestamp expressed as Unix time in milliseconds.</summary>
    public long Timestamp { get; init; }
}

/// <summary>Represents a User message in Pi session state.</summary>
public sealed class PiUserMessage : PiMessage
{
    /// <inheritdoc />
    public override string Role => "user";

    /// <summary>Gets the original User content, which may be text or a content array.</summary>
    public JsonElement Content { get; init; }
}

/// <summary>Represents an authoritative Assistant message emitted by Pi.</summary>
public sealed class PiAssistantMessage : PiMessage
{
    /// <inheritdoc />
    public override string Role => "assistant";

    /// <summary>Gets the ordered Assistant content blocks.</summary>
    public IReadOnlyList<PiContent> Content { get; init; } = [];

    /// <summary>Gets the provider API identifier used for the request.</summary>
    public string? Api { get; init; }

    /// <summary>Gets the selected provider identifier.</summary>
    public string? Provider { get; init; }

    /// <summary>Gets the selected model identifier.</summary>
    public string? Model { get; init; }

    /// <summary>Gets authoritative usage for this Assistant message.</summary>
    public PiUsage? Usage { get; init; }

    /// <summary>Gets the provider stop reason.</summary>
    public string? StopReason { get; init; }

    /// <summary>Gets the provider error text when the message stopped with an error.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>Represents the authoritative result of one Pi Tool Call.</summary>
public sealed class PiToolResultMessage : PiMessage
{
    /// <inheritdoc />
    public override string Role => "toolResult";

    /// <summary>Gets the Tool Call identifier correlated with this result.</summary>
    public string ToolCallId { get; init; } = string.Empty;

    /// <summary>Gets the Pi Tool name.</summary>
    public string ToolName { get; init; } = string.Empty;

    /// <summary>Gets the ordered Tool Result content blocks.</summary>
    public IReadOnlyList<PiContent> Content { get; init; } = [];

    /// <summary>Gets Tool-specific result details as raw JSON.</summary>
    public JsonElement? Details { get; init; }

    /// <summary>Gets usage attributed to the Tool Result, when reported.</summary>
    public PiUsage? Usage { get; init; }

    /// <summary>Gets a value indicating whether Tool execution failed.</summary>
    public bool IsError { get; init; }
}

/// <summary>Represents a shell execution message created by Pi's direct RPC <c>bash</c> command.</summary>
/// <remarks>Direct RPC shell execution is separate from <c>turn_end.toolResults</c>.</remarks>
public sealed class PiBashExecutionMessage : PiMessage
{
    /// <inheritdoc />
    public override string Role => "bashExecution";

    /// <summary>Gets the executed shell command.</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>Gets the captured command output.</summary>
    public string Output { get; init; } = string.Empty;

    /// <summary>Gets the process exit code, or <see langword="null"/> when no exit code was produced.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Gets a value indicating whether execution was cancelled.</summary>
    public bool Cancelled { get; init; }

    /// <summary>Gets a value indicating whether the inline output was truncated.</summary>
    public bool Truncated { get; init; }

    /// <summary>Gets the path containing complete output when Pi persisted truncated output.</summary>
    public string? FullOutputPath { get; init; }
}

/// <summary>Represents an extension-defined custom Pi message.</summary>
public sealed class PiCustomMessage : PiMessage
{
    /// <inheritdoc />
    public override string Role => "custom";

    /// <summary>Gets the extension-defined custom message type.</summary>
    public string CustomType { get; init; } = string.Empty;

    /// <summary>Gets the custom message payload.</summary>
    public JsonElement Content { get; init; }

    /// <summary>Gets a value indicating whether Pi requests that the message be displayed.</summary>
    public bool Display { get; init; }

    /// <summary>Gets optional extension-defined details.</summary>
    public JsonElement? Details { get; init; }
}

/// <summary>Represents a summary created while branching Pi session history.</summary>
public sealed class PiBranchSummaryMessage : PiMessage
{
    /// <inheritdoc />
    public override string Role => "branchSummary";

    /// <summary>Gets the branch summary text.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Gets the source session-entry identifier summarized by this message.</summary>
    public string FromId { get; init; } = string.Empty;
}

/// <summary>Represents a summary created by Pi context compaction.</summary>
public sealed class PiCompactionSummaryMessage : PiMessage
{
    /// <inheritdoc />
    public override string Role => "compactionSummary";

    /// <summary>Gets the compaction summary text.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Gets the token estimate before compaction.</summary>
    public long TokensBefore { get; init; }
}

/// <summary>Preserves an unrecognized Pi message for forward compatibility.</summary>
public sealed class PiUnknownMessage : PiMessage
{
    /// <summary>Initializes an unknown Pi message.</summary>
    /// <param name="role">The unrecognized message role.</param>
    /// <param name="raw">The complete raw JSON object.</param>
    public PiUnknownMessage(string role, JsonElement raw)
    {
        Role = role;
        Raw = raw;
    }

    /// <inheritdoc />
    public override string Role { get; }

    /// <summary>Gets the complete raw JSON object.</summary>
    public JsonElement Raw { get; }
}
