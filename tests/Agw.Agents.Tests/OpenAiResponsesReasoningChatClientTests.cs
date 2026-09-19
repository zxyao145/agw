using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Agw.Agents.Execution.Agents.Middleware;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;

#pragma warning disable OPENAI001

namespace Agw.Agents.Tests;

public sealed class OpenAiResponsesReasoningChatClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetResponse_DeepSeekReasoningContent_RoundTripsThroughPortableHistory(bool streaming)
    {
        // Arrange: DeepSeek returns reasoning.content, not an OpenAI reasoning summary.
        await using var fixture = new ClientFixture("https://api.deepseek.com");
        var response = await SendAsync(fixture.Client, [new(ChatRole.User, "question")], streaming);
        var original = JsonSerializer.Serialize(response.Messages);
        var replay = JsonSerializer.Deserialize<List<ChatMessage>>(original)!;
        replay.Add(new(ChatRole.Tool, [new FunctionResultContent("call-1", "result")]));

        // Act
        await SendAsync(fixture.Client, replay, streaming);

        // Assert: the real SDK request retains readable reasoning, IDs and tool pairing.
        var reasoning = Assert.Single(
            response.Messages.SelectMany(message => message.Contents).OfType<TextReasoningContent>()
        );
        Assert.Equal("original reasoning", reasoning.Text);
        var inputs = fixture.Handler.Requests[1].GetProperty("input").EnumerateArray().ToList();
        var item = Assert.Single(inputs, item => item.GetProperty("type").GetString() == "reasoning");
        Assert.Equal("reason-1", item.GetProperty("id").GetString());
        Assert.Equal("reasoning_text", item.GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("original reasoning", item.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.False(item.TryGetProperty("encrypted_content", out _));
        Assert.Equal(
            "call-1",
            Assert
                .Single(inputs, item => item.GetProperty("type").GetString() == "function_call")
                .GetProperty("call_id")
                .GetString()
        );
        Assert.Equal(
            "call-1",
            Assert
                .Single(inputs, item => item.GetProperty("type").GetString() == "function_call_output")
                .GetProperty("call_id")
                .GetString()
        );
        Assert.Equal(original, JsonSerializer.Serialize(response.Messages));
    }

    [Theory]
    [InlineData("https://api.deepseek.com", false)]
    [InlineData("https://api.openai.com/v1", true)]
    [InlineData("https://gateway.example.test/v1", true)]
    [InlineData("https://api.deepseek.com.example.test/v1", true)]
    public async Task GetResponse_ProtectedHistory_UsesDestinationProtocolWithoutMutatingHistory(
        string endpoint,
        bool supportsEncryptedContent
    )
    {
        // Arrange: opaque data is provider-specific; DeepSeek only accepts plain reasoning content.
        await using var fixture = new ClientFixture(endpoint);
        var message = new ChatMessage(
            ChatRole.Assistant,
            [new TextReasoningContent("") { ProtectedData = "opaque-data" }, new TextContent("answer")]
        );
        var original = JsonSerializer.Serialize(message);

        // Act
        await SendAsync(fixture.Client, [message], false);

        // Assert
        var item = Assert.Single(
            Assert.Single(fixture.Handler.Requests).GetProperty("input").EnumerateArray(),
            item => item.GetProperty("type").GetString() == "reasoning"
        );
        Assert.Equal(supportsEncryptedContent, item.TryGetProperty("encrypted_content", out var encrypted));
        if (supportsEncryptedContent)
            Assert.Equal("opaque-data", encrypted.GetString());
        else
            Assert.Equal("", item.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(original, JsonSerializer.Serialize(message));
    }

    private static Task<ChatResponse> SendAsync(IChatClient client, IEnumerable<ChatMessage> messages, bool streaming)
    {
        var token = TestContext.Current.CancellationToken;
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "result", "lookup")] };
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
            services.AddHttpClient("responses-test").ConfigurePrimaryHttpMessageHandler(() => Handler);
            _services = services.BuildServiceProvider();
            var native = new OpenAIClient(
                new ApiKeyCredential("test-key"),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri(endpoint),
                    Transport = new HttpClientPipelineTransport(
                        _services.GetRequiredService<IHttpClientFactory>().CreateClient("responses-test")
                    ),
                }
            ).GetResponsesClient();
            Client = OpenAiResponsesReasoningChatClient.Create(
                native.AsIChatClient("thinking-model"),
                new Uri(endpoint)
            );
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

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var payload = JsonSerializer.Deserialize<JsonElement>(
                await request.Content!.ReadAsStringAsync(cancellationToken)
            );
            Requests.Add(payload);
            const string reasoning =
                """{"type":"reasoning","id":"reason-1","summary":[],"content":[{"type":"reasoning_text","text":"original reasoning"}]}""";
            const string function =
                """{"type":"function_call","id":"fc-1","call_id":"call-1","name":"lookup","arguments":"{}","status":"completed"}""";
            var response =
                $$$"""{"id":"resp-{{{Requests.Count}}}","object":"response","created_at":1,"status":"completed","model":"thinking-model","store":false,"output":[{{{reasoning}}},{{{function}}}],"usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}""";
            var streaming = payload.TryGetProperty("stream", out var stream) && stream.GetBoolean();
            if (streaming)
            {
                var events = new StringBuilder();
                var sequence = 0;
                Add("response.created", new { response = JsonSerializer.Deserialize<JsonElement>(response) });
                Add(
                    "response.output_item.added",
                    new
                    {
                        output_index = 0,
                        item = JsonSerializer.Deserialize<JsonElement>(
                            """{"type":"reasoning","id":"reason-1","summary":[],"content":[]}"""
                        ),
                    }
                );
                Add(
                    "response.reasoning_text.delta",
                    new
                    {
                        item_id = "reason-1",
                        output_index = 0,
                        content_index = 0,
                        delta = "original reasoning",
                    }
                );
                Add(
                    "response.output_item.done",
                    new { output_index = 0, item = JsonSerializer.Deserialize<JsonElement>(reasoning) }
                );
                Add(
                    "response.output_item.added",
                    new { output_index = 1, item = JsonSerializer.Deserialize<JsonElement>(function) }
                );
                Add(
                    "response.output_item.done",
                    new { output_index = 1, item = JsonSerializer.Deserialize<JsonElement>(function) }
                );
                Add("response.completed", new { response = JsonSerializer.Deserialize<JsonElement>(response) });
                response = events.ToString();

                void Add(string type, object data)
                {
                    var value = JsonSerializer.SerializeToNode(data)!;
                    value["type"] = type;
                    value["sequence_number"] = sequence++;
                    events.Append($"event: {type}\ndata: {value.ToJsonString()}\n\n");
                }
            }
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    response,
                    Encoding.UTF8,
                    streaming ? "text/event-stream" : "application/json"
                ),
            };
        }
    }
}
