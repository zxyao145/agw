using Agw.Shared;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public class AgwMessageUtilTests
{
    [Fact]
    public void CreateExecutionInputMessages_HandoffAndCurrentInput_PreservesOrderAndMetadata()
    {
        var targetId = Guid.CreateVersion7();
        var handoffMessage = new ChatMessage(ChatRole.Assistant, "previous plan") { MessageId = "handoff-message" };
        ConversationHandoffMetadata.MarkHandoffMessage(handoffMessage);
        var input = new AgwUserInput
        {
            MessageId = "current-message",
            Author = "requester",
            Contents =
            [
                new AgwTextContent
                {
                    Content = "implement it",
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["existing"] = "kept",
                        ["targetType"] = "wrong",
                    },
                },
            ],
        };

        var messages = AgwMessageUtil.CreateExecutionInputMessages(
            input,
            AgentRuntimeType.Agentflow,
            targetId,
            new ConversationHandoff([handoffMessage], 41)
        );

        Assert.Equal(["previous plan", "implement it"], messages.Select(message => message.Text));
        Assert.Equal("current-message", messages[1].MessageId);
        Assert.Equal("requester", messages[1].AuthorName);
        Assert.Equal(41L, messages[1].AdditionalProperties![ConversationHandoffMetadata.ThroughSequenceKey]);
        var text = Assert.IsType<TextContent>(Assert.Single(messages[1].Contents));
        Assert.Equal("kept", text.AdditionalProperties!["existing"]);
        Assert.Equal("agentflow", text.AdditionalProperties["targetType"]);
        Assert.Equal(targetId.ToString("D"), text.AdditionalProperties["targetId"]);
    }

    [Fact]
    public void CreateUserChatMessage_TextAndUri_PreservesSupportedContentProperties()
    {
        var input = new AgwUserInput
        {
            MessageId = "message-1",
            Contents =
            [
                new AgwTextContent
                {
                    Content = "inspect",
                    AdditionalProperties = new AdditionalPropertiesDictionary { ["text"] = "metadata" },
                },
                new AgwUriContent(new Uri("https://example.com/file"), "text/plain")
                {
                    AdditionalProperties = new AdditionalPropertiesDictionary { ["uri"] = "metadata" },
                },
            ],
        };

        var message = AgwMessageUtil.CreateUserChatMessage(input);

        Assert.Equal("message-1", message.MessageId);
        Assert.Equal(Constants.DefaultInputAuthor, message.AuthorName);
        Assert.Equal("metadata", Assert.IsType<TextContent>(message.Contents[0]).AdditionalProperties!["text"]);
        Assert.Equal("metadata", Assert.IsType<UriContent>(message.Contents[1]).AdditionalProperties!["uri"]);
    }

    [Fact]
    public void CreateUserChatMessage_ImageAndText_PreservesOrderBytesAndProperties()
    {
        var input = new AgwUserInput
        {
            MessageId = "message-1",
            Contents =
            [
                new AgwDataContent(new byte[] { 1, 2, 3 }, "image/png")
                {
                    Name = "screen.png",
                    AdditionalProperties = new AdditionalPropertiesDictionary { ["image"] = "metadata" },
                },
                new AgwTextContent { Content = "describe this" },
            ],
        };

        var message = AgwMessageUtil.CreateUserChatMessage(input);

        var image = Assert.IsType<DataContent>(message.Contents[0]);
        Assert.Equal(new byte[] { 1, 2, 3 }, image.Data.ToArray());
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal("screen.png", image.Name);
        Assert.Equal("metadata", image.AdditionalProperties!["image"]);
        Assert.Equal("describe this", Assert.IsType<TextContent>(message.Contents[1]).Text);
    }

    [Fact]
    public void CreateUserChatMessage_MoreThanFiveImages_ThrowsInvalidParam()
    {
        var input = new AgwUserInput
        {
            Contents = Enumerable
                .Range(0, 6)
                .Select(index =>
                    (AgwContent)new AgwDataContent(new byte[] { 1 }, "image/png") { Name = $"image-{index}.png" }
                )
                .ToList(),
        };

        var exception = Assert.Throws<AgwException>(() => AgwMessageUtil.CreateUserChatMessage(input));

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Contains("up to 5 images", exception.Message);
    }

    [Fact]
    public void CreateUserChatMessage_NonImageDataContent_ThrowsInvalidParam()
    {
        var input = new AgwUserInput
        {
            Contents = [new AgwDataContent(new byte[] { 1 }, "application/pdf") { Name = "file.pdf" }],
        };

        var exception = Assert.Throws<AgwException>(() => AgwMessageUtil.CreateUserChatMessage(input));

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Contains("Unsupported image type", exception.Message);
    }

    [Fact]
    public void CreateUserChatMessage_ImageOverFiveMegabytes_ThrowsInvalidParam()
    {
        var input = new AgwUserInput
        {
            Contents = [new AgwDataContent(new byte[5 * 1024 * 1024 + 1], "image/png") { Name = "large.png" }],
        };

        var exception = Assert.Throws<AgwException>(() => AgwMessageUtil.CreateUserChatMessage(input));

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Contains("large.png exceeds the 5 MB limit", exception.Message);
    }

    [Fact]
    public void CreateUserChatMessage_ImagesOverTenMegabytesTotal_ThrowsInvalidParam()
    {
        var imageBytes = new byte[4 * 1024 * 1024];
        var input = new AgwUserInput
        {
            Contents =
            [
                new AgwDataContent(imageBytes, "image/png"),
                new AgwDataContent(imageBytes, "image/png"),
                new AgwDataContent(imageBytes, "image/png"),
            ],
        };

        var exception = Assert.Throws<AgwException>(() => AgwMessageUtil.CreateUserChatMessage(input));

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Contains("total up to 10 MB", exception.Message);
    }
}
