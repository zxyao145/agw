using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Agents.History;
using Agw.Agents.Execution.Agents.Middleware.Telemetry;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public sealed class ResponseSchemaResultMetadataTests
{
    [Theory]
    [InlineData("system", true)]
    [InlineData("claude-code", true)]
    [InlineData("codex", true)]
    [InlineData("pi", true)]
    [InlineData("system", false)]
    [InlineData("claude-code", false)]
    [InlineData("codex", false)]
    [InlineData("pi", false)]
    public async Task Run_WithSchema_MarksLiveAndPersistedResultsOnly(string author, bool streaming)
    {
        foreach (var configured in new[] { false, true })
        {
            var storage = new RecordingHistoryProvider();
            ChatHistoryProvider history = configured ? new ResponseSchemaChatHistoryProvider(storage) : storage;
            AIAgent agent = new CallbackAgent(history, author);
            if (configured)
            {
                agent = new AgentResponseSchemaExecutionAgent(agent, "test", author, "test");
            }

            var input = new ChatMessage(ChatRole.User, "Review");
            List<AgwMessage> output;
            if (streaming)
            {
                output = [];
                await foreach (
                    var update in agent.RunStreamingAsync(
                        [input],
                        cancellationToken: TestContext.Current.CancellationToken
                    )
                )
                {
                    output.Add(update.ToAiMessage()!);
                }
            }
            else
            {
                var response = await agent.RunAsync([input], cancellationToken: TestContext.Current.CancellationToken);
                output = response.Messages.Select(message => message.ToAiMessage()!).ToList();
            }

            Assert.Equal(2, output.Count);
            Assert.Null(output[0].AdditionalProperties?.GetValueOrDefault("resultFormat"));
            Assert.Equal(configured ? "json" : null, output[1].AdditionalProperties?.GetValueOrDefault("resultFormat"));
            Assert.Equal("success", output[1].AdditionalProperties!["subtype"]);
            Assert.Equal("{\"approved\":false}", Assert.IsType<AgwTextContent>(output[1].Contents[0]).Content);

            // Serialization happens inside the SDK history callback, before the outer execution wrapper sees output.
            using var persisted = JsonDocument.Parse(storage.Messages[1]);
            var properties = persisted.RootElement.GetProperty("additionalProperties");
            Assert.Equal(configured, properties.TryGetProperty("resultFormat", out var format));
            if (configured)
                Assert.Equal("json", format.GetString());
            using var ordinary = JsonDocument.Parse(storage.Messages[0]);
            Assert.False(
                ordinary.RootElement.GetProperty("additionalProperties").TryGetProperty("resultFormat", out _)
            );
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_ContentLevelResult_AddsMessageMetadataWithoutChangingSharedProperties(bool streaming)
    {
        var properties = new AdditionalPropertiesDictionary { ["subtype"] = "success" };
        var content = new TextContent("not valid JSON")
        {
            AdditionalProperties = new() { ["type"] = JsonSerializer.SerializeToElement("result") },
        };
        AdditionalPropertiesDictionary? updated;
        if (streaming)
        {
            var update = new AgentResponseUpdate { AdditionalProperties = properties, Contents = [content] };
            ResponseSchemaResultMetadata.Apply(update);
            updated = update.AdditionalProperties;
        }
        else
        {
            var message = new ChatMessage(ChatRole.Assistant, [content]) { AdditionalProperties = properties };
            ResponseSchemaResultMetadata.Apply(message);
            updated = message.AdditionalProperties;
        }

        Assert.Equal("json", updated!["resultFormat"]);
        Assert.Equal("success", updated["subtype"]);
        Assert.False(properties.ContainsKey("resultFormat"));
        Assert.Equal("not valid JSON", content.Text);
    }

    private sealed class RecordingHistoryProvider : ChatHistoryProvider
    {
        public List<string> Messages { get; } = [];

        protected override ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken)
        {
            Messages.AddRange(
                (context.ResponseMessages ?? []).Select(message =>
                    JsonSerializer.Serialize(
                        message.ToAiMessage(),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    )
                )
            );
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CallbackAgent : AIAgent
    {
        private readonly ChatHistoryProvider _history;
        private readonly string _author;

        public CallbackAgent(ChatHistoryProvider history, string author)
        {
            _history = history;
            _author = author;
        }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new TestSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<AgentSession>(new TestSession());

        protected override async Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken
        )
        {
            await PersistAsync(session, messages, cancellationToken);
            return new AgentResponse(CreateMessages());
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            await PersistAsync(session, messages, cancellationToken);
            foreach (var message in CreateMessages())
            {
                yield return new AgentResponseUpdate
                {
                    Role = message.Role,
                    AuthorName = message.AuthorName,
                    MessageId = message.MessageId,
                    Contents = message.Contents,
                    AdditionalProperties = message.AdditionalProperties,
                };
            }
        }

        private async Task PersistAsync(
            AgentSession? session,
            IEnumerable<ChatMessage> input,
            CancellationToken cancellationToken
        )
        {
            session ??= await CreateSessionAsync(cancellationToken);
            await _history.InvokedAsync(
                new ChatHistoryProvider.InvokedContext(this, session, input, CreateMessages()),
                cancellationToken
            );
        }

        private List<ChatMessage> CreateMessages() =>
            [
                new(ChatRole.Assistant, "Reviewing...")
                {
                    MessageId = "text",
                    AuthorName = _author,
                    AdditionalProperties = new() { ["type"] = "assistant" },
                },
                new(ChatRole.Assistant, "{\"approved\":false}")
                {
                    MessageId = "result",
                    AuthorName = _author,
                    AdditionalProperties = new() { ["type"] = "result", ["subtype"] = "success" },
                },
            ];
    }

    private sealed class TestSession : AgentSession;
}
