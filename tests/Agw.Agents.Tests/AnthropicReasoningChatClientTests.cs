using System.Net;
using System.Text;
using System.Text.Json;
using Agw.Agents.Execution.Agents.Middleware;
using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Tests;

public sealed class AnthropicReasoningChatClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetResponse_DeepSeekHistoryWithPlainAnswer_PreservesThinkingContract(bool streaming)
    {
        // Arrange: older answers can predate thinking mode or originate from another model.
        await using var fixture = new ClientFixture("https://api.deepseek.com/anthropic");
        fixture.Handler.RequireThinking = true;
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "previous question"),
            new(ChatRole.Assistant, "previous answer"),
            new(ChatRole.User, "continue"),
            new(
                ChatRole.Assistant,
                [
                    new TextReasoningContent("original reasoning") { ProtectedData = "original-signature" },
                    new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?>()),
                ]
            ),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "result")]),
        };
        var original = JsonSerializer.Serialize(messages);

        // Act
        await SendAsync(fixture.Client, messages, streaming);

        // Assert: inspect the actual SDK HTTP payload, including the unmodified original block.
        var inputs = Assert.Single(fixture.Handler.Requests).GetProperty("messages");
        Assert.Equal("", inputs[1].GetProperty("content")[0].GetProperty("thinking").GetString());
        Assert.Equal("previous answer", inputs[1].GetProperty("content")[1].GetProperty("text").GetString());
        Assert.Equal("original reasoning", inputs[3].GetProperty("content")[0].GetProperty("thinking").GetString());
        Assert.Equal("original-signature", inputs[3].GetProperty("content")[0].GetProperty("signature").GetString());
        Assert.Equal(original, JsonSerializer.Serialize(messages));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetResponse_EmptyRecordedThinking_RoundTripsAfterSerialization(bool streaming)
    {
        // Arrange
        await using var fixture = new ClientFixture("https://api.deepseek.com/anthropic");
        fixture.Handler.RequireThinking = true;
        var messages = JsonSerializer.Deserialize<List<ChatMessage>>(
            JsonSerializer.Serialize(
                new ChatMessage[] { new(ChatRole.Assistant, [new TextReasoningContent(""), new TextContent("answer")]) }
            )
        )!;

        // Act
        await SendAsync(fixture.Client, messages, streaming);

        // Assert
        var content = Assert.Single(fixture.Handler.Requests).GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(
            new[] { "thinking", "text" },
            content.EnumerateArray().Select(block => block.GetProperty("type").GetString())
        );
        Assert.Equal("", content[0].GetProperty("thinking").GetString());
    }

    [Theory]
    [InlineData("https://api.anthropic.com")]
    [InlineData("https://gateway.example.test/anthropic")]
    [InlineData("https://api.deepseek.com.example.test/anthropic")]
    public async Task GetResponse_OtherEndpoints_DoesNotSynthesizeThinking(string endpoint)
    {
        // Arrange
        await using var fixture = new ClientFixture(endpoint);

        // Act
        await SendAsync(fixture.Client, [new(ChatRole.Assistant, "answer")], false);

        // Assert
        var content = Assert.Single(fixture.Handler.Requests).GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("text", Assert.Single(content.EnumerateArray()).GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetResponse_ToolResponse_RoundTripsThinkingAndSignatureAfterSerialization(bool streaming)
    {
        // Arrange
        await using var fixture = new ClientFixture("https://api.deepseek.com/anthropic");
        fixture.Handler.RequireThinking = true;
        fixture.Handler.ReturnToolCall = true;

        // Act
        var response = await SendAsync(fixture.Client, [new(ChatRole.User, "question")], streaming);
        var messages = JsonSerializer.Deserialize<List<ChatMessage>>(JsonSerializer.Serialize(response.Messages))!;
        messages.Insert(0, new(ChatRole.User, "question"));
        messages.Add(new(ChatRole.Tool, [new FunctionResultContent("call-1", "result")]));
        await SendAsync(fixture.Client, messages, streaming);

        // Assert
        var content = fixture.Handler.Requests[1].GetProperty("messages")[1].GetProperty("content");
        Assert.Equal("original reasoning", content[0].GetProperty("thinking").GetString());
        Assert.Equal("original-signature", content[0].GetProperty("signature").GetString());
        Assert.Equal("call-1", content[1].GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("https://api.deepseek.com/anthropic", false)]
    [InlineData("https://api.deepseek.com/anthropic", true)]
    [InlineData("https://api.anthropic.com", false)]
    [InlineData("https://api.anthropic.com", true)]
    public async Task GetResponse_EmptySignedThinking_PreservesBlockTypeBeforeAndAfterSerialization(
        string endpoint,
        bool streaming
    )
    {
        // Arrange: a valid ordinary thinking block can contain only a signature.
        await using var fixture = new ClientFixture(endpoint);
        fixture.Handler.ReturnToolCall = true;
        fixture.Handler.ThinkingText = "";
        fixture.Handler.RequireThinking = endpoint.Contains("deepseek.com", StringComparison.Ordinal);
        var response = await SendAsync(fixture.Client, [new(ChatRole.User, "question")], streaming);
        var original = JsonSerializer.Serialize(response.Messages);

        // Act: exercise both in-memory tool continuation and persisted history replay.
        foreach (
            var messages in new[]
            {
                response.Messages.ToList(),
                JsonSerializer.Deserialize<List<ChatMessage>>(original)!,
            }
        )
        {
            messages.Add(new(ChatRole.Tool, [new FunctionResultContent("call-1", "result")]));
            await SendAsync(fixture.Client, messages, streaming);
        }

        // Assert
        foreach (var request in fixture.Handler.Requests.Skip(1))
        {
            var content = request.GetProperty("messages")[0].GetProperty("content");
            Assert.Equal("thinking", content[0].GetProperty("type").GetString());
            Assert.Equal("", content[0].GetProperty("thinking").GetString());
            Assert.Equal("original-signature", content[0].GetProperty("signature").GetString());
            Assert.Equal("call-1", content[1].GetProperty("id").GetString());
        }
        Assert.Equal(original, JsonSerializer.Serialize(response.Messages));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetResponse_DeepSeekLegacySignatureOnlyHistory_DoesNotInventRedactedThinking(bool streaming)
    {
        // Arrange: older histories have no native block type after JSON serialization.
        await using var fixture = new ClientFixture("https://api.deepseek.com/anthropic");
        var message = new ChatMessage(
            ChatRole.Assistant,
            [new TextReasoningContent("") { ProtectedData = "original-signature" }, new TextContent("answer")]
        );
        var original = JsonSerializer.Serialize(message);

        // Act
        await SendAsync(fixture.Client, [message], streaming);

        // Assert
        var content = Assert.Single(fixture.Handler.Requests).GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("thinking", content[0].GetProperty("type").GetString());
        Assert.Equal("", content[0].GetProperty("thinking").GetString());
        Assert.Equal("original-signature", content[0].GetProperty("signature").GetString());
        Assert.Equal(original, JsonSerializer.Serialize(message));
    }

    [Theory]
    [InlineData("https://api.anthropic.com", false)]
    [InlineData("https://api.anthropic.com", true)]
    [InlineData("https://api.deepseek.com/anthropic", false)]
    [InlineData("https://api.deepseek.com/anthropic", true)]
    public async Task GetResponse_ExplicitRedactedThinking_PreservesOpaqueDataAfterSerialization(
        string endpoint,
        bool streaming
    )
    {
        // Arrange: real encrypted content must never be reinterpreted as a thinking signature.
        await using var fixture = new ClientFixture(endpoint);
        fixture.Handler.ReturnToolCall = true;
        fixture.Handler.ReturnRedactedThinking = true;
        var response = await SendAsync(fixture.Client, [new(ChatRole.User, "question")], streaming);
        var messages = JsonSerializer.Deserialize<List<ChatMessage>>(JsonSerializer.Serialize(response.Messages))!;
        messages.Add(new(ChatRole.Tool, [new FunctionResultContent("call-1", "result")]));

        // Act
        await SendAsync(fixture.Client, messages, streaming);

        // Assert
        var content = fixture.Handler.Requests[1].GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("redacted_thinking", content[0].GetProperty("type").GetString());
        Assert.Equal("encrypted-data", content[0].GetProperty("data").GetString());
        Assert.Equal("call-1", content[1].GetProperty("id").GetString());
    }

    private static Task<ChatResponse> SendAsync(IChatClient client, IEnumerable<ChatMessage> messages, bool streaming)
    {
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "result", "lookup")] };
        var token = TestContext.Current.CancellationToken;
        return streaming
            ? client.GetStreamingResponseAsync(messages, options, token).ToChatResponseAsync(cancellationToken: token)
            : client.GetResponseAsync(messages, options, token);
    }

    internal sealed class ClientFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        public RecordingHandler Handler { get; } = new();
        public IChatClient Client { get; }

        public ClientFixture(string endpoint)
        {
            var services = new ServiceCollection();
            services.AddHttpClient("anthropic-test").ConfigurePrimaryHttpMessageHandler(() => Handler);
            _services = services.BuildServiceProvider();
            var client = new AnthropicClient(
                new ClientOptions
                {
                    ApiKey = "test-key",
                    BaseUrl = endpoint,
                    HttpClient = _services.GetRequiredService<IHttpClientFactory>().CreateClient("anthropic-test"),
                }
            );
            Client = AnthropicReasoningChatClient.Create(client.AsIChatClient("thinking-model"), new Uri(endpoint));
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _services.DisposeAsync();
        }
    }

    internal sealed class RecordingHandler : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];
        public bool RequireThinking { get; set; }
        public bool ReturnToolCall { get; set; }
        public string ThinkingText { get; set; } = "original reasoning";
        public bool ReturnRedactedThinking { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var payload = JsonSerializer.Deserialize<JsonElement>(
                await request.Content!.ReadAsStringAsync(cancellationToken)
            );
            Requests.Add(payload);
            var inputs = payload.GetProperty("messages").EnumerateArray().ToList();
            if (
                RequireThinking
                && inputs
                    .SelectMany(message => message.GetProperty("content").EnumerateArray())
                    .Any(block => block.GetProperty("type").GetString() == "redacted_thinking")
            )
            {
                return new(HttpStatusCode.UnprocessableEntity)
                {
                    Content = new StringContent(
                        """{"error":{"message":"unknown variant `redacted_thinking`","type":"invalid_request_error"}}""",
                        Encoding.UTF8,
                        "application/json"
                    ),
                };
            }
            if (
                RequireThinking
                && inputs.Any(message =>
                    message.GetProperty("role").GetString() == "assistant"
                    && !message
                        .GetProperty("content")
                        .EnumerateArray()
                        .Any(block => block.GetProperty("type").GetString() == "thinking")
                )
            )
            {
                return new(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        """{"error":{"message":"The content[].thinking in the thinking mode must be passed back to the API.","type":"invalid_request_error"}}""",
                        Encoding.UTF8,
                        "application/json"
                    ),
                };
            }

            var toolCall =
                ReturnToolCall
                && !inputs.Any(message =>
                    message
                        .GetProperty("content")
                        .EnumerateArray()
                        .Any(block => block.GetProperty("type").GetString() == "tool_result")
                );
            var thinking = ReturnRedactedThinking
                ? """{"type":"redacted_thinking","data":"encrypted-data"}"""
                : JsonSerializer.Serialize(
                    new
                    {
                        type = "thinking",
                        thinking = ThinkingText,
                        signature = "original-signature",
                    }
                );
            const string tool = """{"type":"tool_use","id":"call-1","name":"lookup","input":{}}""";
            const string text = """{"type":"text","text":"finished"}""";
            var content = toolCall ? $"{thinking},{tool}" : text;
            var stopReason = toolCall ? "tool_use" : "end_turn";
            var message =
                $$$"""{"id":"msg-{{{Requests.Count}}}","type":"message","role":"assistant","model":"thinking-model","content":[{{{content}}}],"stop_reason":"{{{stopReason}}}","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}""";
            var streaming = payload.TryGetProperty("stream", out var stream) && stream.GetBoolean();
            if (streaming)
            {
                var start = JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(message)!;
                start["content"] = new System.Text.Json.Nodes.JsonArray();
                var events = new StringBuilder(
                    $"event: message_start\ndata: {{\"type\":\"message_start\",\"message\":{start.ToJsonString()}}}\n\n"
                );
                if (toolCall)
                {
                    var startBlock = ReturnRedactedThinking
                        ? thinking
                        : """{"type":"thinking","thinking":"","signature":""}""";
                    events.Append(
                        $"event: content_block_start\ndata: {{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{startBlock}}}\n\n"
                    );
                    if (!ReturnRedactedThinking)
                    {
                        if (ThinkingText.Length > 0)
                        {
                            var delta = JsonSerializer.Serialize(
                                new { type = "thinking_delta", thinking = ThinkingText }
                            );
                            events.Append(
                                $"event: content_block_delta\ndata: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{delta}}}\n\n"
                            );
                        }
                        events.Append(
                            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"original-signature\"}}\n\n"
                        );
                    }
                    events.Append("event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n");
                }
                var index = toolCall ? 1 : 0;
                events.Append(
                    $"event: content_block_start\ndata: {{\"type\":\"content_block_start\",\"index\":{index},\"content_block\":{(toolCall ? tool : text)}}}\n\n"
                );
                events.Append(
                    $"event: content_block_stop\ndata: {{\"type\":\"content_block_stop\",\"index\":{index}}}\n\n"
                );
                events.Append(
                    $"event: message_delta\ndata: {{\"type\":\"message_delta\",\"delta\":{{\"stop_reason\":\"{stopReason}\",\"stop_sequence\":null}},\"usage\":{{\"output_tokens\":1}}}}\n\n"
                );
                events.Append("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
                message = events.ToString();
            }
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    message,
                    Encoding.UTF8,
                    streaming ? "text/event-stream" : "application/json"
                ),
            };
        }
    }
}
