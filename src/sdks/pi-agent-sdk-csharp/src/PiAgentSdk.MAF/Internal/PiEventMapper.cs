using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace PiAgentSdk.MAF.Internal;

internal sealed class PiEventMapper
{
    private const string AgentName = "pi";
    private const string ModelNamePropertyName = "modelName";
    private readonly string _responseId = Guid.CreateVersion7().ToString("N");
    private readonly HashSet<int> _emittedAssistantContentIndexes = [];
    private readonly string _configuredModelName;
    private string? _activeAssistantMessageId;
    private string _activeModelName;
    private int _messageSequence;
    private bool _assistantErrorEmitted;

    public PiEventMapper(string? configuredModelName = null)
    {
        _configuredModelName = NormalizeModelName(configuredModelName);
        _activeModelName = _configuredModelName;
    }

    public AgentResponseUpdate? ToUpdate(PiEvent evt)
    {
        if (evt is PiMessageEvent { Type: "message_start", Message: PiAssistantMessage startingAssistant })
        {
            _activeAssistantMessageId = CreateMessageId("assistant");
            _activeModelName = ResolveModelName(startingAssistant.Model, _configuredModelName);
            ResetAssistantTracking();
            return null;
        }

        var endsAssistantMessage = evt is PiMessageEvent { Type: "message_end", Message: PiAssistantMessage };
        var update = evt switch
        {
            PiMessageUpdateEvent message => MapMessageUpdate(message),
            PiToolExecutionEvent { Type: "tool_execution_start" or "tool_execution_update" } => null,
            PiToolExecutionEvent { Type: "tool_execution_end" } tool => MapToolEnd(tool),
            PiTurnEndEvent turnEnd => MapTurnEnd(turnEnd),
            PiMessageEvent { Type: "message_end", Message: PiAssistantMessage assistant } => MapAssistantEnd(assistant),
            PiRetryEvent { Type: "auto_retry_end", Success: false } retry => Fatal(
                "retry.failed",
                retry.FinalError ?? retry.ErrorMessage ?? "Pi retry attempts were exhausted."
            ),
            PiCompactionEvent { Type: "compaction_end" } compaction => MapCompactionEnd(compaction),
            PiExtensionErrorEvent extension => Status(
                "extension.error",
                extension.Error ?? "Pi extension failed.",
                isError: true
            ),
            PiExtensionUiRequestEvent { Request.IsDialog: false } ui => Status(
                $"extension.ui.{ui.Request.Method}",
                ui.Request.Message ?? ui.Request.Title ?? $"Pi UI event: {ui.Request.Method}"
            ),
            _ => null,
        };

        if (update != null)
        {
            update.AuthorName = AgentName;
            update.ResponseId = _responseId;
            update.MessageId = ResolveMessageId(evt, update.Role);
            SetModelName(update.AdditionalProperties, ResolveModelName(evt));
        }

        if (endsAssistantMessage)
        {
            _activeAssistantMessageId = null;
        }

        if (evt is PiTurnEndEvent)
        {
            _activeAssistantMessageId = null;
            _activeModelName = _configuredModelName;
            ResetAssistantTracking();
        }

        return update;
    }

    public static IReadOnlyList<ChatMessage> ToHistoryMessages(
        PiTurnEndEvent turnEnd,
        string? configuredModelName = null
    )
    {
        var messages = new List<ChatMessage>();
        var modelName = ResolveModelName((turnEnd.Message as PiAssistantMessage)?.Model, configuredModelName);
        if (turnEnd.Message is PiAssistantMessage assistant)
        {
            var contents = assistant.Content.Select(MapContent).OfType<AIContent>().ToList();
            if (!string.IsNullOrWhiteSpace(assistant.ErrorMessage))
            {
                contents.Add(CreateError(assistant.ErrorMessage, isFatal: true));
            }

            if (contents.Count > 0)
            {
                var additionalProperties = new AdditionalPropertiesDictionary { [ModelNamePropertyName] = modelName };
                messages.Add(
                    new ChatMessage(ChatRole.Assistant, contents)
                    {
                        AuthorName = AgentName,
                        MessageId = Guid.CreateVersion7().ToString("N"),
                        AdditionalProperties = additionalProperties,
                    }
                );
            }
        }

        foreach (var result in turnEnd.ToolResults.OfType<PiToolResultMessage>())
        {
            var contents = new List<AIContent>
            {
                new FunctionResultContent(result.ToolCallId, MapResult(result.Content)) { RawRepresentation = result },
            };
            if (result.IsError)
            {
                contents.Add(CreateError($"Pi tool '{result.ToolName}' failed.", isFatal: false));
            }

            messages.Add(
                new ChatMessage(ChatRole.Tool, contents)
                {
                    AuthorName = AgentName,
                    MessageId = Guid.CreateVersion7().ToString("N"),
                    AdditionalProperties = new AdditionalPropertiesDictionary { [ModelNamePropertyName] = modelName },
                }
            );
        }

        return messages;
    }

