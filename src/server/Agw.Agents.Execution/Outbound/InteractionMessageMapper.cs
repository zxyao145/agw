using System.Text.Json;
using Agw.Shared.Utils;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Outbound;

/// <summary>The single wire projection for live, persisted and replayed interactions.</summary>
internal static class InteractionMessageMapper
{
    private static readonly JsonSerializerOptions JsonOptions = WebJsonOptions.Default;

    public static AgwMessage Create(
        InteractionRequest request,
        string messageId,
        Guid? executionId = null,
        string? streamingScopeId = null
    )
    {
        var properties = new AdditionalPropertiesDictionary
        {
            ["type"] = AgwMessageTypes.InteractionRequest,
            ["interaction"] = JsonSerializer.SerializeToElement<InteractionRequest>(
                request is UserInputInteraction input ? input with { Arguments = null } : request,
                JsonOptions
            ),
        };
        if (executionId.HasValue)
            properties["executionId"] = executionId.Value.ToString("D");
        if (!string.IsNullOrWhiteSpace(streamingScopeId))
            properties["streamingScopeId"] = streamingScopeId;
        return new AgwMessage(
            messageId,
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwTextContent { Content = request.Prompt }],
            properties
        );
    }
}
