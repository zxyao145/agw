using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Agw.Tools.HumanInteraction;

public static class HumanInteractionToolMetadata
{
    public const string SourceKey = "Agw.HumanInteraction.Source";
    private const string DescriptionKey = "Agw.HumanInteraction.Description";
    private static readonly JsonSerializerOptions JsonOptions = WebJsonOptions.Default;

    public static InteractionSource ReadSource(ChatOptions? options) =>
        options?.AdditionalProperties?.TryGetValue(SourceKey, out var value) == true
            ? value switch
            {
                InteractionSource source => source,
                JsonElement json => json.Deserialize<InteractionSource>(JsonOptions) ?? new(),
                _ => new(),
            }
            : new InteractionSource { NodeId = "standalone" };

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
