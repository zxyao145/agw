using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agw.Agents.Execution.Agents.ExternalAgents.Acp.Protocol;

internal sealed record AcpImplementation(string Name, string Version, string? Title = null);

internal sealed record AcpInitializeRequest(
    int ProtocolVersion,
    object ClientCapabilities,
    AcpImplementation ClientInfo
)
{
    public static AcpInitializeRequest Create(string version) =>
        new(
            1,
            new
            {
                fs = new { readTextFile = false, writeTextFile = false },
                terminal = false,
                session = new { configOptions = new { boolean = new { } } },
            },
            new("agw", version, "Agw")
        );
}

internal sealed record AcpInitializeResponse(
    int ProtocolVersion,
    AcpAgentCapabilities AgentCapabilities,
    IReadOnlyList<AcpAuthMethod>? AuthMethods = null,
    AcpImplementation? AgentInfo = null
);

internal sealed record AcpAuthMethod(string Id, string Name, string? Type = null, string? Description = null)
{
    public static string Describe(IReadOnlyList<AcpAuthMethod> methods) =>
        methods.Count == 0 ? "none" : string.Join(", ", methods.Select(method => $"{method.Id} ({method.Name})"));
}

internal sealed record AcpAuthenticateRequest(string MethodId);

internal sealed record AcpAgentCapabilities(
    bool LoadSession = false,
    AcpPromptCapabilities? PromptCapabilities = null,
    AcpSessionCapabilities? SessionCapabilities = null
);

internal sealed record AcpPromptCapabilities(bool Image = false, bool Audio = false, bool EmbeddedContext = false);

internal sealed record AcpSessionCapabilities(
    JsonElement? Resume = null,
    JsonElement? Close = null,
    JsonElement? AdditionalDirectories = null
);

internal record AcpSessionStateResponse(
    AcpSessionModeState? Modes = null,
    IReadOnlyList<AcpSessionConfigOption>? ConfigOptions = null
);

internal sealed record AcpNewSessionResponse(
    string SessionId,
    AcpSessionModeState? Modes = null,
    IReadOnlyList<AcpSessionConfigOption>? ConfigOptions = null
) : AcpSessionStateResponse(Modes, ConfigOptions);

internal sealed record AcpSessionModeState(string CurrentModeId, IReadOnlyList<AcpSessionMode> AvailableModes);

internal sealed record AcpSessionMode(string Id, string Name, string? Description = null);

internal sealed record AcpSessionConfigOption(
    string Id,
    string Type,
    JsonElement CurrentValue,
    JsonElement? Options = null,
    string? Name = null,
    string? Category = null
);

internal sealed record AcpNewSessionRequest(
    string Cwd,
    IReadOnlyList<object> McpServers,
    IReadOnlyList<string>? AdditionalDirectories = null
);

internal sealed record AcpResumeSessionRequest(
    string SessionId,
    string Cwd,
    IReadOnlyList<object> McpServers,
    IReadOnlyList<string>? AdditionalDirectories = null
);

internal sealed record AcpLoadSessionRequest(
    string SessionId,
    string Cwd,
    IReadOnlyList<object> McpServers,
    IReadOnlyList<string>? AdditionalDirectories = null
);

internal sealed record AcpCloseSessionRequest(string SessionId);

internal sealed record AcpCancelNotification(string SessionId);

internal sealed record AcpSetModeRequest(string SessionId, string ModeId);

internal sealed record AcpSetConfigOptionRequest(string SessionId, string ConfigId, JsonElement Value)
{
    public string? Type => Value.ValueKind is JsonValueKind.True or JsonValueKind.False ? "boolean" : null;
}

internal sealed record AcpSetConfigOptionResponse(IReadOnlyList<AcpSessionConfigOption> ConfigOptions);

internal sealed record AcpPromptRequest(string SessionId, IReadOnlyList<AcpContentBlock> Prompt);

internal sealed record AcpPromptResponse(string StopReason);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", IgnoreUnrecognizedTypeDiscriminators = true)]
[JsonDerivedType(typeof(AcpTextContent), "text")]
[JsonDerivedType(typeof(AcpImageContent), "image")]
[JsonDerivedType(typeof(AcpAudioContent), "audio")]
[JsonDerivedType(typeof(AcpResourceLink), "resource_link")]
[JsonDerivedType(typeof(AcpEmbeddedResource), "resource")]
internal record AcpContentBlock;

internal sealed record AcpTextContent(string Text) : AcpContentBlock;

internal sealed record AcpImageContent(string Data, string MimeType) : AcpContentBlock;

internal sealed record AcpAudioContent(string Data, string MimeType) : AcpContentBlock;

internal sealed record AcpResourceLink(string Uri, string Name, string? MimeType = null) : AcpContentBlock;

internal sealed record AcpEmbeddedResource(AcpResourceContents Resource) : AcpContentBlock;

