using Agw.Shared.Contracts.Agents;

using Microsoft.Extensions.AI;

namespace Agw.Shared.Extensions;

public static class ChatMessageContentExtensions
{
    public static List<AIContent> WithoutBlankTextualContent(
        this IEnumerable<AIContent> contents,
        AdditionalPropertiesDictionary? messageProperties)
    {
        ArgumentNullException.ThrowIfNull(contents);

        var preserveEmptyText = messageProperties.IsToolMessage();
        return contents
            .Where(content => content switch
            {
                TextContent text => preserveEmptyText || !string.IsNullOrWhiteSpace(text.Text),
                TextReasoningContent reasoning => !string.IsNullOrWhiteSpace(reasoning.Text),
                _ => true
            })
            .ToList();
    }

    public static bool IsToolMessage(this AdditionalPropertiesDictionary? properties) =>
        properties?.TryGetValue("type", out var type) == true &&
        ToolMessageTypes.IsToolMessage(type?.ToString());
}
