using Agw.Agents.Execution.Runtimes;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data;
using ClaudeCodeSdk.MAF;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution;

internal static class AgwMessageUtil
{
    #region runtimes

    public static string ExtractInputText(AgwUserInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return string.Join(
            Environment.NewLine,
            input.Contents.Select(ExtractContentText).Where(static value => !string.IsNullOrWhiteSpace(value))
        );
    }

    private static string? ExtractContentText(AgwContent content)
    {
        return content switch
        {
            AgwTextContent text => text.Content,
            AgwTextReasoningContent textReasoning => textReasoning.Content,
            AgwErrorContent error => error.Content,
            AgwFunctionCallContent functionCall => functionCall.Content,
            AgwFunctionResultContent functionResult => functionResult.Content,
            AgwUriContent uri => uri.Uri.ToString(),
            _ => null,
        };
    }

    #endregion

    public static ChatMessage CreateUserChatMessage(AgwUserInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Contents);

        return new ChatMessage(ChatRole.User, ConvertToAIContents(input.Contents))
        {
            MessageId = string.IsNullOrWhiteSpace(input.MessageId) ? Guid.CreateVersion7().ToString() : input.MessageId,
            AuthorName = string.IsNullOrWhiteSpace(input.Author) ? Constants.DefaultInputAuthor : input.Author,
        };
    }

    public static List<ChatMessage> CreateExecutionInputMessages(
        AgwUserInput input,
        AgentRuntimeType targetType,
        Guid targetId,
        ConversationHandoff handoff
    )
    {
        ArgumentNullException.ThrowIfNull(handoff);

        var currentMessage = CreateUserChatMessage(input);
        ApplyTargetMetadata(currentMessage, targetType, targetId);
        ConversationHandoffMetadata.SetThroughSequence(currentMessage, handoff.ThroughSequence);

        var messages = new List<ChatMessage>(handoff.Messages.Count + 1);
        messages.AddRange(handoff.Messages);
        messages.Add(currentMessage);
        return messages;
    }

    private static void ApplyTargetMetadata(ChatMessage message, AgentRuntimeType targetType, Guid targetId)
    {
        var content = message.Contents.FirstOrDefault();
        if (content == null)
        {
            return;
        }

        content.AdditionalProperties ??= [];
        content.AdditionalProperties["targetType"] = targetType switch
        {
            AgentRuntimeType.Agent => "agent",
            AgentRuntimeType.Agentflow => "agentflow",
            _ => targetType.ToString().ToLowerInvariant(),
        };
        content.AdditionalProperties["targetId"] = targetId.ToString("D");
    }

    private static List<AIContent> ConvertToAIContents(IEnumerable<AgwContent> contents)
    {
        var aiContents = new List<AIContent>();
        foreach (var item in contents)
        {
            switch (item)
            {
                case AgwTextContent text:
                    aiContents.Add(
                        new TextContent(text.Content)
                        {
                            AdditionalProperties = CloneAdditionalProperties(text.AdditionalProperties),
                        }
                    );
                    break;

                case AgwUriContent uri:
                    aiContents.Add(
                        new UriContent(uri.Uri, uri.MediaType)
                        {
                            AdditionalProperties = CloneAdditionalProperties(uri.AdditionalProperties),
                        }
                    );
                    break;
            }
        }

        return aiContents;
    }

    private static AdditionalPropertiesDictionary? CloneAdditionalProperties(
        AdditionalPropertiesDictionary? additionalProperties
    ) => additionalProperties == null ? null : new AdditionalPropertiesDictionary(additionalProperties);

    #region agents

    /// <summary>
    /// 处理 result message
    /// </summary>
    /// <param name="session"></param>
    /// <param name="agwMessage"></param>
    /// <returns></returns>
    public static AgwMessage PostAgwMessage(AgentRuntime session, AgwMessage agwMessage)
    {
        if (IsResult(session, agwMessage))
        {
            agwMessage = agwMessage with { Author = Constants.DefaultAgentAuthor };
        }

        return agwMessage;
    }

    public static bool IsResult(AgentRuntime session, AgwMessage agwMessage)
    {
        if (session.Agent is ClaudeCodeAIAgent)
        {
            if (
                agwMessage.AdditionalProperties != null
                && agwMessage.AdditionalProperties.TryGetValue("type", out object? type)
            )
            {
                string? typeValue = (string?)type;
                if (typeValue == "result")
                {
                    return true;
                }
            }
        }

        return false;
    }

    #endregion
}