internal sealed record AcpResourceContents(
    string Uri,
    string? MimeType = null,
    string? Text = null,
    string? Blob = null
);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", IgnoreUnrecognizedTypeDiscriminators = true)]
[JsonDerivedType(typeof(AcpToolCallContentBlock), "content")]
[JsonDerivedType(typeof(AcpToolCallDiff), "diff")]
[JsonDerivedType(typeof(AcpToolCallTerminal), "terminal")]
[JsonDerivedType(typeof(AcpToolCallSession), "session")]
internal record AcpToolCallContent;

internal sealed record AcpToolCallContentBlock(AcpContentBlock Content) : AcpToolCallContent;

internal sealed record AcpToolCallDiff(string Path, string? OldText, string NewText) : AcpToolCallContent;

internal sealed record AcpToolCallTerminal(string TerminalId) : AcpToolCallContent;

internal sealed record AcpToolCallSession(string SessionId) : AcpToolCallContent;

internal sealed record AcpSessionNotification(string SessionId, AcpSessionUpdate Update);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "sessionUpdate", IgnoreUnrecognizedTypeDiscriminators = true)]
[JsonDerivedType(typeof(AcpAgentMessageChunk), "agent_message_chunk")]
[JsonDerivedType(typeof(AcpAgentThoughtChunk), "agent_thought_chunk")]
[JsonDerivedType(typeof(AcpUserMessageChunk), "user_message_chunk")]
[JsonDerivedType(typeof(AcpToolCall), "tool_call")]
[JsonDerivedType(typeof(AcpToolCallUpdate), "tool_call_update")]
[JsonDerivedType(typeof(AcpPlan), "plan")]
[JsonDerivedType(typeof(AcpCurrentModeUpdate), "current_mode_update")]
[JsonDerivedType(typeof(AcpConfigOptionUpdate), "config_option_update")]
internal record AcpSessionUpdate;

internal sealed record AcpAgentMessageChunk(AcpContentBlock Content, string? MessageId = null) : AcpSessionUpdate;

internal sealed record AcpAgentThoughtChunk(AcpContentBlock Content, string? MessageId = null) : AcpSessionUpdate;

internal sealed record AcpUserMessageChunk(AcpContentBlock Content, string? MessageId = null) : AcpSessionUpdate;

internal sealed record AcpToolCall(
    string ToolCallId,
    string Title,
    string? Kind = null,
    string? Status = null,
    IReadOnlyList<AcpToolCallContent>? Content = null,
    [property: JsonConverter(typeof(AcpRawValueConverter))] JsonElement? RawInput = null,
    [property: JsonConverter(typeof(AcpRawValueConverter))] JsonElement? RawOutput = null
) : AcpSessionUpdate;

internal sealed record AcpToolCallUpdate(
    string ToolCallId,
    string? Title = null,
    string? Kind = null,
    string? Status = null,
    IReadOnlyList<AcpToolCallContent>? Content = null,
    [property: JsonConverter(typeof(AcpRawValueConverter))] JsonElement? RawInput = null,
    [property: JsonConverter(typeof(AcpRawValueConverter))] JsonElement? RawOutput = null
) : AcpSessionUpdate;

internal sealed record AcpPlan(IReadOnlyList<AcpPlanEntry> Entries) : AcpSessionUpdate;

internal sealed record AcpPlanEntry(string Content, string Priority, string Status);

internal sealed record AcpCurrentModeUpdate(string CurrentModeId) : AcpSessionUpdate;

internal sealed record AcpConfigOptionUpdate(IReadOnlyList<AcpSessionConfigOption> ConfigOptions) : AcpSessionUpdate;

internal sealed record AcpRequestPermissionRequest(
    string SessionId,
    AcpToolCallUpdate ToolCall,
    IReadOnlyList<AcpPermissionOption> Options
);

internal sealed record AcpPermissionOption(string OptionId, string Name, string Kind);

internal sealed record AcpRequestPermissionResponse(AcpPermissionOutcome Outcome);

internal sealed record AcpPermissionOutcome(string Outcome, string? OptionId = null);

internal static class AcpStopReasons
{
    public const string EndTurn = "end_turn",
        MaxTokens = "max_tokens",
        MaxTurnRequests = "max_turn_requests",
        Refusal = "refusal",
        Cancelled = "cancelled";
}

internal static class AcpToolCallStatuses
{
    public const string Pending = "pending",
        InProgress = "in_progress",
        Completed = "completed",
        Failed = "failed";
}

internal static class AcpPermissionOptionKinds
{
    public const string AllowOnce = "allow_once",
        AllowAlways = "allow_always",
        RejectOnce = "reject_once",
        RejectAlways = "reject_always";
}

internal static class AcpRpcErrorCodes
{
    public const int MethodNotFound = -32601,
        AuthRequired = -32000,
        ResourceNotFound = -32002;
}
