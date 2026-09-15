using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Agw.Agents.Execution.Agents.Middleware;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

#pragma warning disable SCME0001 // OpenAI's custom JSON fields use JsonPatch.

namespace Agw.Agents.Tests;

public sealed class OpenAiReasoningChatClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeepSeek_ToolContinuationAfterPlainAnswer_IncludesReasoningOnEveryAssistant(bool streaming)
    {
        // Arrange: the failing conversation contains an older plain answer before a new thinking tool call.
        await using var fixture = new ClientFixture("https://api.deepseek.com/v1");
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "previous request"),
            new(ChatRole.Assistant, "previous answer without reasoning"),
            new(ChatRole.User, "continue"),
            new(
                ChatRole.Assistant,
                [
                    new TextReasoningContent("current reasoning"),
                    new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?>()),
                ]
            ),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "tool result")]),
        };
        var original = JsonSerializer.Serialize(history);
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "result", "lookup")] };

        // Act
        if (streaming)
            await fixture
                .Client.GetStreamingResponseAsync(history, options, TestContext.Current.CancellationToken)
                .ToChatResponseAsync(cancellationToken: TestContext.Current.CancellationToken);
        else
            await fixture.Client.GetResponseAsync(history, options, TestContext.Current.CancellationToken);

        // Assert
        var messages = Assert.Single(fixture.Handler.Requests).GetProperty("messages");
        Assert.True(messages[1].TryGetProperty("reasoning_content", out var previousReasoning));
        Assert.Equal(string.Empty, previousReasoning.GetString());
        Assert.Equal("current reasoning", messages[3].GetProperty("reasoning_content").GetString());
        Assert.False(messages[0].TryGetProperty("reasoning_content", out _));
        Assert.False(messages[4].TryGetProperty("reasoning_content", out _));
        Assert.Equal(original, JsonSerializer.Serialize(history));
    }

    [Theory]
    [InlineData("https://api.deepseek.com/v1", true)]
    [InlineData("https://gateway.example.test/v1", false)]
    [InlineData("https://api.openai.com/v1", false)]
    public async Task PlainAssistantHistory_AddsEmptyReasoningOnlyForDeepSeek(string endpoint, bool expected)
    {
        await using var fixture = new ClientFixture(endpoint);
        await fixture.Client.GetResponseAsync(
            [new(ChatRole.User, "request"), new(ChatRole.Assistant, "plain answer")],
            cancellationToken: TestContext.Current.CancellationToken
        );
        var assistant = Assert.Single(fixture.Handler.Requests).GetProperty("messages")[1];
        Assert.Equal(expected, assistant.TryGetProperty("reasoning_content", out var reasoning));
        if (expected)
            Assert.Equal(string.Empty, reasoning.GetString());
    }

    [Fact]
    public async Task DeepSeek_NativeReasoning_PreservesOriginalField()
    {
        await using var fixture = new ClientFixture("https://api.deepseek.com/v1");
        var native = new AssistantChatMessage("answer");
        native.Patch.Set("$.reasoning_content"u8, "native reasoning");
        await fixture.Client.GetResponseAsync(
            [new(ChatRole.Assistant, "answer") { RawRepresentation = native }],
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Equal(
            "native reasoning",
            Assert
                .Single(fixture.Handler.Requests)
                .GetProperty("messages")[0]
                .GetProperty("reasoning_content")
                .GetString()
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolContinuation_SeparateReasoningMessage_ReturnsReasoningOnToolCall(bool streaming)
    {
        // Arrange
        await using var fixture = new ClientFixture("https://gateway.example.test/v1");
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "start"),
            new(ChatRole.Assistant, [new TextReasoningContent("checking "), new TextContent("")])
            {
                AuthorName = "agent",
                MessageId = "reasoning-1",
            },
            new(ChatRole.Assistant, [new TextReasoningContent("the repository")])
            {
                AuthorName = "agent",
                MessageId = "reasoning-2",
            },
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?>())])
            {
                AuthorName = "agent",
                MessageId = "tool-call",
            },
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "tool result")]),
        };
        var original = JsonSerializer.Serialize(history);
        var options = new ChatOptions { Instructions = "system instructions" };

        // Act
        if (streaming)
            await fixture
                .Client.GetStreamingResponseAsync(history, options, TestContext.Current.CancellationToken)
                .ToChatResponseAsync(cancellationToken: TestContext.Current.CancellationToken);
        else
            await fixture.Client.GetResponseAsync(history, options, TestContext.Current.CancellationToken);

        // Assert
        var messages = Assert.Single(fixture.Handler.Requests).GetProperty("messages");
        var toolCall = Assert.Single(messages.EnumerateArray(), message => message.TryGetProperty("tool_calls", out _));
        Assert.True(toolCall.TryGetProperty("reasoning_content", out var reasoning));
        Assert.Equal("checking the repository", reasoning.GetString());
        Assert.Equal("call-1", toolCall.GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal(original, JsonSerializer.Serialize(history));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolContinuation_AfterSerialization_ReturnsReasoningInHttpPayload(bool streaming)
    {
        await using var fixture = new ClientFixture("https://gateway.example.test/v1");
        var client = fixture.Client;
        var token = TestContext.Current.CancellationToken;
        var response = streaming
            ? await client
                .GetStreamingResponseAsync([new(ChatRole.User, "start")], cancellationToken: token)
                .ToChatResponseAsync(cancellationToken: token)
            : await client.GetResponseAsync([new(ChatRole.User, "start")], cancellationToken: token);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        // Persistence/session reload removes the native SDK representation, but must retain reasoning.
        var history = JsonSerializer.Deserialize<List<ChatMessage>>(
            JsonSerializer.Serialize(response.Messages, jsonOptions),
            jsonOptions
        )!;
        Assert.Equal(
            "original reasoning",
            Assert.Single(history.Single().Contents.OfType<TextReasoningContent>()).Text
        );
        history.Insert(0, new(ChatRole.User, "start"));
        history.Add(
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "one"), new FunctionResultContent("call-2", "two")])
        );
        history.Add(
            new(
                ChatRole.Assistant,
                [
                    new TextReasoningContent("second "),
                    new TextReasoningContent("reasoning"),
                    new TextContent("checking"),
                    new FunctionCallContent("call-3", "lookup", new Dictionary<string, object?>()),
                ]
            )
        );
        history.Add(new(ChatRole.Tool, [new FunctionResultContent("call-3", "three")]));
        var options = new ChatOptions
        {
            Instructions = "system instructions",
            RawRepresentationFactory = _ =>
            {
                var native = new ChatCompletionOptions { Temperature = 0.25f };
                native.Patch.Set("$.vendor_setting"u8, "retained");
                return native;
            },
        };
        var originalFactory = options.RawRepresentationFactory;
        var originalHistory = JsonSerializer.Serialize(history, jsonOptions);

        if (streaming)
            await client
                .GetStreamingResponseAsync(history, options, token)
                .ToChatResponseAsync(cancellationToken: token);
        else
            await client.GetResponseAsync(history, options, token);

        var payload = fixture.Handler.Requests[1];
        var messages = payload.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(
            new[] { "system", "user", "assistant", "tool", "tool", "assistant", "tool" },
            messages.Select(message => message.GetProperty("role").GetString())
        );
        Assert.Equal("original reasoning", messages[2].GetProperty("reasoning_content").GetString());
        Assert.Equal("second reasoning", messages[5].GetProperty("reasoning_content").GetString());
        Assert.Equal(2, messages[2].GetProperty("tool_calls").GetArrayLength());
        Assert.Equal("call-3", messages[6].GetProperty("tool_call_id").GetString());
        Assert.Equal("checking", messages[5].GetProperty("content").GetString());
        Assert.Equal("retained", payload.GetProperty("vendor_setting").GetString());
        Assert.Equal(0.25, payload.GetProperty("temperature").GetDouble());
        Assert.Same(originalFactory, options.RawRepresentationFactory);
        Assert.Equal(originalHistory, JsonSerializer.Serialize(history, jsonOptions));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("system")]
    [InlineData("assistant")]
    [InlineData("other-author")]
    public async Task ToolContinuation_ReasoningPrefix_DoesNotCrossMessageBoundaries(string boundary)
    {
        // Arrange
        await using var fixture = new ClientFixture("https://gateway.example.test/v1");
        var history = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new TextReasoningContent("unrelated reasoning")]) { AuthorName = "agent" },
        };
        if (boundary != "other-author")
            history.Add(new(new ChatRole(boundary), "new message") { AuthorName = "agent" });
        history.Add(
            new(ChatRole.Assistant, [new FunctionCallContent("call", "lookup", new Dictionary<string, object?>())])
            {
                AuthorName = boundary == "other-author" ? "another-agent" : "agent",
            }
        );

        // Act
        await fixture.Client.GetResponseAsync(history, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var call = Assert.Single(
            Assert.Single(fixture.Handler.Requests).GetProperty("messages").EnumerateArray(),
            message => message.TryGetProperty("tool_calls", out _)
        );
        Assert.False(call.TryGetProperty("reasoning_content", out _));
    }

    [Fact]
    public async Task ToolContinuation_WithOwnReasoning_DoesNotOverwriteItWithPreviousReasoning()
    {
        // Arrange
        await using var fixture = new ClientFixture("https://gateway.example.test/v1");
        var history = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new TextReasoningContent("previous reasoning")]),
            new(
                ChatRole.Assistant,
                [
                    new TextReasoningContent("own reasoning"),
                    new FunctionCallContent("call", "lookup", new Dictionary<string, object?>()),
                ]
            ),
        };

        // Act
        await fixture.Client.GetResponseAsync(history, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var call = Assert.Single(
            Assert.Single(fixture.Handler.Requests).GetProperty("messages").EnumerateArray(),
            message => message.TryGetProperty("tool_calls", out _)
        );
        Assert.Equal("own reasoning", call.GetProperty("reasoning_content").GetString());
    }

    [Theory]
    [InlineData("https://api.deepseek.com/v1", true)]
    [InlineData("https://gateway.example.test/v1", true)]
    [InlineData("https://api.openai.com/v1", false)]
    [InlineData("https://deployment.openai.azure.com/", false)]
    public async Task PortableReasoning_UsesCompatibleEndpointProtocol(string endpoint, bool expectedReasoning)
    {
        await using var fixture = new ClientFixture(endpoint);
        await fixture.Client.GetResponseAsync(
            [
                new(ChatRole.User, "question"),
                new(ChatRole.Assistant, [new TextReasoningContent("portable reasoning"), new TextContent("answer")]),
            ],
            cancellationToken: TestContext.Current.CancellationToken
        );

        var messages = Assert.Single(fixture.Handler.Requests).GetProperty("messages");
        Assert.Equal(expectedReasoning, messages[1].TryGetProperty("reasoning_content", out _));
        Assert.False(messages[0].TryGetProperty("reasoning_content", out _));
        Assert.Equal("answer", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ConcurrentRequests_KeepReasoningIsolatedAndDoNotPatchLaterPlainRequests()
    {
        await using var fixture = new ClientFixture("https://gateway.example.test/v1");
        var token = TestContext.Current.CancellationToken;
        await Task.WhenAll(
            Enumerable
                .Range(0, 8)
                .Select(index =>
                    fixture.Client.GetResponseAsync(
                        [
                            new(ChatRole.User, $"request-{index}"),
                            new(
                                ChatRole.Assistant,
                                [new TextReasoningContent($"reasoning-{index}"), new TextContent("answer")]
                            ),
                        ],
                        new ChatOptions { Instructions = index % 2 == 0 ? "instructions" : null },
                        token
                    )
                )
        );
        Assert.Equal(8, fixture.Handler.Requests.Count);
        foreach (var request in fixture.Handler.Requests)
        {
            var messages = request.GetProperty("messages").EnumerateArray().ToList();
            var user = Assert.Single(messages, message => message.GetProperty("role").GetString() == "user");
            var assistant = Assert.Single(messages, message => message.GetProperty("role").GetString() == "assistant");
            Assert.Equal(
                user.GetProperty("content").GetString()!.Replace("request-", "reasoning-"),
                assistant.GetProperty("reasoning_content").GetString()
            );
        }

        await fixture.Client.GetResponseAsync([new(ChatRole.User, "plain")], cancellationToken: token);
        Assert.False(
            fixture.Handler.Requests[^1].GetProperty("messages")[0].TryGetProperty("reasoning_content", out _)
        );
    }

    [Fact]
    public async Task NativeMessagesAndOmittedRoles_DoNotShiftReasoningOntoAnotherMessage()
    {
        await using var fixture = new ClientFixture("https://gateway.example.test/v1");
        var native = new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent("ignored-1", "one"), new FunctionResultContent("ignored-2", "two")]
        )
        {
            RawRepresentation = new UserChatMessage("native message"),
        };
        await fixture.Client.GetResponseAsync(
            [
                new(new ChatRole("developer"), "developer instructions"),
                new(new ChatRole("omitted-role"), "ignored"),
                new(ChatRole.Tool, "display only"),
                native,
                new(ChatRole.Assistant, [new TextReasoningContent("original reasoning"), new TextContent("answer")]),
            ],
            cancellationToken: TestContext.Current.CancellationToken
        );

        var messages = Assert.Single(fixture.Handler.Requests).GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal("developer", messages[0].GetProperty("role").GetString());
        Assert.Equal("native message", messages[1].GetProperty("content").GetString());
        Assert.False(messages[1].TryGetProperty("reasoning_content", out _));
        Assert.Equal("original reasoning", messages[2].GetProperty("reasoning_content").GetString());
    }

    internal sealed class ClientFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        public RecordingHandler Handler { get; } = new();
        public IChatClient Client { get; }

        public ClientFixture(string endpoint)
        {
            var services = new ServiceCollection();
            services.AddHttpClient("reasoning-test").ConfigurePrimaryHttpMessageHandler(() => Handler);
            _services = services.BuildServiceProvider();
            var uri = new Uri(endpoint);
            var nativeClient = new OpenAIClient(
                new ApiKeyCredential("test-key"),
                new OpenAIClientOptions
                {
                    Endpoint = uri,
                    Transport = new HttpClientPipelineTransport(
                        _services.GetRequiredService<IHttpClientFactory>().CreateClient("reasoning-test")
                    ),
                }
            ).GetChatClient("thinking-model");
            Client = OpenAiReasoningChatClient.Create(nativeClient, uri);
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
        public bool CompleteAfterTools { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            await Task.Yield();
            var payload = JsonSerializer.Deserialize<JsonElement>(
                await request.Content!.ReadAsStringAsync(cancellationToken)
            );
            lock (Requests)
                Requests.Add(payload);
            var streaming = payload.TryGetProperty("stream", out var stream) && stream.GetBoolean();
            var json = $$$"""
                {"id":"response","object":"{{{(
                    streaming ? "chat.completion.chunk" : "chat.completion"
                )}}}","created":1,"model":"thinking-model","choices":[{
                    "index":0,"finish_reason":"tool_calls","{{{(streaming ? "delta" : "message")}}}":{
                        "role":"assistant","content":"done","reasoning_content":"original reasoning","tool_calls":[
                            {"index":0,"id":"call-1","type":"function","function":{"name":"lookup","arguments":"{}"}},
                            {"index":1,"id":"call-2","type":"function","function":{"name":"lookup","arguments":"{}"}}
                        ]
                    }
                }]}
                """;
            if (
                CompleteAfterTools
                && payload.GetProperty("messages").EnumerateArray().Last().GetProperty("role").GetString() == "tool"
            )
            {
                json = $$$"""
                    {"id":"final-response","object":"{{{(
                        streaming ? "chat.completion.chunk" : "chat.completion"
                    )}}}","created":2,"model":"thinking-model","choices":[{
                        "index":0,"finish_reason":"stop","{{{(streaming ? "delta" : "message")}}}":{
                            "role":"assistant","content":"finished","reasoning_content":"final reasoning"
                        }
                    }]}
                    """;
            }
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    streaming ? $"data: {json.Replace("\n", "").Replace("\r", "")}\n\ndata: [DONE]\n\n" : json,
                    Encoding.UTF8,
                    streaming ? "text/event-stream" : "application/json"
                ),
            };
        }
    }
}
