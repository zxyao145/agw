using Agw.Agents.Execution.HumanInteraction;
using Agw.Agents.Execution.HumanInteraction.Durable.Contracts;
using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Shared.Data.Entities.Agentflows;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Tests;

public partial class AgentflowRuntimeServiceTests
{
    [Fact]
    public async Task ExecuteDurableSegmentAsync_CustomInput_UsesNodeIdentityAcrossEveryResume()
    {
        var token = TestContext.Current.CancellationToken;
        var accessor = new HumanInteractionContextAccessor();
        await using var services = new ServiceCollection()
            .AddSingleton(accessor)
            .AddSingleton<IHumanInteractionContextAccessor>(accessor)
            .BuildServiceProvider();
        var completed = new List<string>();
        var fixture = CreateCharacterizationFixture(
            [AgentflowNodeKind.Agent, AgentflowNodeKind.Output],
            _ =>
                MafInteractionIntegrationTests.CreateAgent(
                    new MafInteractionIntegrationTests.InputModel(),
                    services,
                    completed,
                    deferred: true
                ),
            interactionAccessor: accessor
        );
        var manifest = CreateManifest(fixture.Flow.Id);
        manifest = manifest with { Settings = manifest.Settings with { PermissionMode = PermissionMode.FullAccess } };
        var input = new DurableExecutionSegmentInput(manifest.ExecutionId, 0, [], null);
        var ledger = new List<DurableResolvedInteraction>();
        var sink = new RecordingSegmentSink();
        DurableExecutionSegmentResult? result = null;
        for (var segment = 0; segment < 4; segment++)
        {
            result = await fixture.Service.ExecuteDurableSegmentAsync(manifest, input, sink, token);
            if (result.Status != DurableExecutionSegmentStatus.WaitingForHuman)
                break;
            Assert.NotEmpty(result.PendingInteractions);
            var answered = result
                .PendingInteractions.Select(request =>
                {
                    var userInput = Assert.IsType<UserInputInteraction>(request);
                    Assert.Equal("node-0", userInput.Source.NodeId);
                    return new DurableResolvedInteraction(
                        userInput,
                        MafInteractionIntegrationTests.Answer(userInput, cancelled: false)
                    );
                })
                .ToArray();
            ledger.AddRange(answered);
            input = new(manifest.ExecutionId, segment + 1, answered, result.Checkpoint)
            {
                InputCatalog = result.InputCatalog,
                ResolvedInputs = ledger.ToArray(),
            };
        }
        Assert.NotNull(result);
        Assert.True(result.Status == DurableExecutionSegmentStatus.Completed, result.ErrorMessage);
        Assert.Equal(["a:A:keep", "b:B:keep"], completed);
        Assert.Equal(2, ledger.Count);
        Assert.Equal(2, ledger.Select(item => item.Request.InteractionId).Distinct().Count());
    }
}
