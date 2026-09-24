using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Agents.Middleware.Telemetry;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public sealed class ResponseSchemaResultMetadataTests
{
    [Theory]
    [InlineData(EngineKind.Maf, true)]
    [InlineData(EngineKind.ClaudeCode, true)]
    [InlineData(EngineKind.Codex, true)]
    [InlineData(EngineKind.Pi, true)]
    [InlineData(EngineKind.Maf, false)]
    [InlineData(EngineKind.ClaudeCode, false)]
    [InlineData(EngineKind.Codex, false)]
    [InlineData(EngineKind.Pi, false)]
    public async Task Run_WithSchema_MarksLiveAndPersistedResultsOnly(EngineKind engine, bool streaming)
    {
        using var owner = HistoryTestFixture.EnterUser();
        foreach (var configured in new[] { false, true })
        {
            await using var fixture = await HistoryTestFixture.CreateAsync();
            var history = fixture.CreateProvider(engine, structuredResult: configured);
            var author = engine.ToString();
            AIAgent agent = new CallbackAgent(history, author);
            if (configured)
            {
                agent = new AgentResponseSchemaExecutionAgent(agent, "test", author, "test");
            }
            var session = await fixture.CreateSessionAsync(agent);

            var input = new ChatMessage(ChatRole.User, "Review");
            List<AgwMessage> output;
            if (streaming)
            {
                output = [];
                await foreach (
                    var update in agent.RunStreamingAsync(
                        [input],
                        session,
                        cancellationToken: TestContext.Current.CancellationToken
                    )
                )
                {
                    output.Add(update.ToAiMessage()!);
                }
            }
            else
            {
                var response = await agent.RunAsync(
                    [input],
                    session,
                    cancellationToken: TestContext.Current.CancellationToken
                );
                output = response.Messages.Select(message => message.ToAiMessage()!).ToList();
            }

            Assert.Equal(2, output.Count);
            Assert.Null(output[0].AdditionalProperties?.GetValueOrDefault("resultFormat"));
            Assert.Equal(configured ? "json" : null, output[1].AdditionalProperties?.GetValueOrDefault("resultFormat"));
            Assert.Equal("success", output[1].AdditionalProperties!["subtype"]);
            Assert.Equal("{\"approved\":false}", Assert.IsType<AgwTextContent>(output[1].Contents[0]).Content);

            // SDK 的历史回调在外层执行包装看到输出之前完成持久化；持久化的 Result 总带有格式，没有 ResponseSchema 时为 markdown。
            // Persistence happens inside the SDK history callback, before the outer execution wrapper sees output; a persisted Result always carries its format, markdown without a ResponseSchema.
            var stored = await fixture.ReadMessagesAsync();
            var persisted = Assert.Single(stored, message => message.Text == "{\"approved\":false}");
            Assert.Equal(
                configured ? "json" : "markdown",
                persisted.AdditionalProperties?.GetValueOrDefault("resultFormat")?.ToString()
            );
            var ordinary = Assert.Single(stored, message => message.Text == "Reviewing...");
            Assert.False(ordinary.AdditionalProperties?.ContainsKey("resultFormat") ?? false);
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

    /// <summary>
    /// 像 SDK 一样先在运行开始时调用 InvokingAsync，再在输出之前交出完整响应。
    /// Like SDKs, calls InvokingAsync when the run starts and hands over the complete response before emitting output.
    /// </summary>
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
            await _history.InvokingAsync(
                new ChatHistoryProvider.InvokingContext(this, session, input),
                cancellationToken
            );
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
