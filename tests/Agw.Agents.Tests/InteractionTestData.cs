using System.Text.Json;
using Agw.Agents.Execution.Outbound;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

internal static class InteractionTestData
{
    public static WorkflowGateInteraction Gate(
        string id,
        string nodeId = "node",
        string? name = null,
        string mode = "approval",
        string prompt = "Continue?",
        IReadOnlyList<ChatMessage>? messages = null
    ) =>
        new()
        {
            InteractionId = id,
            Source = new InteractionSource
            {
                NodeId = nodeId,
                NodeName = name,
                ProviderRequestId = id,
            },
            Mode = mode,
            Prompt = prompt,
            InputPreview = messages?.LastOrDefault()?.Text,
        };

    public static ToolApprovalInteraction Tool(
        string id,
        string name = "run_shell",
        string node = "standalone",
        string? callId = null
    ) =>
        new()
        {
            InteractionId = id,
            Prompt = "Allow tool?",
            Source = new InteractionSource
            {
                NodeId = node,
                ToolName = name,
                CallId = callId ?? $"call-{id}",
                ProviderRequestId = id,
            },
            Arguments = JsonSerializer.SerializeToElement(new { }),
        };

    public static UserInputInteraction Input(string id, string node = "standalone", string? callId = null) =>
        new()
        {
            InteractionId = id,
            Prompt = "Choose a color",
            InputKind = "questions",
            Source = new InteractionSource
            {
                NodeId = node,
                ToolName = "ask_user_question",
                CallId = callId ?? $"call-{id}",
                ProviderRequestId = id,
            },
            Payload = JsonSerializer.SerializeToElement(new { questions = new[] { new { question = "Color?" } } }),
        };

    public static InteractionResponse Decision(
        InteractionRequest request,
        bool approved,
        string? text = null,
        string scope = "once",
        JsonElement? data = null
    ) =>
        request switch
        {
            ToolApprovalInteraction => new ToolApprovalDecision
            {
                InteractionId = request.InteractionId,
                Approved = approved,
                Scope = Scope(scope),
            },
            WorkflowGateInteraction => new WorkflowGateDecision
            {
                InteractionId = request.InteractionId,
                Approved = approved,
                ResponseText = text,
            },
            UserInputInteraction => new UserInputResponse
            {
                InteractionId = request.InteractionId,
                Cancelled = !approved,
                ResponseData = data,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };

    public static ApprovalScope Scope(string scope) =>
        scope switch
        {
            "always-tool" => ApprovalScope.AlwaysTool,
            "always-arguments" => ApprovalScope.AlwaysArguments,
            _ => ApprovalScope.Once,
        };

    public static InteractionRequest Read(AgwMessage message) =>
        ((JsonElement)message.AdditionalProperties!["interaction"]!).Deserialize<InteractionRequest>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        )!;
}

internal sealed class InteractionTestSink : IExecutionMessageSink
{
    public List<AgwMessage> Messages { get; } = [];
    public Func<AgwMessage, CancellationToken, ValueTask>? OnWrite { get; set; }

    public async ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken)
    {
        Messages.Add(message);
        if (OnWrite is not null)
            await OnWrite(message, cancellationToken);
    }
}
