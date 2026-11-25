using System.Text;
using System.Text.Json;
using Agw.Agents.Execution.Agents.ExternalAgents.Acp.Protocol;
using Agw.Agents.Execution.Agents.Tools;
using DiffPlex;
using DiffPlex.Renderer;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents.ExternalAgents.Acp;

internal sealed class AcpUpdateMapper(string responseId, ILogger? logger = null)
{
    private const string AuthorName = "acp";
    private const string MissingToolResult = "The ACP agent ended the turn before this tool call finished.";

    private static readonly UnidiffRenderer DiffRenderer = new(new Differ(), 3);
    private readonly Dictionary<string, AcpToolCallState> _toolCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StringBuilder> _bodyText = new(StringComparer.Ordinal);
    private string? _lastBodyMessageId;
    private AcpAgentMessageChunk? _lastBodyChunk;
    private string? _anonymousChunkKind;
    private int _messageNumber;
    private int _thoughtNumber;

    public IReadOnlyList<AgentResponseUpdate> Map(AcpSessionUpdate update)
    {
        if (update is AcpAgentMessageChunk message)
        {
            return MapMessage(message);
        }

        if (update is AcpAgentThoughtChunk thought)
        {
            var id = GetChunkId(thought.MessageId, "thought");
            return thought.Content is AcpTextContent text
                ? [CreateUpdate(ChatRole.Assistant, id, [new TextReasoningContent(text.Text)], thought)]
                : [];
        }

        _anonymousChunkKind = null;
        return update switch
        {
            AcpToolCall call => MapToolCall(
                new AcpToolCallUpdate(
                    call.ToolCallId,
                    call.Title,
                    call.Kind,
                    call.Status,
                    call.Content,
                    call.RawInput,
                    call.RawOutput
                ),
                call
            ),
            AcpToolCallUpdate patch => MapToolCall(patch, patch),
            AcpPlan plan => [MapPlan(plan)],
            AcpUserMessageChunk or AcpCurrentModeUpdate or AcpConfigOptionUpdate => [],
            _ => IgnoreUpdate(update),
        };
    }

    public IReadOnlyList<AgentResponseUpdate> EnsureToolCall(AcpToolCallUpdate toolCall)
    {
        _anonymousChunkKind = null;
        return MapToolCall(toolCall, toolCall, forceCall: true);
    }

    public AcpToolCallState? GetToolCall(string toolCallId) => _toolCalls.GetValueOrDefault(toolCallId);

    public IReadOnlyList<AgentResponseUpdate> Complete()
    {
        var updates = new List<AgentResponseUpdate>();
        foreach (var state in _toolCalls.Values)
        {
            if (!state.CallEmitted)
            {
                updates.Add(CreateToolCall(state));
            }

            if (!state.ResultEmitted)
            {
                updates.Add(CreateToolResult(state, MissingToolResult, MissingToolResult));
            }
        }

        return updates;
    }

    public AgentResponseUpdate? CreateResult()
    {
        if (_lastBodyMessageId is null || !_bodyText.TryGetValue(_lastBodyMessageId, out var body))
        {
            return null;
        }

        var text = body.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var result = CreateUpdate(ChatRole.Assistant, $"{responseId}-result", [new TextContent(text)], _lastBodyChunk);
        result.AdditionalProperties = new()
        {
            [AgwMessageClassifier.TypeKey] = AgwMessageClassifier.ResultType,
            [AgwMessageClassifier.ResultSourceMessageIdKey] = _lastBodyMessageId,
        };
        return result;
    }

    private IReadOnlyList<AgentResponseUpdate> MapMessage(AcpAgentMessageChunk message)
    {
        var id = GetChunkId(message.MessageId, "message");
        var content = MapContent(message.Content);
        if (content is null)
        {
            return [];
        }

        _lastBodyMessageId = id;
        _lastBodyChunk = message;
        if (content is TextContent text)
        {
            if (!_bodyText.TryGetValue(id, out var body))
            {
                body = new StringBuilder();
                _bodyText.Add(id, body);
            }

            body.Append(text.Text);
        }

        return [CreateUpdate(ChatRole.Assistant, id, [content], message)];
    }

    private string GetChunkId(string? providerId, string kind)
    {
        if (providerId is not null)
        {
            _anonymousChunkKind = null;
            return $"acp:{providerId}";
        }

        if (_anonymousChunkKind != kind)
        {
            _anonymousChunkKind = kind;
            if (kind == "message")
            {
                _messageNumber++;
            }
            else
            {
                _thoughtNumber++;
            }
        }

        var number = kind == "message" ? _messageNumber : _thoughtNumber;
        return $"{responseId}-{kind}-{number}";
    }

    private static AIContent? MapContent(AcpContentBlock content) =>
        content switch
        {
            AcpTextContent text => new TextContent(text.Text),
            AcpImageContent image => new DataContent(Convert.FromBase64String(image.Data), image.MimeType),
            AcpAudioContent audio => new DataContent(Convert.FromBase64String(audio.Data), audio.MimeType),
            AcpResourceLink link => new UriContent(link.Uri, link.MimeType),
            AcpEmbeddedResource { Resource.Text: { } text } => new TextContent(text),
            AcpEmbeddedResource { Resource: { Blob: { } blob } resource } => new DataContent(
                Convert.FromBase64String(blob),
                resource.MimeType ?? "application/octet-stream"
            ),
            _ => null,
        };

