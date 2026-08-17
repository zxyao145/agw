using System.Text.Json;

using Agw.Shared.AgwMsgVm;
using Agw.Shared.Utils;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Shared.Extensions;

public static class AgentRunResponseUpdateExtensions
{
    /// <summary>
    /// Convert AgentResponseUpdate to AiMessage DTO.
    /// </summary>
    public static AgwMessage? ToAiMessage(this ChatMessage? chatMessage)
    {
        if (chatMessage == null) return null;

        var contents = ConvertContents(chatMessage.Contents, chatMessage.AdditionalProperties);

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

        var contents = ConvertContents(
            update.Contents,
            update.AdditionalProperties,
            preserveWhitespaceOnlyText: true);

        return new AgwMessage(
            update.MessageId ?? "",
            update.AuthorName,
            update.Role.HasValue ? update.Role.Value.Value : AiRole.Empty,
            contents,
            update.AdditionalProperties
        );
    }

    private static List<AgwContent> ConvertContents(
        IEnumerable<AIContent> contents,
        AdditionalPropertiesDictionary? messageProperties,
        bool preserveWhitespaceOnlyText = false)
    {
        var filteredContents = preserveWhitespaceOnlyText
            ? contents.WithoutEmptyStreamingTextualContent(messageProperties)
            : contents.WithoutBlankTextualContent(messageProperties);

        return filteredContents
            .Select(ConvertContent)
            .OfType<AgwContent>()
            .ToList();
    }

    private static AgwContent? ConvertContent(AIContent content)
    {
        var additionalProps = content.AdditionalProperties == null
            ? new AdditionalPropertiesDictionary()
            : new AdditionalPropertiesDictionary(content.AdditionalProperties);
        var citations = content.Annotations?
            .OfType<CitationAnnotation>()
            .Where(static citation => citation.Url != null)
            .Select(static citation => new
            {
                title = citation.Title,
                url = citation.Url!.ToString(),
                snippet = citation.Snippet,
                toolName = citation.ToolName
            })
            .ToArray();
        if (citations?.Length > 0)
        {
            additionalProps["citations"] = citations;
        }

        return content switch
        {
            TextContent text => new AgwTextContent { Content = text.Text, AdditionalProperties = additionalProps },
            TextReasoningContent thinking => new AgwTextReasoningContent { Content = thinking.Text, AdditionalProperties = additionalProps },
            FunctionCallContent call => CreateFunctionCallContent(call, additionalProps),
            FunctionResultContent result => CreateFunctionResultContent(result, additionalProps),
            ErrorContent error => new AgwErrorContent { Content = error.Message, AdditionalProperties = additionalProps },
            UsageContent usage => new AgwUsageContent { Content = usage.Details, AdditionalProperties = additionalProps },

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
        var content = Obj2String(result.Result);
        return new AgwFunctionResultContent { Content = content, AdditionalProperties = props };
    }


    private static string Obj2String(object? obj)
    {
        var content = obj switch
        {
            null => "",
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? "",
            _ => JsonUtil.Serialize(obj)
        };
        return content;
    }
}

public static class AiMessageExtensions
{
    public static string Serialize(this AgwMessage message) => JsonUtil.Serialize(message);
}
