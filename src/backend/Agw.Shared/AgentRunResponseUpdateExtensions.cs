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
            .OfType<AiMessageContent>()
            .ToList();

        return new AiMessage(
            chatMessage.MessageId ?? "",
            chatMessage.AuthorName,
            chatMessage.Role.Value,
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
            .OfType<AiMessageContent>()
            .ToList();

        return new AiMessage(
            update.MessageId ?? "",
            update.AuthorName,
            update.Role.HasValue ? update.Role.Value.Value : "",
            contents,
            update.AdditionalProperties
        );
    }

    private static AiMessageContent? ConvertContent(AIContent content)
    {
        var additionalProps = content.AdditionalProperties ?? [];

        return content switch
        {
            TextContent text => new(content.GetType().Name, text.Text, content.AdditionalProperties),
            FunctionCallContent call => CreateFunctionCallContent(call, additionalProps),
            FunctionResultContent result => CreateFunctionResultContent(result, additionalProps),
            TextReasoningContent thinking => new(content.GetType().Name, thinking.Text, content.AdditionalProperties),
            ErrorContent error => new(content.GetType().Name, error.Message, content.AdditionalProperties),
            UsageContent usage => new(content.GetType().Name, usage.Details, content.AdditionalProperties),
            _ => null
        };
    }

    private static AiMessageContent CreateFunctionCallContent(FunctionCallContent call, AdditionalPropertiesDictionary props)
    {
        props["callId"] = call.CallId;
        props["toolName"] = call.Name;
        var content = call.Arguments == null ? "" : JsonUtil.Serialize(call.Arguments);
        return new(call.GetType().Name, content, props);
    }

    private static AiMessageContent CreateFunctionResultContent(FunctionResultContent result, AdditionalPropertiesDictionary props)
    {
        props["callId"] = result.CallId;
        var content = result.Result == null ? "" : JsonUtil.Serialize(result.Result);
        return new(result.GetType().Name, content, props);
    }
}

public static class AiMessageExtensions
{
    public static string Serialize(this AiMessage message) => JsonUtil.Serialize(message);
}
