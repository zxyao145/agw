using Agw.Shared.Contracts.Agents;

using Microsoft.Extensions.AI;

namespace Agw.Shared.Extensions;

public static class ChatMessageContentExtensions
{
    public static List<AIContent> WithoutBlankTextualContent(
        this IEnumerable<AIContent> contents,
        AdditionalPropertiesDictionary? messageProperties) =>
        FilterTextualContent(contents, messageProperties, preserveWhitespaceOnlyText: false);

    internal static List<AIContent> WithoutEmptyStreamingTextualContent(
        this IEnumerable<AIContent> contents,
        AdditionalPropertiesDictionary? messageProperties) =>
        FilterTextualContent(contents, messageProperties, preserveWhitespaceOnlyText: true);

    private static List<AIContent> FilterTextualContent(
        IEnumerable<AIContent> contents,
        AdditionalPropertiesDictionary? messageProperties,
        bool preserveWhitespaceOnlyText)
    {
        ArgumentNullException.ThrowIfNull(contents);

        var preserveEmptyText = messageProperties.IsToolMessage();
        return contents
            .Where(content => content switch
            {
                TextContent text => preserveEmptyText || IsMeaningfulText(text.Text, preserveWhitespaceOnlyText),
                TextReasoningContent reasoning => IsMeaningfulText(reasoning.Text, preserveWhitespaceOnlyText),
                _ => true
            })
            .ToList();
    }

    private static bool IsMeaningfulText(string text, bool preserveWhitespaceOnlyText) =>
        preserveWhitespaceOnlyText
            ? !string.IsNullOrEmpty(text)
            : !string.IsNullOrWhiteSpace(text);

    public static bool IsToolMessage(this AdditionalPropertiesDictionary? properties) =>
        properties?.TryGetValue("type", out var type) == true &&
        ToolMessageTypes.IsToolMessage(type?.ToString());
}
