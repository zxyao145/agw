using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Summaries;

internal static class AgentTurnResultText
{
    public static string NormalizeJson(string text)
    {
        // Accept complete JSON first so scalar values cannot be mistaken for embedded containers.
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            {
                throw InvalidResult();
            }

            return document.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            // Compatible endpoints may wrap their answer in a code fence or explanatory text.
        }

        var openingFence = Regex.Match(text, @"(?m)^[ \t]*(`{3,}|~{3,})[^\r\n]*\r?\n");
        if (openingFence.Success)
        {
            var bodyStart = openingFence.Index + openingFence.Length;
            var closingFence = new Regex(
                @"(?m)^[ \t]*" + Regex.Escape(openingFence.Groups[1].Value) + @"[ \t]*\r?$"
            ).Match(text, bodyStart);
            if (
                !closingFence.Success
                || ContainsContainerDelimiter(text[..openingFence.Index])
                || ContainsContainerDelimiter(text[(closingFence.Index + closingFence.Length)..])
            )
            {
                throw InvalidResult();
            }

            try
            {
                using var document = JsonDocument.Parse(text[bodyStart..closingFence.Index]);
                return document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                    ? document.RootElement.GetRawText()
                    : throw InvalidResult();
            }
            catch (JsonException)
            {
                throw InvalidResult();
            }
        }

        ReadOnlySpan<byte> remaining = Encoding.UTF8.GetBytes(text);
        string? json = null;
        while (!remaining.IsEmpty)
        {
            var start = remaining.IndexOfAny((byte)'{', (byte)'[');
            if (start < 0)
            {
                if (remaining.IndexOfAny((byte)'}', (byte)']') >= 0)
                {
                    throw InvalidResult();
                }
                break;
            }

            if (json != null || remaining[..start].IndexOfAny((byte)'}', (byte)']') >= 0)
            {
                throw InvalidResult();
            }

            remaining = remaining[start..];
            var reader = new Utf8JsonReader(remaining);
            try
            {
                using var document = JsonDocument.ParseValue(ref reader);
                json = document.RootElement.GetRawText();
                remaining = remaining[(int)reader.BytesConsumed..];
            }
            catch (JsonException)
            {
                // Do not salvage nested fragments from a malformed JSON document.
                throw InvalidResult();
            }
        }

        return json ?? throw InvalidResult();
    }

    private static bool ContainsContainerDelimiter(string text) => text.AsSpan().IndexOfAny("{}[]") >= 0;

    private static AgwException InvalidResult() =>
        new(
            ErrorCodes.AgentExecutionFailed,
            "Response Schema result must contain exactly one valid JSON object or array."
        );

    public static string? ExtractLastAssistantText(IReadOnlyList<ChatMessage> messages)
    {
        var finalMessage = messages.LastOrDefault(message =>
            message.Role == ChatRole.Assistant && message.Contents.OfType<TextContent>().Any()
        );
        if (finalMessage == null)
        {
            return null;
        }

        var text = string.Concat(finalMessage.Contents.OfType<TextContent>().Select(content => content.Text));
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
