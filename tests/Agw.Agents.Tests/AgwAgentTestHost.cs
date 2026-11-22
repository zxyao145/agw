using System.Runtime.CompilerServices;
using Agw.Agents.Execution.Agents.Composition;
using Agw.Tools.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

internal sealed record ModelRequest(
    IReadOnlyList<ChatMessage> Messages,
    string? Instructions,
    IReadOnlyList<string> ToolNames
);

internal sealed class RecordingModelClient : IChatClient
{
    private readonly Func<int, IReadOnlyList<ChatMessage>, IList<AIContent>>? _reply;

    public RecordingModelClient(Func<int, IReadOnlyList<ChatMessage>, IList<AIContent>>? reply = null)
    {
        _reply = reply;
    }

    public List<ModelRequest> Requests { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = CreateResponse(messages, options);
        return Task.FromResult(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, response.Contents) { MessageId = response.MessageId })
            {
                ResponseId = response.ResponseId,
                FinishReason = response.FinishReason,
            }
        );
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        yield return CreateResponse(messages, options);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    private ChatResponseUpdate CreateResponse(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var request = new ModelRequest(
            messages.Select(message => message.Clone()).ToArray(),
            options?.Instructions,
            options?.Tools?.Select(tool => tool.Name).ToArray() ?? []
        );
        var index = Requests.Count;
        Requests.Add(request);
        var contents = _reply?.Invoke(index, request.Messages) ?? [new TextContent("done")];
        return new ChatResponseUpdate(ChatRole.Assistant, contents)
        {
            MessageId = $"answer-{index}",
            ResponseId = $"response-{index}",
            FinishReason = contents.Any(content => content is FunctionCallContent)
                ? ChatFinishReason.ToolCalls
                : ChatFinishReason.Stop,
        };
    }
}

internal static class AgwAgentTestHost
{
    public static ResolvedAgentDefinition Definition(
        ChatHistoryProvider history,
        AIContextProvider? compactionProvider = null,
        string? systemPrompt = null
    ) =>
        new()
        {
            Id = "test-agent",
            Name = "Test agent",
            SystemPrompt = systemPrompt,
            ModelId = "test-model",
            OpenTelemetrySourceName = "test-source",
            ChatHistoryProvider = history,
            CompactionProvider = compactionProvider,
        };

    public static AgentCapabilityComposition Capabilities(
        IReadOnlyList<AITool>? tools = null,
        IReadOnlyList<AIContextProvider>? contextProviders = null,
        IReadOnlyList<LoopEvaluator>? loopEvaluators = null,
        IReadOnlyList<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>>? autoApprovalRules = null,
        IReadOnlySet<string>? planModeAllowedToolNames = null
    )
    {
        var contribution = new ToolContribution();
        contribution.Tools.AddRange(tools ?? []);
        contribution.ContextProviders.AddRange(contextProviders ?? []);
        contribution.LoopEvaluators.AddRange(loopEvaluators ?? []);
        contribution.AutoApprovalRules.AddRange(autoApprovalRules ?? []);
        if (planModeAllowedToolNames != null)
            contribution.PlanModeAllowedToolNames.UnionWith(planModeAllowedToolNames);
        return Capabilities(contribution);
    }

    public static AgentCapabilityComposition Capabilities(ToolContribution contribution) =>
        new(contribution, [], [], new AgentResourceLease());

    public static async Task RunTurnAsync(
        HistoryTestFixture fixture,
        AIAgent agent,
        AgentSession session,
        params ChatMessage[] input
    )
    {
        await using var turn = fixture.BeginTurn();
        await foreach (
            var _ in agent.RunStreamingAsync(input, session, cancellationToken: TestContext.Current.CancellationToken)
        ) { }
    }
}
