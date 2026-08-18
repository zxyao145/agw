using System.Runtime.CompilerServices;
using Agw.Agents.Execution.Agents.AIContextProviders.AgwWorkspace;
using Agw.Files.Utils;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Tests;

public class AgwWorkspaceProviderTests
{
    [Fact]
    public async Task InvokingAsync_UsesAgentAndProjectFromProvider()
    {
        var agent = new Agent { Id = Guid.CreateVersion7() };
        var project = new Project { Id = Guid.CreateVersion7(), Workspace = "/workspace" };
        var provider = new AgwWorkspaceProvider(
            agent,
            project,
            [new StubInstructionsSource((context, _) => ValueTask.FromResult<string?>(context.Project.Workspace))]
        );

        var result = await provider.InvokingAsync(CreateInvokingContext(), TestContext.Current.CancellationToken);

        Assert.Equal(project.Workspace, result.Instructions);
    }

    [Fact]
    public async Task InvokingAsync_MultipleSources_MergesNonEmptyInstructionsInRegistrationOrder()
    {
        var provider = CreateProvider(
            new StubInstructionsSource((_, _) => ValueTask.FromResult<string?>(" first ")),
            new StubInstructionsSource((_, _) => ValueTask.FromResult<string?>("   ")),
            new StubInstructionsSource((_, _) => ValueTask.FromResult<string?>("\nsecond\n"))
        );

        var result = await provider.InvokingAsync(CreateInvokingContext(), TestContext.Current.CancellationToken);

        Assert.Equal($"first{Environment.NewLine}second", result.Instructions);
    }

    [Fact]
    public async Task InvokingAsync_SameProvider_ReadsSourceOnEveryInvocation()
    {
        var callCount = 0;
        var provider = CreateProvider(
            new StubInstructionsSource((_, _) => ValueTask.FromResult<string?>($"value-{++callCount}"))
        );

        var first = await provider.InvokingAsync(CreateInvokingContext(), TestContext.Current.CancellationToken);
        var second = await provider.InvokingAsync(CreateInvokingContext(), TestContext.Current.CancellationToken);

        Assert.Equal("value-1", first.Instructions);
        Assert.Equal("value-2", second.Instructions);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task InvokingAsync_SourceThrows_PropagatesException()
    {
        var provider = CreateProvider(
            new StubInstructionsSource((_, _) => ValueTask.FromException<string?>(new TestException()))
        );

        await Assert.ThrowsAsync<TestException>(async () =>
            await provider.InvokingAsync(CreateInvokingContext(), TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task InvokingAsync_CancellationRequested_PropagatesCancellation()
    {
        var sourceCancellationToken = new CancellationToken(canceled: true);
        var provider = CreateProvider(
            new StubInstructionsSource((_, _) => ValueTask.FromCanceled<string?>(sourceCancellationToken))
        );

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await provider.InvokingAsync(CreateInvokingContext(), TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task GetInstructionsAsync_WorkspaceWithTilde_UsesExpandedWorkspace()
    {
        const string workspace = "~/.agw/context-provider-test";
        var source = new ProjectInstructionsSource();
        var context = new AgwInstructionsSourceContext(
            new Agent(),
            new Project { Workspace = workspace },
            CreateInvokingContext()
        );

        var result = await source.GetInstructionsAsync(context, TestContext.Current.CancellationToken);

        var expected = $"""
            # others

            - Your default workspace or working directory is '{PathUtil.ExpandTilde(workspace)}'.
            """;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void AddAgents_RegistersWorkspaceInstructionsSource()
    {
        var services = new ServiceCollection();
        services.AddAgents(new ConfigurationBuilder().Build());
        using var serviceProvider = services.BuildServiceProvider();

        var sources = serviceProvider.GetServices<IAgentInstructionsSource>().ToArray();

        Assert.IsType<ProjectInstructionsSource>(Assert.Single(sources));
    }

    private static AgwWorkspaceProvider CreateProvider(params IAgentInstructionsSource[] sources)
    {
        return new AgwWorkspaceProvider(new Agent(), new Project(), sources);
    }

    private static AIContextProvider.InvokingContext CreateInvokingContext()
    {
        var agent = new ChatClientAgent(new StubChatClient(), new ChatClientAgentOptions { Name = "test-agent" });
        return new AIContextProvider.InvokingContext(agent, null, new AIContext());
    }

    private sealed class StubInstructionsSource : IAgentInstructionsSource
    {
        private readonly Func<AgwInstructionsSourceContext, CancellationToken, ValueTask<string?>> _callback;

        public StubInstructionsSource(
            Func<AgwInstructionsSourceContext, CancellationToken, ValueTask<string?>> callback
        )
        {
            _callback = callback;
        }

        public ValueTask<string?> GetInstructionsAsync(
            AgwInstructionsSourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            return _callback(context, cancellationToken);
        }
    }

    private sealed class StubChatClient : IChatClient
    {
        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType.IsInstanceOfType(this) ? this : null;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "done")]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
        }
    }

    private sealed class TestException : Exception { }
}
