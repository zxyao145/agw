using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Agents.Middleware;
using Agw.Agents.Execution.Summaries;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Data.Entities.Agents;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class AgentRuntimeServiceSummaryTests
{
    [Fact]
    public async Task AppendDefinitionSummaryAsync_EnabledSystemAgent_AppendsResult()
    {
        var projectId = Guid.NewGuid();
        var modelProviderId = Guid.NewGuid();
        var summaryService = new RecordingSummaryService();
        var service = CreateService(summaryService);
        var agent = new Agent
        {
            Type = AgentType.System,
            EnableSummary = true,
            ModelProviderId = modelProviderId,
        };
        var outputs = new List<AgwMessage>
        {
            new("assistant-1", "agent", AiRole.Assistant, [new AgwTextContent { Content = "done" }])
        };

        var result = await service.AppendDefinitionSummaryAsync(
            agent,
            [new ChatMessage(ChatRole.User, "request")],
            outputs,
            projectId,
            "context-1",
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal("result", result[1].AdditionalProperties!["type"]);
        var call = Assert.Single(summaryService.Calls);
        Assert.Equal(modelProviderId, call.ModelProviderId);
        Assert.Equal(["request", "done"], call.Messages.Select(message => message.Text));
    }

    [Theory]
    [InlineData(AgentType.System, false)]
    [InlineData(AgentType.External, true)]
    public async Task AppendDefinitionSummaryAsync_NotEnabledSystemAgent_ReturnsOriginalMessages(
        AgentType agentType,
        bool enableSummary)
    {
        var summaryService = new RecordingSummaryService();
        var service = CreateService(summaryService);
        var output = new AgwMessage("assistant-1", "agent", AiRole.Assistant, []);

        var result = await service.AppendDefinitionSummaryAsync(
            new Agent
            {
                Type = agentType,
                EnableSummary = enableSummary,
                ModelProviderId = Guid.NewGuid(),
            },
            [new ChatMessage(ChatRole.User, "request")],
            [output],
            Guid.NewGuid(),
            "context-1",
            TestContext.Current.CancellationToken);

        Assert.Same(output, Assert.Single(result));
        Assert.Empty(summaryService.Calls);
    }

    private static AgentRuntimeService CreateService(IAgentTurnSummaryService summaryService) =>
        new(
            agentAppService: null!,
            projectAppService: null!,
            toolRegistry: null!,
            chatHistoryProvider: null!,
            providerSessionState: null!,
            taskSessionBindingService: null!,
            dataPaths: null!,
            fileSystemResolver: null!,
            sessionStateStore: null!,
            logger: NullLogger<AgentRuntimeService>.Instance,
            observabilityMiddleware: new ObservabilityMiddleware(NullLogger<ObservabilityMiddleware>.Instance),
            usageTrackingMiddleware: null!,
            summaryService);

    private sealed class RecordingSummaryService : IAgentTurnSummaryService
    {
        public List<Call> Calls { get; } = [];

        public Task<ChatMessage> CreateResultAsync(
            Guid modelProviderId,
            IReadOnlyList<ChatMessage> sourceMessages,
            Guid projectId,
            string contextId,
            string? customInstructions,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new Call(modelProviderId, sourceMessages));
            return Task.FromResult(AgentTurnSummaryService.CreateResultMessage("summary"));
        }
    }

    private sealed record Call(Guid ModelProviderId, IReadOnlyList<ChatMessage> Messages);
}
