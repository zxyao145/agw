using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiAgentSdk;

internal static class PiProtocolJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            AllowOutOfOrderMetadataProperties = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new PiContentJsonConverter());
        options.Converters.Add(new PiMessageJsonConverter());
        options.Converters.Add(new PiAssistantDeltaJsonConverter());
        options.Converters.Add(new PiEventJsonConverter());
        return options;
    }

    internal static T DeserializeConcrete<T>(JsonElement element, JsonSerializerOptions options)
        where T : class =>
        element.Deserialize<T>(WithoutProtocolConverters(options))
        ?? throw new JsonException($"Unable to deserialize {typeof(T).Name}.");

    private static JsonSerializerOptions WithoutProtocolConverters(JsonSerializerOptions source)
    {
        var copy = new JsonSerializerOptions(source);
        for (var index = copy.Converters.Count - 1; index >= 0; index--)
        {
            if (
                copy.Converters[index]
                is PiContentJsonConverter
                    or PiMessageJsonConverter
                    or PiAssistantDeltaJsonConverter
                    or PiEventJsonConverter
            )
            {
                copy.Converters.RemoveAt(index);
            }
        }

        copy.Converters.Add(new PiContentJsonConverter());
        copy.Converters.Add(new PiMessageJsonConverter());
        copy.Converters.Add(new PiAssistantDeltaJsonConverter());
        return copy;
    }
}

internal sealed class PiContentJsonConverter : JsonConverter<PiContent>
{
    public override PiContent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement.Clone();
        var type = GetString(root, "type") ?? "unknown";
        return type switch
        {
            "text" => PiProtocolJson.DeserializeConcrete<PiTextContent>(root, options),
            "image" => PiProtocolJson.DeserializeConcrete<PiImageContent>(root, options),
            "thinking" => PiProtocolJson.DeserializeConcrete<PiThinkingContent>(root, options),
            "toolCall" => PiProtocolJson.DeserializeConcrete<PiToolCallContent>(root, options),
            _ => new PiUnknownContent(type, root),
        };
    }

    public override void Write(Utf8JsonWriter writer, PiContent value, JsonSerializerOptions options)
    {
        if (value is PiUnknownContent unknown)
        {
            unknown.Raw.WriteTo(writer);
            return;
        }

        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }

    internal static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

internal sealed class PiMessageJsonConverter : JsonConverter<PiMessage>
{
    public override PiMessage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement.Clone();
        var role = PiContentJsonConverter.GetString(root, "role") ?? "unknown";
        return role switch
        {
            "user" => PiProtocolJson.DeserializeConcrete<PiUserMessage>(root, options),
            "assistant" => PiProtocolJson.DeserializeConcrete<PiAssistantMessage>(root, options),
            "toolResult" => PiProtocolJson.DeserializeConcrete<PiToolResultMessage>(root, options),
            "bashExecution" => PiProtocolJson.DeserializeConcrete<PiBashExecutionMessage>(root, options),
            "custom" => PiProtocolJson.DeserializeConcrete<PiCustomMessage>(root, options),
            "branchSummary" => PiProtocolJson.DeserializeConcrete<PiBranchSummaryMessage>(root, options),
            "compactionSummary" => PiProtocolJson.DeserializeConcrete<PiCompactionSummaryMessage>(root, options),
            _ => new PiUnknownMessage(role, root),
        };
    }

    public override void Write(Utf8JsonWriter writer, PiMessage value, JsonSerializerOptions options)
    {
        if (value is PiUnknownMessage unknown)
        {
            unknown.Raw.WriteTo(writer);
            return;
        }

        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}

internal sealed class PiAssistantDeltaJsonConverter : JsonConverter<PiAssistantDelta>
{
    public override PiAssistantDelta Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement.Clone();
        var type = PiContentJsonConverter.GetString(root, "type") ?? "unknown";
        return type switch
        {
            "text_delta" => PiProtocolJson.DeserializeConcrete<PiTextDelta>(root, options),
            "thinking_delta" => PiProtocolJson.DeserializeConcrete<PiThinkingDelta>(root, options),
            "toolcall_start" => PiProtocolJson.DeserializeConcrete<PiToolCallStartDelta>(root, options),
            "toolcall_delta" => PiProtocolJson.DeserializeConcrete<PiToolCallArgumentsDelta>(root, options),
            "toolcall_end" => PiProtocolJson.DeserializeConcrete<PiToolCallEndDelta>(root, options),
            "text_start" or "text_end" or "thinking_start" or "thinking_end" => new PiContentBoundaryDelta(type)
            {
                ContentIndex = GetInt(root, "contentIndex"),
                Content = PiContentJsonConverter.GetString(root, "content"),
            },
            _ => new PiUnknownAssistantDelta(type, root) { ContentIndex = GetInt(root, "contentIndex") },
        };
    }

    public override void Write(Utf8JsonWriter writer, PiAssistantDelta value, JsonSerializerOptions options)
    {
        if (value is PiUnknownAssistantDelta unknown)
        {
            unknown.Raw.WriteTo(writer);
            return;
        }

        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }

    private static int GetInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;
}

