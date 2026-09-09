using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agw.Agents.Contracts.Execution;

public sealed record InteractionSource
{
    public string? NodeId { get; init; }
    public string? NodeName { get; init; }
    public string? ToolName { get; init; }
    public string? CallId { get; init; }
    public string? ProviderRequestId { get; init; }
    public string? ProviderScopeId { get; init; }
}

/// <summary>A tool describes its input; the execution owns the interaction ID.</summary>
public sealed record UserInputRequest(string InputKind, string Prompt, JsonElement Payload)
{
    public InteractionSource Source { get; init; } = new();
    public JsonElement? Arguments { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ToolApprovalInteraction), "tool-approval")]
[JsonDerivedType(typeof(WorkflowGateInteraction), "workflow-gate")]
[JsonDerivedType(typeof(UserInputInteraction), "user-input")]
public abstract record InteractionRequest
{
    public required string InteractionId { get; init; }
    public required string Prompt { get; init; }
    public InteractionSource Source { get; init; } = new();
}

public sealed record ToolApprovalInteraction : InteractionRequest
{
    public JsonElement? Arguments { get; init; }
}

public sealed record WorkflowGateInteraction : InteractionRequest
{
    public string Mode { get; init; } = "approval";
    public string? InputPreview { get; init; }
}

public sealed record UserInputInteraction : InteractionRequest
{
    public required string InputKind { get; init; }
    public required JsonElement Payload { get; init; }
    public JsonElement? Arguments { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ApprovalScope>))]
public enum ApprovalScope
{
    Once,
    AlwaysTool,
    AlwaysArguments,
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ToolApprovalDecision), "tool-approval")]
[JsonDerivedType(typeof(WorkflowGateDecision), "workflow-gate")]
[JsonDerivedType(typeof(UserInputResponse), "user-input")]
public abstract record InteractionResponse
{
    public required string InteractionId { get; init; }
}

public sealed record ToolApprovalDecision : InteractionResponse
{
    public required bool Approved { get; init; }
    public ApprovalScope Scope { get; init; } = ApprovalScope.Once;
}

public sealed record WorkflowGateDecision : InteractionResponse
{
    public required bool Approved { get; init; }
    public string? ResponseText { get; init; }
}

public sealed record UserInputResponse : InteractionResponse
{
    public required bool Cancelled { get; init; }
    public JsonElement? ResponseData { get; init; }
}

public interface IHumanInteractionChannel
{
    ValueTask<UserInputResponse> RequestAsync(UserInputRequest request, CancellationToken cancellationToken);
}

public interface IHumanInteractionContextAccessor
{
    IHumanInteractionChannel? Current { get; }
    IInteractionRequestRegistry? Requests { get; }
}

/// <summary>Execution-owned descriptions survive SDK approval queues and durable boundaries.</summary>
public interface IInteractionRequestRegistry
{
    UserInputInteraction Register(string providerRequestId, UserInputRequest request);
    UserInputInteraction? Find(string providerRequestId, string scopeId);
    bool IsUserInputCall(string nodeId, string callId);
}
