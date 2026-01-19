using DSystem.Infrastructure;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace DSystem.Domain.Models;

public static class AgentRunResponseUpdateExtensions
{
    /// <summary>
    /// Convert AgentRunResponseUpdate to ClaudeCodeMessage DTO. agentRunResponseUpdate
    /// </summary>
    public static AiMessage? ToAiMessage(this AgentRunResponseUpdate? update)
    {
        if (update == null)
        {
            return null;
        }

        var contents = update.Contents;

        var aiMsgContents = contents.Select(content =>
        {
            var contentAadditionalProperties = content.AdditionalProperties ?? new AdditionalPropertiesDictionary();

            AiMessageContent? aiMsgContent = null;
            if (content is TextContent textContent)
            {
                aiMsgContent = new AiMessageContent(content.GetType().Name, textContent.Text, content.AdditionalProperties);
            }
            else if (content is FunctionCallContent call)
            {
                contentAadditionalProperties.Add("callId", call.CallId);
                aiMsgContent = new AiMessageContent(content.GetType().Name, call.Name, contentAadditionalProperties);
            }
            else if (content is FunctionResultContent callResult)
            {
                var callResultContent = callResult.Result == null
                    ? ""
                    : JsonUtil.Serialize(callResult.Result);
                contentAadditionalProperties.Add("callId", callResult.CallId);
                aiMsgContent = new AiMessageContent(content.GetType().Name, callResultContent, contentAadditionalProperties);
            }
            else if (content is TextReasoningContent thinkingContent)
            {
                var t = thinkingContent.Text;
                aiMsgContent = new AiMessageContent(content.GetType().Name, t, content.AdditionalProperties);
            }
            else if (content is ErrorContent error)
            {
                aiMsgContent = new AiMessageContent(content.GetType().Name, error.Message, content.AdditionalProperties);
            }
            else if (content is UsageContent usageContent)
            {
                aiMsgContent = new AiMessageContent(content.GetType().Name, usageContent.Details, content.AdditionalProperties);
            }
            return aiMsgContent;
        })
            .Where(x => x != null)
            .Select(x => x!)
            .ToList();

        var role = update.Role;

        var aiMessage = new AiMessage
            (
                update.MessageId ?? "",
                update.AuthorName,
                role.HasValue ? role.Value.Value : "",
                aiMsgContents,
                update.AdditionalProperties
            );

        return aiMessage;
    }
}


public static class AiMessageExtensions
{
    public static string Serialize(this AiMessage aiMessage)
    {
        return JsonUtil.Serialize(aiMessage);
    }
}
