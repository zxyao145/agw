using System.Text.Json;
using Agw.Agents.Execution.Agents.ExternalAgents.Acp;
using Agw.Agents.Execution.Agents.ExternalAgents.Acp.Protocol;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests.Acp;

public sealed class AcpPromptBuilderTests
{
    [Fact]
    public void Build_TextAndSystem_KeepsOrder()
    {
        var prompt = AcpPromptBuilder.Build(
            [new(ChatRole.User, "context"), new(ChatRole.System, "system"), new(ChatRole.User, "prompt")],
            null
        );

        Assert.Equal(
            [new AcpTextContent("context"), new AcpTextContent("system"), new AcpTextContent("prompt")],
            prompt
        );
    }

    [Theory]
    [InlineData("image/png", "image")]
    [InlineData("audio/wav", "audio")]
    [InlineData("application/pdf", "embedded")]
    public void Build_DataWithoutCapability_ThrowsInvalidParam(string mediaType, string capability)
    {
        var exception = Assert.Throws<AgwException>(() =>
            AcpPromptBuilder.Build([new(ChatRole.User, [new DataContent(new byte[] { 1 }, mediaType)])], null)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Equal($"This ACP agent does not accept {capability} content.", exception.Message);
    }

    [Fact]
    public void Build_ImageAndAudioWithCapabilities_EncodesBase64()
    {
        byte[] bytes = [1, 2, 3];
        var prompt = AcpPromptBuilder.Build(
            [new(ChatRole.User, [new DataContent(bytes, "image/png"), new DataContent(bytes, "audio/wav")])],
            new AcpPromptCapabilities(Image: true, Audio: true)
        );

        Assert.Equal([new AcpImageContent("AQID", "image/png"), new AcpAudioContent("AQID", "audio/wav")], prompt);
    }

    [Theory]
    [InlineData("a.pdf", "agw:attachment/a.pdf")]
    [InlineData("a b.pdf", "agw:attachment/a%20b.pdf")]
    [InlineData(" ", "agw:attachment/%20")]
    [InlineData("résumé #1/100%.pdf", "agw:attachment/r%C3%A9sum%C3%A9%20%231%2F100%25.pdf")]
    [InlineData("a%20b.pdf", "agw:attachment/a%2520b.pdf")]
    [InlineData("", "agw:attachment/1")]
    [InlineData(null, "agw:attachment/1")]
    public void Build_OtherData_UsesNamedResourceUri(string? name, string uri)
    {
        var data = new DataContent(new byte[] { 1, 2, 3 }, "application/pdf") { Name = name };
        var prompt = AcpPromptBuilder.Build(
            [new(ChatRole.User, [data])],
            new AcpPromptCapabilities(EmbeddedContext: true)
        );

        var block = JsonSerializer.SerializeToElement(Assert.Single(prompt), AcpJson.Options);
        Assert.Equal("resource", block.GetProperty("type").GetString());
        var resource = block.GetProperty("resource");
        var wireUri = resource.GetProperty("uri").GetString()!;
        Assert.Equal(uri, wireUri);
        var parsedUri = new Uri(wireUri, UriKind.Absolute);
        Assert.Empty(parsedUri.Query);
        Assert.Empty(parsedUri.Fragment);
        Assert.Equal(string.IsNullOrEmpty(name) ? "1" : name, Uri.UnescapeDataString(parsedUri.Segments[^1]));
        Assert.Equal("AQID", resource.GetProperty("blob").GetString());
        Assert.Equal("application/pdf", resource.GetProperty("mimeType").GetString());
    }

    [Fact]
    public void Build_UriContent_ProducesResourceLink()
    {
        const string uri = "https://example.com/files/report%20one.pdf?download=1";
        var prompt = AcpPromptBuilder.Build([new(ChatRole.User, [new UriContent(uri)])], null);

        Assert.Equal(new AcpResourceLink(uri, "report one.pdf"), Assert.Single(prompt));
    }

    [Fact]
    public void Build_NothingConvertible_ThrowsInvalidParam()
    {
        var exception = Assert.Throws<AgwException>(() =>
            AcpPromptBuilder.Build([new(ChatRole.Assistant, [new TextReasoningContent("thinking")])], null)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }
}