internal sealed class PiEventJsonConverter : JsonConverter<PiEvent>
{
    public override PiEvent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement.Clone();
        var type = PiContentJsonConverter.GetString(root, "type") ?? "unknown";
        return type switch
        {
            "agent_start" or "agent_settled" or "turn_start" => new PiMarkerEvent(type),
            "agent_end" => PiProtocolJson.DeserializeConcrete<PiAgentEndEvent>(root, options),
            "turn_end" => PiProtocolJson.DeserializeConcrete<PiTurnEndEvent>(root, options),
            "message_start" or "message_end" => ReadMessageEvent(root, type, options),
            "message_update" => PiProtocolJson.DeserializeConcrete<PiMessageUpdateEvent>(root, options),
            "tool_execution_start" or "tool_execution_update" or "tool_execution_end" => ReadToolExecutionEvent(
                root,
                type,
                options
            ),
            "compaction_start" or "compaction_end" => ReadCompactionEvent(root, type, options),
            "auto_retry_start" or "auto_retry_end" => ReadRetryEvent(root, type, options),
            "queue_update" => PiProtocolJson.DeserializeConcrete<PiQueueUpdateEvent>(root, options),
            "extension_error" => PiProtocolJson.DeserializeConcrete<PiExtensionErrorEvent>(root, options),
            "extension_ui_request" => new PiExtensionUiRequestEvent
            {
                Request = PiProtocolJson.DeserializeConcrete<PiExtensionUiRequest>(root, options),
            },
            _ => new PiUnknownEvent(type, root),
        };
    }

    public override void Write(Utf8JsonWriter writer, PiEvent value, JsonSerializerOptions options)
    {
        if (value is PiUnknownEvent unknown)
        {
            unknown.Raw.WriteTo(writer);
            return;
        }

        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }

    private static PiMessageEvent ReadMessageEvent(JsonElement root, string type, JsonSerializerOptions options) =>
        new(type)
        {
            Message = root.TryGetProperty("message", out var message) ? message.Deserialize<PiMessage>(options) : null,
        };

    private static PiToolExecutionEvent ReadToolExecutionEvent(
        JsonElement root,
        string type,
        JsonSerializerOptions options
    )
    {
        var concrete = PiProtocolJson.DeserializeConcrete<PiToolExecutionEventData>(root, options);
        return new PiToolExecutionEvent(type)
        {
            ToolCallId = concrete.ToolCallId,
            ToolName = concrete.ToolName,
            Args = concrete.Args,
            PartialResult = concrete.PartialResult,
            Result = concrete.Result,
            IsError = concrete.IsError,
        };
    }

    private static PiCompactionEvent ReadCompactionEvent(JsonElement root, string type, JsonSerializerOptions options)
    {
        var concrete = PiProtocolJson.DeserializeConcrete<PiCompactionEventData>(root, options);
        return new PiCompactionEvent(type)
        {
            Reason = concrete.Reason,
            Result = concrete.Result,
            Aborted = concrete.Aborted,
            WillRetry = concrete.WillRetry,
            ErrorMessage = concrete.ErrorMessage,
        };
    }

    private static PiRetryEvent ReadRetryEvent(JsonElement root, string type, JsonSerializerOptions options)
    {
        var concrete = PiProtocolJson.DeserializeConcrete<PiRetryEventData>(root, options);
        return new PiRetryEvent(type)
        {
            Attempt = concrete.Attempt,
            MaxAttempts = concrete.MaxAttempts,
            DelayMs = concrete.DelayMs,
            Success = concrete.Success,
            ErrorMessage = concrete.ErrorMessage,
            FinalError = concrete.FinalError,
        };
    }

    private sealed class PiToolExecutionEventData
    {
        public string ToolCallId { get; init; } = string.Empty;

        public string ToolName { get; init; } = string.Empty;

        public JsonElement Args { get; init; }

        public PiToolExecutionResult? PartialResult { get; init; }

        public PiToolExecutionResult? Result { get; init; }

        public bool IsError { get; init; }
    }

    private sealed class PiCompactionEventData
    {
        public string? Reason { get; init; }

        public PiCompactionResult? Result { get; init; }

        public bool Aborted { get; init; }

        public bool WillRetry { get; init; }

        public string? ErrorMessage { get; init; }
    }

    private sealed class PiRetryEventData
    {
        public int Attempt { get; init; }

        public int MaxAttempts { get; init; }

        public int DelayMs { get; init; }

        public bool Success { get; init; }

        public string? ErrorMessage { get; init; }

        public string? FinalError { get; init; }
    }
}
