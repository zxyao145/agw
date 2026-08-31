using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PiAgentSdk.MAF.Internal;

internal sealed class PiMafPrompt
{
    public required string Text { get; init; }

    public IReadOnlyList<PiImage> Images { get; init; } = [];
}

internal static class PiMafPromptBuilder
{
    private const string ImageOnlyPrompt = "Use the attached image or images as the input for this turn.";

    public static PiMafPrompt? Create(IReadOnlyList<ChatMessage> messages)
    {
        var entries = new List<(ChatRole Role, string Text)>();
        var images = new List<PiImage>();
        foreach (var message in messages)
        {
            var parts = new List<string>();
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                        parts.Add(text.Text);
                        break;
                    case FunctionResultContent result:
                        parts.Add(SerializeValue(result.Result));
                        break;
                    case UriContent uri:
                        parts.Add(uri.Uri.ToString());
                        break;
                    case DataContent data when message.Role == ChatRole.User && data.HasTopLevelMediaType("image"):
                        images.Add(new PiImage(Convert.ToBase64String(data.Data.Span), data.MediaType));
                        break;
                    case TextReasoningContent:
                        break;
                }
            }

            var value = string.Join("\n", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
            if (!string.IsNullOrWhiteSpace(value))
            {
                entries.Add((message.Role, value));
            }
        }

        if (entries.Count == 0 && images.Count == 0)
        {
            return null;
        }

        string prompt;
        if (entries.Count == 0)
        {
            prompt = ImageOnlyPrompt;
        }
        else if (entries.Count == 1 && entries[0].Role == ChatRole.User)
        {
            prompt = entries[0].Text;
        }
        else
        {
            prompt = string.Join("\n\n", entries.Select(entry => $"[{entry.Role}]\n{entry.Text}"));
        }

        return new PiMafPrompt { Text = prompt, Images = images };
    }

    private static string SerializeValue(object? value) =>
        value switch
        {
            null => string.Empty,
            string text => text,
            JsonElement element => element.GetRawText(),
            _ => JsonSerializer.Serialize(value),
        };
}
