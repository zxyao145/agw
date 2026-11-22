using Agw.Agents.Execution.Agents.Composition;
using Agw.Agents.Execution.Agents.History;
using Agw.Agents.Execution.Agents.Middleware.ModelInput;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.Agents.Tools;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public sealed class AgentProviderPromptPipelineTests
{
    [Fact]
    public async Task Responses_FirstLocalHistoryRequest_PromotesDefaultSystemInstructionsThroughMaf()
    {
        using var owner = HistoryTestFixture.EnterUser();
        await using var historyFixture = await HistoryTestFixture.CreateAsync();
        await using var model = new OpenAiResponsesReasoningChatClientTests.ClientFixture("https://api.openai.com/v1");
        var history = historyFixture.CreateProvider();
        var definition = CreateDefinition(history, "gpt-6.2-sol");
        var stableInstructions = AgentRuntimeFactoryUtil.BuildInstructions(definition.SystemPrompt);
        var client = OpenAiResponsesPromptCachingChatClient.Create(
            model.Client,
            new Uri("https://api.openai.com/v1"),
            definition.ModelId,
            stableInstructions
        );
        await using var capabilities = CreateCapabilities();
        var innerAgent = client.AsAgwAgent(
            definition,
            capabilities,
            NullLoggerFactory.Instance,
            historyFixture.Services
        );
        var agent = HistoryTestFixture.Record(innerAgent, history);
        var session = await historyFixture.CreateSessionAsync(agent);

        await using (historyFixture.BeginTurn())
        {
            await using var updates = agent
                .RunStreamingAsync(
                    [new(ChatRole.User, "question")],
                    session,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .GetAsyncEnumerator(TestContext.Current.CancellationToken);
            Assert.True(await updates.MoveNextAsync());
        }

        var payload = Assert.Single(model.Handler.Requests);
        Assert.Equal("developer", payload.GetProperty("input")[0].GetProperty("role").GetString());
        Assert.Equal(
            stableInstructions,
            payload.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString()
        );
        Assert.False(payload.TryGetProperty("instructions", out _));
    }

    [Fact]
    public async Task ChatCompletions_FirstLocalHistoryRequest_SortsToolsAndSerializesStableKeyThroughMaf()
    {
        const string endpoint = "https://api.openai.com/v1";
        using var owner = HistoryTestFixture.EnterUser();
        await using var historyFixture = await HistoryTestFixture.CreateAsync();
        await using var model = new OpenAiReasoningChatClientTests.ClientFixture(
            endpoint,
            AgentRuntimeFactoryUtil.BuildInstructions(null)
        );
        model.Handler.CompleteAfterTools = true;
        var history = historyFixture.CreateProvider();
        var definition = CreateDefinition(history, "gpt-5.5");
        await using var capabilities = CreateCapabilities(
            AIFunctionFactory.Create((Func<string>)(() => "zeta"), "zeta"),
            AIFunctionFactory.Create((Func<string>)(() => "lookup"), "lookup")
        );
        var innerAgent = model.Client.AsAgwAgent(
            definition,
            capabilities,
            NullLoggerFactory.Instance,
            historyFixture.Services
        );
        var agent = HistoryTestFixture.Record(innerAgent, history);
        var session = await historyFixture.CreateSessionAsync(agent);

        await using (historyFixture.BeginTurn())
        {
            await agent.RunAsync(
                [new(ChatRole.User, "question")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            );
        }

        Assert.Equal(2, model.Handler.Requests.Count);
        var firstPayload = model.Handler.Requests[0];
        Assert.Equal("system", firstPayload.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal(
            "You are a helpful agent.",
            firstPayload.GetProperty("messages")[0].GetProperty("content").GetString()
        );
        Assert.Equal(
            new[] { "lookup", "zeta" },
            firstPayload
                .GetProperty("tools")
                .EnumerateArray()
                .Select(tool => tool.GetProperty("function").GetProperty("name").GetString())
        );
        Assert.False(string.IsNullOrEmpty(firstPayload.GetProperty("prompt_cache_key").GetString()));
        Assert.Equal(
            firstPayload.GetProperty("prompt_cache_key").GetString(),
            model.Handler.Requests[1].GetProperty("prompt_cache_key").GetString()
        );
    }

    private static ResolvedAgentDefinition CreateDefinition(AgwChatHistoryProvider history, string modelId) =>
        new()
        {
            Id = "test-agent",
            Name = "Test Agent",
            ModelId = modelId,
            SystemPrompt = null,
            OpenTelemetrySourceName = "test-source",
            ChatHistoryProvider = history,
            CompactionProvider = new CompactionProvider(
                new ContextWindowCompactionStrategy(100_000, 10_000),
                stateKey: "prompt-pipeline"
            ),
        };

    private static AgentCapabilityComposition CreateCapabilities(params AITool[] tools) =>
        new(
            tools,
            [],
            [],
            [],
            [],
            [],
            new HashSet<string>(),
            [],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new AgentResourceLease()
        );
}
