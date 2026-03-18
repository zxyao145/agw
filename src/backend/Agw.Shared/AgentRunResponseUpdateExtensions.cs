using Agw.Shared.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Shared;

public static class AgentRunResponseUpdateExtensions
{
    /// <summary>
    /// Convert AgentResponseUpdate to AiMessage DTO.
    /// </summary>
    public static AiMessage? ToAiMessage(this ChatMessage? chatMessage)
    {
        if (chatMessage == null) return null;

        var contents = chatMessage.Contents
            .Select(ConvertContent)
            .OfType<AgwContent>()
            .ToList();

        return new AiMessage(
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
    public static AiMessage? ToAiMessage(this AgentResponseUpdate? update)
    {
        if (update == null) return null;

        var contents = update.Contents
            .Select(ConvertContent)
            .OfType<AgwContent>()
            .ToList();

        return new AiMessage(
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
            TextContent text => new AgwTextContent { Type = content.GetType().Name, Content = text.Text, AdditionalProperties = content.AdditionalProperties },
            TextReasoningContent thinking => new AgwTextReasoningContent { Type = content.GetType().Name, Content = thinking.Text, AdditionalProperties = content.AdditionalProperties },
            FunctionCallContent call => CreateFunctionCallContent(call, additionalProps),
            FunctionResultContent result => CreateFunctionResultContent(result, additionalProps),
            ErrorContent error => new AgwErrorContent { Type = content.GetType().Name, Content = error.Message, AdditionalProperties = content.AdditionalProperties },
            UsageContent usage => new AgwUsageContent { Type = content.GetType().Name, Content = usage.Details, AdditionalProperties = content.AdditionalProperties },
            _ => null
        };
    }

    private static AgwContent CreateFunctionCallContent(FunctionCallContent call, AdditionalPropertiesDictionary props)
    {
        props["callId"] = call.CallId;
        props["toolName"] = call.Name;
        var content = call.Arguments == null ? "" : JsonUtil.Serialize(call.Arguments);
        return new AgwFunctionCallContent { Type = call.GetType().Name, Content = content, AdditionalProperties = props };
    }

    private static AgwContent CreateFunctionResultContent(FunctionResultContent result, AdditionalPropertiesDictionary props)
    {
        props["callId"] = result.CallId;
        var content = result.Result == null ? "" : JsonUtil.Serialize(result.Result);
        return new AgwFunctionResultContent { Type = result.GetType().Name, Content = content, AdditionalProperties = props };
    }
}

public static class AiMessageExtensions
{
    public static string Serialize(this AiMessage message) => JsonUtil.Serialize(message);
}
