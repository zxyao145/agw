using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Agents.Middleware;
using Agw.Agents.Execution.Summaries;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Data.Entities.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class AgentRuntimeServiceSummaryTests
{
    [Theory]
    [InlineData(AgentType.System)]
    [InlineData(AgentType.External)]
    public async Task AppendDefinitionSummaryAsync_EnabledAgent_AppendsResult(AgentType agentType)
    {
        var projectId = Guid.CreateVersion7();
        var agentModelProviderId = Guid.CreateVersion7();
        var summaryModelProviderId = Guid.CreateVersion7();
        var summaryService = new RecordingSummaryService();
        var service = CreateService(summaryService);
        var agent = new Agent
        {
            Type = agentType,
            EnableSummary = true,
            ModelProviderId = agentModelProviderId,
            SummaryModelProviderId = summaryModelProviderId,
        };
        var outputs = new List<AgwMessage>
        {
            new("assistant-1", "agent", AiRole.Assistant, [new AgwTextContent { Content = "done" }]),
        };

        var result = await service.AppendDefinitionSummaryAsync(
            agent,
            [new ChatMessage(ChatRole.User, "request")],
            outputs,
            projectId,
            "context-1",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(2, result.Count);
        Assert.Equal("result", result[1].AdditionalProperties!["type"]);
        var call = Assert.Single(summaryService.Calls);
        Assert.Equal(summaryModelProviderId, call.ModelProviderId);
        Assert.Equal(["request", "done"], call.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task AppendDefinitionSummaryAsync_SystemAgentWithoutSummaryModelProvider_UsesAgentModelProvider()
    {
        var agentModelProviderId = Guid.CreateVersion7();
        var summaryService = new RecordingSummaryService();
        var service = CreateService(summaryService);

        await service.AppendDefinitionSummaryAsync(
            new Agent
            {
                Type = AgentType.System,
                EnableSummary = true,
                ModelProviderId = agentModelProviderId,
                SummaryModelProviderId = null,
            },
            [new ChatMessage(ChatRole.User, "request")],
            [new AgwMessage("assistant-1", "agent", AiRole.Assistant, [])],
            Guid.CreateVersion7(),
            "context-1",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(agentModelProviderId, Assert.Single(summaryService.Calls).ModelProviderId);
    }

    [Theory]
    [InlineData(AgentType.System, false)]
    [InlineData(AgentType.External, false)]
    public async Task AppendDefinitionSummaryAsync_SummaryDisabled_ReturnsOriginalMessages(
        AgentType agentType,
        bool enableSummary
    )
    {
        var summaryService = new RecordingSummaryService();
        var service = CreateService(summaryService);
        var output = new AgwMessage("assistant-1", "agent", AiRole.Assistant, []);

        var result = await service.AppendDefinitionSummaryAsync(
            new Agent
            {
                Type = agentType,
                EnableSummary = enableSummary,
                ModelProviderId = Guid.CreateVersion7(),
            },
            [new ChatMessage(ChatRole.User, "request")],
            [output],
            Guid.CreateVersion7(),
            "context-1",
            TestContext.Current.CancellationToken
        );

        Assert.Same(output, Assert.Single(result));
        Assert.Empty(summaryService.Calls);
    }

    private static AgentRuntimeService CreateService(IAgentTurnSummaryService summaryService) =>
        new(
            agentAppService: null!,
            projectAppService: null!,
            capabilityComposer: null!,
            chatHistoryProvider: null!,
            providerSessionState: null!,
            taskSessionBindingService: null!,
            dataPaths: null!,
            fileSystemResolver: null!,
            sessionStateStore: null!,
            logger: NullLogger<AgentRuntimeService>.Instance,
            observabilityMiddleware: new ObservabilityMiddleware(NullLogger<ObservabilityMiddleware>.Instance),
            usageTrackingMiddleware: null!,
            summaryService
        );

    private sealed class RecordingSummaryService : IAgentTurnSummaryService
    {
        public List<Call> Calls { get; } = [];

        public Task<ChatMessage> CreateResultAsync(
            Guid modelProviderId,
            IReadOnlyList<ChatMessage> sourceMessages,
            Guid projectId,
            string contextId,
            string? customInstructions,
            CancellationToken cancellationToken = default
        )
        {
            Calls.Add(new Call(modelProviderId, sourceMessages));
            return Task.FromResult(AgentTurnSummaryService.CreateResultMessage("summary"));
        }
    }

    private sealed record Call(Guid ModelProviderId, IReadOnlyList<ChatMessage> Messages);
}
