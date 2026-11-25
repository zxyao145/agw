using System.Globalization;
using Agw.Agents.Execution.Agents.ExternalAgents.Acp.Protocol;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.ExternalAgents.Acp;

internal static class AcpPromptBuilder
{
    public static IReadOnlyList<AcpContentBlock> Build(
        IEnumerable<ChatMessage> messages,
        AcpPromptCapabilities? capabilities
    )
    {
        var prompt = new List<AcpContentBlock>();
        var attachmentIndex = 0;
        foreach (var content in messages.SelectMany(static message => message.Contents))
        {
            switch (content)
            {
                case TextContent text:
                    prompt.Add(new AcpTextContent(text.Text));
                    break;
                case DataContent data when data.HasTopLevelMediaType("image"):
                    RequireCapability(capabilities?.Image, "image");
                    prompt.Add(new AcpImageContent(data.Base64Data.ToString(), data.MediaType));
                    break;
                case DataContent data when data.HasTopLevelMediaType("audio"):
                    RequireCapability(capabilities?.Audio, "audio");
                    prompt.Add(new AcpAudioContent(data.Base64Data.ToString(), data.MediaType));
                    break;
                case DataContent data:
                    RequireCapability(capabilities?.EmbeddedContext, "embedded");
                    attachmentIndex++;
                    var name = string.IsNullOrEmpty(data.Name)
                        ? attachmentIndex.ToString(CultureInfo.InvariantCulture)
                        : Uri.EscapeDataString(data.Name);
                    var resource = new AcpResourceContents(
                        $"agw:attachment/{name}",
                        data.MediaType,
                        Blob: data.Base64Data.ToString()
                    );
                    prompt.Add(new AcpEmbeddedResource(resource));
                    break;
                case UriContent uri:
                    var address = uri.Uri;
                    var path = address.IsAbsoluteUri ? address.AbsolutePath : address.OriginalString;
                    var segment = path.TrimEnd('/').Split('/')[^1];
                    prompt.Add(
                        new AcpResourceLink(
                            address.OriginalString,
                            segment.Length > 0 ? Uri.UnescapeDataString(segment) : address.OriginalString
                        )
                    );
                    break;
            }
        }

        return prompt.Count > 0
            ? prompt
            : throw new AgwException(ErrorCodes.InvalidParam, "The ACP prompt requires at least one content block.");
    }

    private static void RequireCapability(bool? supported, string capability)
    {
        if (supported != true)
        {
            throw new AgwException(ErrorCodes.InvalidParam, $"This ACP agent does not accept {capability} content.");
        }
    }
}
