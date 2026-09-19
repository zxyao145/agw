using System.Net;
using System.Text;
using System.Text.Json;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Providers.Contracts;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public sealed class AnthropicResponseSchemaTests
{
    [Theory]
    [InlineData("{\"type\":\"object\"}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}}}")]
    [InlineData("{\"type\":\"object\",\"required\":[]}")]
    [InlineData("{\"properties\":{},\"required\":[]}")]
    [InlineData("{\"type\":\"array\",\"items\":{\"type\":\"string\"}}")]
    public void Create_AnthropicSchemaThatWouldLoseOutputFormat_FailsExplicitly(string schema)
    {
        // Arrange
        var agent = new Agent { Name = "test", ResponseSchema = schema };

        // Act
        var exception = Assert.Throws<AgwException>(() =>
            AgentResponseSchemaFormat.Create(agent, ProviderType.Anthropic)
        );

        // Assert
        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Contains("Anthropic", exception.Message);
    }

    [Theory]
    [InlineData(ProviderType.OpenAIChatCompletions)]
    [InlineData(ProviderType.OpenAIResponses)]
    public void Create_OtherProvider_DoesNotApplyAnthropicRestrictions(ProviderType providerType)
    {
        // Arrange
        var agent = new Agent { Name = "test", ResponseSchema = "{\"type\":\"object\"}" };

        // Act
        var format = AgentResponseSchemaFormat.Create(agent, providerType);

        // Assert
        Assert.IsType<ChatResponseFormatJson>(format);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetResponse_OptionalProperties_SendsNativeOutputFormat(bool streaming)
    {
        // Arrange
        var agent = new Agent
        {
            Name = "test",
            ResponseSchema = """{"type":"object","properties":{"answer":{"type":"string"}},"required":[]}""",
        };
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        using var client = new AnthropicClient(
            new ClientOptions
            {
                ApiKey = "test-key",
                BaseUrl = "https://anthropic.invalid",
                HttpClient = httpClient,
            }
        ).AsIChatClient("test-model");
        var options = new ChatOptions
        {
            ResponseFormat = AgentResponseSchemaFormat.Create(agent, ProviderType.Anthropic),
        };

        // Act
        if (streaming)
        {
            await foreach (
                var _ in client.GetStreamingResponseAsync(
                    [new ChatMessage(ChatRole.User, "test")],
                    options,
                    TestContext.Current.CancellationToken
                )
            ) { }
        }
        else
        {
            await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "test")],
                options,
                TestContext.Current.CancellationToken
            );
        }

        // Assert
        using var request = JsonDocument.Parse(Assert.IsType<string>(handler.RequestBody));
        var format = request.RootElement.GetProperty("output_config").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        var schema = format.GetProperty("schema");
        Assert.Equal("string", schema.GetProperty("properties").GetProperty("answer").GetProperty("type").GetString());
        Assert.Empty(schema.GetProperty("required").EnumerateArray());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(RequestBody);
            var streaming = document.RootElement.TryGetProperty("stream", out var stream) && stream.GetBoolean();
            const string message =
                """{"id":"msg-1","type":"message","role":"assistant","model":"test-model","content":[],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}""";
            var body = streaming
                ? $"event: message_start\ndata: {{\"type\":\"message_start\",\"message\":{message}}}\n\nevent: message_stop\ndata: {{\"type\":\"message_stop\"}}\n\n"
                : message;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, streaming ? "text/event-stream" : "application/json"),
            };
        }
    }
}