    private IReadOnlyList<AgentResponseUpdate> MapToolCall(
        AcpToolCallUpdate patch,
        AcpSessionUpdate rawUpdate,
        bool forceCall = false
    )
    {
        if (!_toolCalls.TryGetValue(patch.ToolCallId, out var state))
        {
            state = new AcpToolCallState(patch.ToolCallId, rawUpdate);
            _toolCalls.Add(patch.ToolCallId, state);
            _lastBodyMessageId = null;
            _lastBodyChunk = null;
        }

        state.Title = patch.Title ?? state.Title;
        state.Kind = patch.Kind ?? state.Kind;
        state.Status = patch.Status ?? state.Status;
        state.Content = patch.Content ?? state.Content;
        state.RawInput = patch.RawInput ?? state.RawInput;
        state.RawOutput = patch.RawOutput ?? state.RawOutput;
        state.RawUpdate = rawUpdate;

        var updates = new List<AgentResponseUpdate>();
        var finished = state.Status is AcpToolCallStatuses.Completed or AcpToolCallStatuses.Failed;
        if (!state.CallEmitted && (forceCall || finished || state.Status == AcpToolCallStatuses.InProgress))
        {
            updates.Add(CreateToolCall(state));
        }

        if (!state.ResultEmitted && finished)
        {
            var text = state.Content is { Count: > 0 } content
                ? string.Join("\n", content.Select(FormatToolContent).OfType<string>())
                : state.RawOutput?.GetRawText() ?? state.Status!;
            var error =
                state.Status == AcpToolCallStatuses.Failed ? $"ACP tool '{state.Kind ?? "other"}' failed." : null;
            updates.Add(CreateToolResult(state, text, error));
        }

        return updates;
    }

    private AgentResponseUpdate CreateToolCall(AcpToolCallState state)
    {
        state.CallEmitted = true;
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (state.RawInput is { ValueKind: JsonValueKind.Object } input)
        {
            foreach (var property in input.EnumerateObject())
            {
                arguments[property.Name] = property.Value.Clone();
            }
        }
        else if (state.RawInput is { } value)
        {
            arguments["input"] = value.Clone();
        }

        var call = new FunctionCallContent(state.ToolCallId, state.Kind ?? "other", arguments)
        {
            InformationalOnly = true,
            RawRepresentation = state.RawUpdate,
        };
        if (state.Title is not null)
        {
            call.AdditionalProperties = new() { ["toolTitle"] = state.Title };
        }

        return CreateUpdate(ChatRole.Assistant, $"{responseId}-tool-{state.ToolCallId}", [call], state.RawUpdate);
    }

    private AgentResponseUpdate CreateToolResult(AcpToolCallState state, string text, string? error)
    {
        state.ResultEmitted = true;
        var result = new FunctionResultContent(state.ToolCallId, text) { RawRepresentation = state.RawUpdate };
        return CreateUpdate(
            ChatRole.Tool,
            $"{responseId}-tool-result-{state.ToolCallId}",
            error is null ? [result] : [result, new ErrorContent(error)],
            state.RawUpdate
        );
    }

    private static string? FormatToolContent(AcpToolCallContent content) =>
        content switch
        {
            AcpToolCallContentBlock { Content: AcpTextContent text } => text.Text,
            AcpToolCallContentBlock { Content: AcpImageContent image } => $"[image {image.MimeType}]",
            AcpToolCallContentBlock { Content: AcpAudioContent audio } => $"[audio {audio.MimeType}]",
            AcpToolCallContentBlock { Content: AcpResourceLink link } => link.Uri,
            AcpToolCallContentBlock { Content: AcpEmbeddedResource embedded } => embedded.Resource.Text
                ?? embedded.Resource.Uri,
            AcpToolCallDiff diff => DiffRenderer.Generate(
                diff.OldText ?? string.Empty,
                diff.NewText,
                diff.OldText is null ? "/dev/null" : $"a/{diff.Path}",
                $"b/{diff.Path}"
            ),
            AcpToolCallTerminal terminal => $"[terminal {terminal.TerminalId}]",
            AcpToolCallSession session => $"[session {session.SessionId}]",
            _ => null,
        };

    private AgentResponseUpdate MapPlan(AcpPlan plan)
    {
        var id = $"{responseId}-plan";
        var items = plan
            .Entries.Select(
                (entry, index) =>
                    new TodoSnapshotItem($"{responseId}:{index}", entry.Content, null, entry.Status == "completed")
            )
            .ToArray();
        var message = ToolStateSnapshots.CreateTodoSnapshotMessage("acp_plan", id, id, items);
        var update = ToolStateSnapshots.ToUpdate(message);
        update.AuthorName = AuthorName;
        update.ResponseId = responseId;
        update.RawRepresentation = plan;
        return update;
    }

    private IReadOnlyList<AgentResponseUpdate> IgnoreUpdate(AcpSessionUpdate update)
    {
        logger?.LogDebug("Ignoring unsupported ACP session update type {UpdateType}", update.GetType().Name);
        return [];
    }

    private AgentResponseUpdate CreateUpdate(
        ChatRole role,
        string id,
        IList<AIContent> contents,
        AcpSessionUpdate? rawUpdate
    ) =>
        new(role, contents)
        {
            AuthorName = AuthorName,
            MessageId = id,
            ResponseId = responseId,
            RawRepresentation = rawUpdate,
        };
}

internal sealed class AcpToolCallState(string toolCallId, AcpSessionUpdate rawUpdate)
{
    public string ToolCallId { get; } = toolCallId;
    public string? Title { get; set; }
    public string? Kind { get; set; }
    public string? Status { get; set; }
    public IReadOnlyList<AcpToolCallContent>? Content { get; set; }
    public JsonElement? RawInput { get; set; }
    public JsonElement? RawOutput { get; set; }
    public bool CallEmitted { get; set; }
    public bool ResultEmitted { get; set; }
    public AcpSessionUpdate RawUpdate { get; set; } = rawUpdate;
}