    public static UsageDetails ToUsageDetails(PiUsage usage) =>
        new()
        {
            InputTokenCount = usage.Input,
            OutputTokenCount = usage.Output,
            TotalTokenCount = usage.TotalTokens,
            CachedInputTokenCount = usage.CacheRead,
            ReasoningTokenCount = usage.Reasoning,
            AdditionalCounts = new AdditionalPropertiesDictionary<long> { ["cache_write"] = usage.CacheWrite },
        };

    public static UsageContent ToUsageContent(PiUsage usage)
    {
        var content = new UsageContent(ToUsageDetails(usage));
        if (usage.Cost != null)
        {
            content.AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["cost.input"] = usage.Cost.Input,
                ["cost.output"] = usage.Cost.Output,
                ["cost.cacheRead"] = usage.Cost.CacheRead,
                ["cost.cacheWrite"] = usage.Cost.CacheWrite,
                ["cost.total"] = usage.Cost.Total,
            };
        }

        return content;
    }

    private AgentResponseUpdate? MapMessageUpdate(PiMessageUpdateEvent message)
    {
        var update = message.AssistantMessageEvent switch
        {
            PiTextDelta text => CreateUpdate(ChatRole.Assistant, "message.update.text", new TextContent(text.Delta)),
            PiThinkingDelta thinking => CreateUpdate(
                ChatRole.Assistant,
                "message.update.thinking",
                new TextReasoningContent(thinking.Delta)
            ),
            PiToolCallEndDelta tool => CreateUpdate(
                ChatRole.Assistant,
                "message.update.toolcall",
                MapToolCall(tool.ToolCall)
            ),
            _ => null,
        };

        if (update != null)
        {
            _emittedAssistantContentIndexes.Add(message.AssistantMessageEvent.ContentIndex);
        }

        return update;
    }

    private static AgentResponseUpdate MapToolEnd(PiToolExecutionEvent tool)
    {
        var contents = new List<AIContent>
        {
            new FunctionResultContent(tool.ToolCallId, MapResult(tool.Result?.Content ?? []))
            {
                RawRepresentation = tool,
            },
        };
        if (tool.IsError)
        {
            contents.Add(CreateError($"Pi tool '{tool.ToolName}' failed.", isFatal: false));
        }

        return CreateUpdate(ChatRole.Tool, "tool.execution.end", contents);
    }

    private AgentResponseUpdate? MapAssistantEnd(PiAssistantMessage assistant)
    {
        var contents = MapMissingAssistantContents(assistant);
        AddAssistantError(assistant, contents);

        var role = contents.Any(IsAssistantContent) ? ChatRole.Assistant : ChatRole.System;
        return contents.Count == 0 ? null : CreateUpdate(role, "message.end", contents);
    }

    private AgentResponseUpdate? MapTurnEnd(PiTurnEndEvent turnEnd)
    {
        var contents = turnEnd.Message is PiAssistantMessage assistant ? MapMissingAssistantContents(assistant) : [];
        if (turnEnd.Message is PiAssistantMessage completedAssistant)
        {
            AddAssistantError(completedAssistant, contents);
        }

        var usage = new PiUsage();
        var hasUsage = false;
        if (turnEnd.Message is PiAssistantMessage { Usage: not null } usageAssistant)
        {
            usage += usageAssistant.Usage;
            hasUsage = true;
        }

        foreach (var result in turnEnd.ToolResults.OfType<PiToolResultMessage>())
        {
            if (result.Usage != null)
            {
                usage += result.Usage;
                hasUsage = true;
            }
        }

        if (hasUsage)
        {
            contents.Add(ToUsageContent(usage));
        }

        var role = contents.Any(IsAssistantContent) ? ChatRole.Assistant : ChatRole.System;
        var type = contents.Any(content => content is not UsageContent) ? "turn.end" : "turn.end.usage";
        return contents.Count == 0 ? null : CreateUpdate(role, type, contents);
    }

    private static AgentResponseUpdate? MapCompactionEnd(PiCompactionEvent compaction)
    {
        var contents = new List<AIContent>();
        if (compaction.Result?.Usage != null)
        {
            contents.Add(ToUsageContent(compaction.Result.Usage));
        }

        if (!string.IsNullOrWhiteSpace(compaction.ErrorMessage))
        {
            contents.Add(CreateError(compaction.ErrorMessage, isFatal: true));
        }

        return contents.Count == 0 ? null : CreateUpdate(ChatRole.System, "compaction.end", contents);
    }

    private static AIContent? MapContent(PiContent content) =>
        content switch
        {
            PiTextContent text => new TextContent(text.Text) { RawRepresentation = text },
            PiThinkingContent thinking => new TextReasoningContent(thinking.Thinking) { RawRepresentation = thinking },
            PiImageContent image => MapImage(image),
            PiToolCallContent tool => MapToolCall(tool),
            _ => null,
        };

    private List<AIContent> MapMissingAssistantContents(PiAssistantMessage assistant)
    {
        var contents = new List<AIContent>();
        for (var index = 0; index < assistant.Content.Count; index++)
        {
            if (!_emittedAssistantContentIndexes.Add(index))
            {
                continue;
            }

            var mapped = MapContent(assistant.Content[index]);
            if (mapped != null)
            {
                contents.Add(mapped);
            }
        }

        return contents;
    }

    private void AddAssistantError(PiAssistantMessage assistant, ICollection<AIContent> contents)
    {
        if (_assistantErrorEmitted || !string.Equals(assistant.StopReason, "error", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        contents.Add(CreateError(assistant.ErrorMessage ?? "Pi provider returned an error.", isFatal: true));
        _assistantErrorEmitted = true;
    }

    private static DataContent MapImage(PiImageContent image)
    {
        try
        {
            return new DataContent(Convert.FromBase64String(image.Data), image.MimeType) { RawRepresentation = image };
        }
        catch (FormatException exception)
        {
            throw new PiProtocolException("Pi emitted image content with invalid base64 data.", exception);
        }
    }

    private static bool IsAssistantContent(AIContent content) =>
        content is TextContent or TextReasoningContent or FunctionCallContent;

    private static FunctionCallContent MapToolCall(PiToolCallContent tool)
    {
        var arguments = new Dictionary<string, object?>();
        if (tool.Arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in tool.Arguments.EnumerateObject())
            {
                arguments[property.Name] = property.Value.Clone();
            }
        }

        return new FunctionCallContent(tool.Id, tool.Name, arguments)
        {
            InformationalOnly = true,
            RawRepresentation = tool,
        };
    }

    private static object MapResult(IReadOnlyList<PiContent> content)
    {
        if (content.All(item => item is PiTextContent))
        {
            return string.Concat(content.OfType<PiTextContent>().Select(item => item.Text));
        }

        return content
            .Select(item =>
                item switch
                {
                    PiTextContent text => (object)
                        new Dictionary<string, object?> { ["type"] = "text", ["text"] = text.Text },
                    PiImageContent image => new Dictionary<string, object?>
                    {
                        ["type"] = "image",
                        ["data"] = image.Data,
                        ["mimeType"] = image.MimeType,
                    },
                    PiUnknownContent unknown => unknown.Raw.Clone(),
                    _ => JsonSerializer.SerializeToElement(item, item.GetType()),
                }
            )
            .ToList();
    }

    private static AgentResponseUpdate Status(string type, string message, bool isError = false) =>
        CreateUpdate(ChatRole.System, type, isError ? new ErrorContent(message) : new TextContent(message));

    private static AgentResponseUpdate Fatal(string type, string message) =>
        CreateUpdate(ChatRole.System, type, CreateError(message, isFatal: true));

    private static ErrorContent CreateError(string message, bool isFatal)
    {
        var error = new ErrorContent(message);
        if (isFatal)
        {
            error.AdditionalProperties = new AdditionalPropertiesDictionary { ["isFatalError"] = true };
        }

        return error;
    }

    private static AgentResponseUpdate CreateUpdate(ChatRole role, string type, params AIContent[] contents) =>
        CreateUpdate(role, type, (IReadOnlyList<AIContent>)contents);

    private static AgentResponseUpdate CreateUpdate(ChatRole role, string type, IReadOnlyList<AIContent> contents) =>
        new()
        {
            Role = role,
            AuthorName = AgentName,
            Contents = contents.ToList(),
            AdditionalProperties = new AdditionalPropertiesDictionary { ["type"] = type },
        };

    private string ResolveModelName(PiEvent evt) =>
        evt switch
        {
            PiMessageUpdateEvent => _activeModelName,
            PiMessageEvent { Message: PiAssistantMessage assistant } => ResolveModelName(
                assistant.Model,
                _configuredModelName
            ),
            PiTurnEndEvent { Message: PiAssistantMessage assistant } => ResolveModelName(
                assistant.Model,
                _configuredModelName
            ),
            _ => _activeModelName,
        };

    private static string ResolveModelName(string? reportedModelName, string? configuredModelName)
    {
        var modelName = NormalizeModelName(reportedModelName);
        return modelName.Length > 0 ? modelName : NormalizeModelName(configuredModelName);
    }

    private static string NormalizeModelName(string? modelName) =>
        string.IsNullOrWhiteSpace(modelName) ? string.Empty : modelName.Trim();

    private static void SetModelName(AdditionalPropertiesDictionary? properties, string modelName)
    {
        if (properties != null)
        {
            properties[ModelNamePropertyName] = modelName;
        }
    }

    private string ResolveMessageId(PiEvent evt, ChatRole? role)
    {
        if (
            evt is PiMessageUpdateEvent
            || evt is PiMessageEvent { Type: "message_end", Message: PiAssistantMessage }
            || evt is PiTurnEndEvent && role == ChatRole.Assistant
        )
        {
            return _activeAssistantMessageId ??= CreateMessageId("assistant");
        }

        return CreateMessageId(evt is PiToolExecutionEvent ? "tool" : "event");
    }

    private void ResetAssistantTracking()
    {
        _emittedAssistantContentIndexes.Clear();
        _assistantErrorEmitted = false;
    }

    private string CreateMessageId(string kind) => $"{_responseId}-{kind}-{++_messageSequence}";
}
