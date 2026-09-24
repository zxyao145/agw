using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Agw.Tools.HumanInteraction;

public static class HumanInteractionToolMetadata
{
    private const string DescriptionKey = "Agw.HumanInteraction.Description";
    private static readonly JsonSerializerOptions JsonOptions = WebJsonOptions.Default;

    /// <summary>
    /// 读取当前执行上下文投影出的交互来源；没有交互访问器时视为独立 Agent。
    /// Reads the interaction source projected from the current execution context; without an accessor the caller is a standalone Agent.
    /// </summary>
    public static InteractionSource CurrentSource(IHumanInteractionContextAccessor? accessor) =>
        accessor?.Source ?? new InteractionSource { NodeId = "standalone" };

    public static void Write(ToolApprovalRequestContent request, UserInputRequest description)
    {
        request.AdditionalProperties ??= [];
        request.AdditionalProperties[DescriptionKey] = JsonSerializer.SerializeToElement(description, JsonOptions);
    }

    public static UserInputRequest? Read(ToolApprovalRequestContent request) =>
        request.AdditionalProperties?.TryGetValue(DescriptionKey, out var value) == true && value is JsonElement json
            ? json.Deserialize<UserInputRequest>(JsonOptions)
            : null;
}
