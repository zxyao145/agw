using System.Reflection;
using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Summaries;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Tests;

public partial class AgentflowRuntimeServiceTests
{
    [Theory]
    [InlineData("InProcess")]
    [InlineData("Distributed")]
    public async Task AddAgents_RuntimeCollaborators_AreScopedAndResolveThroughTheSameFacade(string executionProvider)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Execution:Provider"] = executionProvider,
                    ["Database:Provider"] = "postgres",
                }
            )
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgents(configuration, new DependencyInjection.RegistrationOptions(false, false, false));
        services.AddSingleton<IApplicationLock, InMemoryApplicationLock>();
        services.AddScoped<IRepository<Agentflow>>(_ => new TestRepository<Agentflow>([], flow => flow.Id));
        services.AddScoped<IRepository<AgentflowNode>>(_ => new TestRepository<AgentflowNode>([], node => node.NodeId));
        services.AddScoped<IRepository<AgentflowEdge>>(_ => new TestRepository<AgentflowEdge>([], edge => edge.EdgeId));
        services.AddScoped<IAgentRuntimeService>(_ => new StubAgentRuntimeService(Guid.CreateVersion7()));
        services.AddScoped<IProviderSessionState>(_ => new StubProviderSessionState());
        services.AddScoped<IAgentTurnSummaryService>(_ => new RecordingSummaryService());
        services.AddScoped<Agw.Projects.Contracts.Runtime.IProjectDefaultResolver>(
            _ => new TestProjectDefaultResolver()
        );
        services.AddScoped<Agw.Projects.Contracts.Runtime.IProjectRuntimeFacade>(_ => new TestProjectRuntimeFacade());
        var types = new[]
        {
            typeof(AgentflowWorkflowFactory),
            typeof(AgentflowExecutionContextFactory),
            typeof(AgentflowCheckpointSupport),
            typeof(DurableAgentflowSegmentRunner),
            typeof(InProcessAgentflowRunner),
            typeof(AgentflowRuntimeService),
        };
        foreach (var type in types)
            Assert.Equal(
                ServiceLifetime.Scoped,
                Assert.Single(services, descriptor => descriptor.ServiceType == type).Lifetime
            );
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();

        var runtime = first.ServiceProvider.GetRequiredService<AgentflowRuntimeService>();

        Assert.Same(runtime, first.ServiceProvider.GetRequiredService<IAgentflowRuntimeService>());
        foreach (var type in types)
        {
            Assert.Same(first.ServiceProvider.GetRequiredService(type), first.ServiceProvider.GetRequiredService(type));
            Assert.NotSame(
                first.ServiceProvider.GetRequiredService(type),
                second.ServiceProvider.GetRequiredService(type)
            );
        }
    }

    [Fact]
    public void RuntimeConstructor_DependsOnlyOnCollaboratorsAndProjectResolution()
    {
        var types = Assert
            .Single(typeof(AgentflowRuntimeService).GetConstructors())
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToHashSet();

        Assert.Equal(6, types.Count);
        Assert.Contains(typeof(AgentflowExecutionContextFactory), types);
        Assert.Contains(typeof(AgentflowWorkflowFactory), types);
        Assert.Contains(typeof(InProcessAgentflowRunner), types);
        Assert.Contains(typeof(DurableAgentflowSegmentRunner), types);
        Assert.Contains(typeof(Agw.Projects.Contracts.Runtime.IProjectDefaultResolver), types);
        Assert.Contains(typeof(Agw.Projects.Contracts.Runtime.IProjectRuntimeFacade), types);
    }

    [Theory]
    [InlineData(typeof(InProcessAgentflowRunner))]
    [InlineData(typeof(DurableAgentflowSegmentRunner))]
    [InlineData(typeof(AgentflowCheckpointSupport))]
    [InlineData(typeof(AgentflowExecutionContextFactory))]
    public void RunnerFields_DoNotRetainPerExecutionState(Type type)
    {
        var fields = type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.NotEmpty(fields);
        Assert.All(
            fields,
            field =>
            {
                Assert.True(field.IsInitOnly);
                Assert.False(typeof(System.Collections.IEnumerable).IsAssignableFrom(field.FieldType));
                Assert.False(
                    field.FieldType.Namespace?.StartsWith("Microsoft.Agents.AI.Workflows", StringComparison.Ordinal)
                        == true
                );
            }
        );
    }

    [Fact]
    public async Task GetMermaidAsync_BuildAndCleanupFail_PreservesOriginalException()
    {
        var agent = new ScriptedAgent([], failOnDispose: true);
        var failure = new InvalidOperationException("construction failed");
        var count = 0;
        var fixture = CreateCharacterizationFixture(
            [AgentflowNodeKind.Agent, AgentflowNodeKind.Agent, AgentflowNodeKind.Output],
            _ => ++count == 1 ? agent : throw failure
        );

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.GetMermaidAsync(fixture.Flow.Id, TestContext.Current.CancellationToken)
        );

        Assert.Same(failure, exception);
        Assert.Equal(1, agent.DisposeCount);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_CancelAfterHumanRequest_DoesNotEmitFinished()
    {
        var agent = new ScriptedAgent(["must not run"]);
        var fixture = CreateCharacterizationFixture(
            [AgentflowNodeKind.HumanGate, AgentflowNodeKind.Agent, AgentflowNodeKind.Output],
            _ => agent
        );
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var messages = new List<AgwMessage>();

        await foreach (
            var message in fixture.Service.ExecuteStreamingAsync(
                fixture.Flow.Id,
                "input",
                cancellation.Token,
                humanGateApprovalHandler: new CancellingApprovalHandler()
            )
        )
        {
            messages.Add(message);
            if (MessageShape(message) == "human-gate-request")
                cancellation.Cancel();
        }

        Assert.Equal(["input", "human-gate-request"], messages.Select(MessageShape));
        Assert.Empty(agent.Inputs);
        Assert.Equal(1, agent.DisposeCount);
    }

    [Fact]
    public async Task ExecuteDurableSegmentAsync_SinkFails_PropagatesAndDisposes()
    {
        var agent = new ScriptedAgent(["done"]);
        var fixture = CreateCharacterizationFixture([AgentflowNodeKind.Agent, AgentflowNodeKind.Output], _ => agent);
        var manifest = CreateManifest(fixture.Flow.Id);
        var failure = new InvalidOperationException("sink failed");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ExecuteDurableSegmentAsync(
                manifest,
                new(manifest.ExecutionId, 0, [], null),
                new FailingSegmentSink(failure),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Same(failure, exception);
        Assert.Equal(1, agent.DisposeCount);
    }

    private sealed class FailingSegmentSink : IExecutionMessageSink
    {
        private readonly Exception _failure;

        public FailingSegmentSink(Exception failure)
        {
            _failure = failure;
        }

        public ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken) =>
            ValueTask.FromException(_failure);
    }
}
