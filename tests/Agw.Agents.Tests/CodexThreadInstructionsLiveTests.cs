using System.Text.Json;
using Agw.Agents.Execution.Agentflows.Messaging;
using Agw.Agents.Execution.Agentflows.Workflows;
using Agw.Agents.Execution.Agents.ExternalAgents.Codex;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Data.Entities.Agentflows;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using OpenAI.CodexSdk.AppServer;
using OpenAI.CodexSdk.AppServer.MAF;
using OpenAI.CodexSdk.AppServer.Protocol;

namespace Agw.Agents.Tests;

public sealed class CodexThreadInstructionsLiveTests : IDisposable
{
    private readonly string _workspace = Directory
        .CreateDirectory(Path.Combine(AppContext.BaseDirectory, "test-data", $"codex-instructions-{Guid.NewGuid():N}"))
        .FullName;

    public static bool CanRunLive =>
        CodexAppServerFixture.IsAvailable && CodexLiveTestOptions.Current is { RunProviderSessionTests: true };

    [Fact(SkipUnless = nameof(CanRunLive), Skip = "Set RunProviderSessionTests in test-data/codex-live.json.")]
    public async Task RunAsync_RealThread_UpdatesInstructionsAcrossResumeAndFeedback()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var options = CreateOptions();
        JsonElement saved;
        await using (var client = await StartClientAsync(timeout.Token))
        {
            var agent = new CodexProviderSessionTrackingAgent(client, options, null, false);
            var session = await agent.CreateSessionAsync(timeout.Token);
            var input = AgentflowMessageTransforms.ApplyInstructions(
                [new ChatMessage(ChatRole.User, "Reply exactly USER_VALUE.")],
                "Reply exactly FIRST_NODE to every request."
            );
            var updates = new List<AgentResponseUpdate>();
            await foreach (var update in agent.RunStreamingAsync(input, session, cancellationToken: timeout.Token))
                updates.Add(update);
            Assert.Equal("FIRST_NODE", updates.ToAgentResponse().Text.Trim());
            saved = await agent.SerializeSessionAsync(session, cancellationToken: timeout.Token);
        }

        await using var resumedClient = await StartClientAsync(timeout.Token);
        var resumedAgent = new CodexProviderSessionTrackingAgent(resumedClient, options, null, false);
        var resumedSession = await resumedAgent.DeserializeSessionAsync(saved, cancellationToken: timeout.Token);
        var feedback = AgentflowMessageTransforms.CreateFeedbackLoopAgentInput([
            new ChatMessage(ChatRole.Assistant, "Review complete."),
            new ChatMessage(ChatRole.User, "Continue.") { AuthorName = "human" },
        ]);
        feedback = AgentflowMessageTransforms.ApplyInstructions(
            feedback,
            "Reply exactly RESUMED_NODE to every request."
        );
        var resumed = await resumedAgent.RunAsync(feedback, resumedSession, cancellationToken: timeout.Token);
        Assert.Equal("RESUMED_NODE", resumed.Text.Trim());
        var continued = await resumedAgent.RunAsync(
            [new ChatMessage(ChatRole.User, "Reply exactly CLEARED_NODE.")],
            resumedSession,
            cancellationToken: timeout.Token
        );
        Assert.Equal("CLEARED_NODE", continued.Text.Trim());
        var final = await resumedAgent.SerializeSessionAsync(resumedSession, cancellationToken: timeout.Token);
        Assert.Equal(saved.GetProperty("threadId").GetString(), final.GetProperty("threadId").GetString());
    }

    [Fact(SkipUnless = nameof(CanRunLive), Skip = "Set RunProviderSessionTests in test-data/codex-live.json.")]
    public async Task RunAsync_RealWorkflow_KeepsNodeInstructionsIndependent()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var context = ExecutionTestScopes.Context(runtimeType: AgentRuntimeType.Agentflow, workspace: _workspace);
        using var scope = ExecutionTestScopes
            .Scope(context, new TurnBroadcast(context.TurnId, context.UserId, TimeProvider.System))
            .Push();
        await using var client = await StartClientAsync(timeout.Token);
        var workflow = CreateWorkflow(client);
        var input = AgentflowMessageTransforms.ApplyInstructions(
            [new ChatMessage(ChatRole.User, "Reply exactly USER_VALUE.")],
            "Use the current node instructions."
        );
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow,
            input,
            cancellationToken: timeout.Token
        );
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        var responses = new List<string>();
        await foreach (var item in run.WatchStreamAsync(timeout.Token))
        {
            if (item is WorkflowErrorEvent error)
            {
                Assert.NotNull(error.Exception);
                Assert.Fail(error.Exception.ToString());
            }
            if (item is AgentResponseEvent { Data: AgentResponse response })
                responses.Add(response.Text.Trim());
        }
        Assert.Contains("NODE_A", responses);
        Assert.Contains("NODE_B", responses);
    }

    private Workflow CreateWorkflow(CodexAppServerClient client)
    {
        var options = CreateOptions();
        var nodes = new[]
        {
            new AgentflowNode
            {
                NodeId = "a",
                Name = "A",
                Kind = AgentflowNodeKind.Agent,
                Instructions = "Reply exactly NODE_A.",
            },
            new AgentflowNode
            {
                NodeId = "b",
                Name = "B",
                Kind = AgentflowNodeKind.Agent,
                Instructions = "Reply exactly NODE_B.",
            },
        };
        return Assert.IsType<Workflow>(
            new AgentflowWorkflowCompiler().Compile(
                new Agentflow { Id = Guid.CreateVersion7(), Name = "codex-instructions" },
                nodes,
                [
                    new AgentflowEdge
                    {
                        EdgeId = "a-b",
                        SourceNodeId = "a",
                        TargetNodeId = "b",
                    },
                ],
                new Dictionary<string, AIAgent>
                {
                    ["a"] = new CodexProviderSessionTrackingAgent(client, options, null, false),
                    ["b"] = new CodexProviderSessionTrackingAgent(client, options, null, false),
                }
            )
        );
    }

    private Task<CodexAppServerClient> StartClientAsync(CancellationToken cancellationToken) =>
        CodexAppServerClient.StartAsync(
            new CodexAppServerClientOptions
            {
                ExecutablePath = CodexAppServerFixture.ExecutablePath(CodexLiveTestOptions.Current?.CodexPath),
                WorkingDirectory = _workspace,
            },
            cancellationToken
        );

    private CodexAppServerAIAgentOptions CreateOptions() =>
        new()
        {
            ThreadStartParams = new ThreadStartParams
            {
                Cwd = _workspace,
                Model = CodexLiveTestOptions.Current?.Model,
                Sandbox = SandboxMode.ReadOnly,
                ApprovalPolicy = new StringAskForApproval { Value = StringAskForApprovalValue.Never },
                DeveloperInstructions = "Follow the current node instructions. Reply with text only. Do not use tools.",
                Config = new Dictionary<string, JsonElement>
                {
                    ["model_reasoning_effort"] = JsonSerializer.SerializeToElement("low"),
                },
            },
        };

    public void Dispose() => Directory.Delete(_workspace, recursive: true);
}
