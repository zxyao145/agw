using Agw.Agents.Execution.Runtimes;
using Agw.Shared.Exceptions;
using ClaudeCodeSdk.MAF;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution;

internal static class AgwMessageUtil
{
    private const int MaxImageCount = 5;
    private const int MaxImageBytes = 5 * 1024 * 1024;
    private const int MaxTotalImageBytes = 10 * 1024 * 1024;

    private static readonly HashSet<string> SupportedImageMediaTypes =
    [
        "image/jpeg",
        "image/png",
        "image/gif",
        "image/webp",
    ];

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
        ValidateImageContents(input.Contents);

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

                case AgwDataContent data:
                    aiContents.Add(
                        new DataContent(data.Data, data.MediaType)
                        {
                            Name = data.Name,
                            AdditionalProperties = CloneAdditionalProperties(data.AdditionalProperties),
                        }
                    );
                    break;
            }
        }

        return aiContents;
    }

    private static void ValidateImageContents(IEnumerable<AgwContent> contents)
    {
        var images = contents.OfType<AgwDataContent>().ToList();
        if (images.Count > MaxImageCount)
        {
            throw new AgwException(ErrorCodes.InvalidParam, $"You can attach up to {MaxImageCount} images.");
        }

        var totalBytes = 0;
        foreach (var image in images)
        {
            if (!SupportedImageMediaTypes.Contains(image.MediaType))
            {
                throw new AgwException(ErrorCodes.InvalidParam, "Unsupported image type. Use JPEG, PNG, GIF, or WebP.");
            }

            var imageBytes = image.Data.Length;
            if (imageBytes > MaxImageBytes)
            {
                throw new AgwException(ErrorCodes.InvalidParam, $"{image.Name ?? "Image"} exceeds the 5 MB limit.");
            }

            totalBytes += imageBytes;
        }

        if (totalBytes > MaxTotalImageBytes)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Images can total up to 10 MB.");
        }
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
