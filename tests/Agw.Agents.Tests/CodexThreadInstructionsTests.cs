using Agw.Agents.Execution.Agentflows.Messaging;
using Agw.Agents.Execution.Agents.ExternalAgents.Codex;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.AI;
using OpenAI.CodexSdk.AppServer;
using OpenAI.CodexSdk.AppServer.MAF;
using OpenAI.CodexSdk.AppServer.Protocol;

namespace Agw.Agents.Tests;

public sealed class CodexThreadInstructionsTests
{
    [Fact]
    public void PrepareInput_Instructions_PreservesOrderAndConfiguredOptions()
    {
        var original = new CodexAppServerAIAgentOptions
        {
            ThreadStartParams = new ThreadStartParams
            {
                BaseInstructions = "base",
                DeveloperInstructions = "configured",
                Model = "model",
            },
            ConfigureThreadResume = parameters =>
                parameters with
                {
                    BaseInstructions = "resumed-base",
                    DeveloperInstructions = "resumed-configured",
                },
        };
        var user = new ChatMessage(ChatRole.User, "user input");
        var image = new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1, 2, 3 }, "image/png")]);
        var messages = AgentflowMessageTransforms.ApplyInstructions([user, image], "start");
        messages = AgentflowMessageTransforms.ApplyInstructions(messages, "node");
        messages.Add(new ChatMessage(new ChatRole("developer"), "additional"));

        var prepared = CodexProviderSessionTrackingAgent.PrepareInput(messages, original);
        var resumed = prepared.Options.ConfigureThreadResume!(new ThreadResumeParams { ThreadId = "thread" });

        Assert.Equal([user, image], prepared.Messages);
        Assert.Equal(
            "configured\n\nnode\n\nstart\n\nadditional",
            prepared.Options.ThreadStartParams.DeveloperInstructions.Value
        );
        Assert.Equal("resumed-configured\n\nnode\n\nstart\n\nadditional", resumed.DeveloperInstructions.Value);
        Assert.Equal("base", prepared.Options.ThreadStartParams.BaseInstructions.Value);
        Assert.Equal("resumed-base", resumed.BaseInstructions.Value);
        Assert.Equal("model", prepared.Options.ThreadStartParams.Model.Value);
        Assert.Equal("thread", resumed.ThreadId);
        Assert.Equal("configured", original.ThreadStartParams.DeveloperInstructions.Value);
        Assert.Equal(5, messages.Count);
        Assert.Same(user, prepared.Messages[0]);
        Assert.Same(image, prepared.Messages[1]);
    }

    [Fact]
    public void PrepareInput_FeedbackLoop_PreservesFeedbackAndHumanInput()
    {
        var messages = AgentflowMessageTransforms.CreateFeedbackLoopAgentInput([
            new ChatMessage(ChatRole.Assistant, "Review feedback"),
            new ChatMessage(ChatRole.User, "Proceed") { AuthorName = "human" },
        ]);
        messages = AgentflowMessageTransforms.ApplyInstructions(messages, "node");

        var prepared = CodexProviderSessionTrackingAgent.PrepareInput(messages, new CodexAppServerAIAgentOptions());

        Assert.Equal(
            $"node\n\n{AgentflowMessageTransforms.FeedbackLoopInstruction}",
            prepared.Options.ThreadStartParams.DeveloperInstructions.Value
        );
        Assert.Equal(["Review feedback", "Proceed"], prepared.Messages.Select(message => message.Text));
        Assert.All(prepared.Messages, message => Assert.Equal(ChatRole.User, message.Role));
    }

    [Fact]
    public void PrepareInput_IndependentInvocations_ReplacesAndClearsInstructions()
    {
        var options = new CodexAppServerAIAgentOptions();
        var first = CodexProviderSessionTrackingAgent.PrepareInput(
            [new ChatMessage(ChatRole.System, "first")],
            options
        );
        var second = CodexProviderSessionTrackingAgent.PrepareInput(
            [new ChatMessage(ChatRole.System, "second")],
            options
        );
        var empty = CodexProviderSessionTrackingAgent.PrepareInput(
            [new ChatMessage(ChatRole.User, "continue")],
            options
        );
        var parameters = new ThreadResumeParams { ThreadId = "thread" };

        Assert.Equal("first", first.Options.ConfigureThreadResume!(parameters).DeveloperInstructions.Value);
        Assert.Equal("second", second.Options.ConfigureThreadResume!(parameters).DeveloperInstructions.Value);
        Assert.Equal("", empty.Options.ConfigureThreadResume!(parameters).DeveloperInstructions.Value);
        Assert.Equal("first", first.Options.ThreadStartParams.DeveloperInstructions.Value);
        Assert.False(options.ThreadStartParams.DeveloperInstructions.IsDefined);
        Assert.Null(options.ConfigureThreadResume);
    }

    [Fact]
    public void PrepareInput_ResumeWithoutConfigurator_PreservesConfiguredInstructions()
    {
        var options = new CodexAppServerAIAgentOptions
        {
            ThreadStartParams = new ThreadStartParams { DeveloperInstructions = "configured" },
        };

        var prepared = CodexProviderSessionTrackingAgent.PrepareInput(
            [new ChatMessage(ChatRole.System, "node")],
            options
        );
        var resumed = prepared.Options.ConfigureThreadResume!(new ThreadResumeParams { ThreadId = "thread" });

        Assert.Equal("configured\n\nnode", resumed.DeveloperInstructions.Value);
    }

    [Fact]
    public void PrepareInput_InstructionHasNonTextContent_RejectsUnsupportedContent()
    {
        var message = new ChatMessage(ChatRole.System, [new DataContent(new byte[] { 1 }, "image/png")]);

        var exception = Assert.Throws<AgwException>(() =>
            CodexProviderSessionTrackingAgent.PrepareInput([message], new CodexAppServerAIAgentOptions())
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_NodeInstructions_PreservesTurnInput(bool streaming)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var client = await CodexAppServerClient.StartAsync(
            new CodexAppServerClientOptions { ExecutablePath = CodexAppServerFixture.ExecutablePath() },
            cancellation.Token
        );
        TurnStartParams? observed = null;
        var options = new CodexAppServerAIAgentOptions
        {
            ThreadStartParams = new ThreadStartParams { Ephemeral = true },
            ConfigureTurnStart = (_, parameters) =>
            {
                observed = parameters;
                cancellation.Cancel();
                return parameters;
            },
        };
        var agent = new CodexProviderSessionTrackingAgent(client, options, null, false);
        var session = await agent.CreateSessionAsync(cancellation.Token);
        var input = AgentflowMessageTransforms.ApplyInstructions(
            [new ChatMessage(ChatRole.User, "Create a new branch and then execute it")],
            "Complete the assigned node responsibility."
        );

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (streaming)
            {
                await foreach (var _ in agent.RunStreamingAsync(input, session, cancellationToken: cancellation.Token))
                { }
            }
            else
            {
                await agent.RunAsync(input, session, cancellationToken: cancellation.Token);
            }
        });

        Assert.NotNull(observed);
        Assert.Equal(
            "Create a new branch and then execute it",
            Assert.IsType<TextUserInput>(Assert.Single(observed.Input)).Text
        );
    }
}
