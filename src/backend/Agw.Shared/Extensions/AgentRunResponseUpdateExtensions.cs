using Agw.Shared.Models;
using Agw.Shared.Utils;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Shared;

public static class AgentRunResponseUpdateExtensions
{
    /// <summary>
    /// Convert AgentResponseUpdate to AiMessage DTO.
    /// </summary>
    public static AgwMessage? ToAiMessage(this ChatMessage? chatMessage)
    {
        if (chatMessage == null) return null;

        var contents = chatMessage.Contents
            .Select(ConvertContent)
            .OfType<AgwContent>()
            .ToList();

        return new AgwMessage(
            chatMessage.MessageId ?? "",
            chatMessage.AuthorName,
            chatMessage.Role,
            contents,
            chatMessage.AdditionalProperties
        );
    }

    /// <summary>
    /// Convert AgentResponseUpdate to AiMessage DTO.
    /// </summary>
    public static AgwMessage? ToAiMessage(this AgentResponseUpdate? update)
    {
        if (update == null) return null;

        var contents = update.Contents
            .Select(ConvertContent)
            .OfType<AgwContent>()
            .ToList();

        return new AgwMessage(
            update.MessageId ?? "",
            update.AuthorName,
            update.Role.HasValue ? update.Role.Value.Value : AiRole.Empty,
            contents,
            update.AdditionalProperties
        );
    }

    private static AgwContent? ConvertContent(AIContent content)
    {
        var additionalProps = content.AdditionalProperties ?? [];

        return content switch
        {
            TextContent text => new AgwTextContent { Content = text.Text, AdditionalProperties = content.AdditionalProperties },
            TextReasoningContent thinking => new AgwTextReasoningContent { Content = thinking.Text, AdditionalProperties = content.AdditionalProperties },
            FunctionCallContent call => CreateFunctionCallContent(call, additionalProps),
            FunctionResultContent result => CreateFunctionResultContent(result, additionalProps),
            ErrorContent error => new AgwErrorContent { Content = error.Message, AdditionalProperties = content.AdditionalProperties },
            UsageContent usage => new AgwUsageContent { Content = usage.Details, AdditionalProperties = content.AdditionalProperties },
            UriContent uriContent => new AgwUriContent(uriContent.Uri, uriContent.MediaType),
            DataContent dataContent => new AgwDataContent(dataContent.Uri, dataContent.MediaType),
            _ => null
        };
    }

    private static AgwContent CreateFunctionCallContent(FunctionCallContent call, AdditionalPropertiesDictionary props)
    {
        props["callId"] = call.CallId;
        props["toolName"] = call.Name;
        var content = call.Arguments == null ? "" : JsonUtil.Serialize(call.Arguments);
        return new AgwFunctionCallContent { Content = content, AdditionalProperties = props };
    }

    private static AgwContent CreateFunctionResultContent(FunctionResultContent result, AdditionalPropertiesDictionary props)
    {
        props["callId"] = result.CallId;
        var content = result.Result == null ? "" : JsonUtil.Serialize(result.Result);
        return new AgwFunctionResultContent { Content = content, AdditionalProperties = props };
    }
}

public static class AiMessageExtensions
{
    public static string Serialize(this AgwMessage message) => JsonUtil.Serialize(message);
}
