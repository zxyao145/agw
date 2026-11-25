using Agw.Agents.Execution.Agents.ExternalAgents.Acp;
using Agw.Agents.Execution.Agents.ExternalAgents.Acp.Protocol;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Turns;
using Agw.Projects.Application.History;
using Agw.Providers.Contracts;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Tooling;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using static Agw.Agents.Tests.Acp.AcpAgentHarness;

namespace Agw.Agents.Tests.Acp;

public sealed class AcpPermissionTests
{
    private static object Permission(string toolCallId = "t1") =>
        new
        {
            permission = new
            {
                toolCall = new
                {
                    toolCallId,
                    title = "Edit src/x.ts",
                    kind = "edit",
                    rawInput = new { path = "src/x.ts" },
                },
                options = new[]
                {
                    new
                    {
                        optionId = "allow",
                        name = "Allow once",
                        kind = "allow_once",
                    },
                    new
                    {
                        optionId = "reject",
                        name = "Reject once",
                        kind = "reject_once",
                    },
                },
            },
        };

    [Fact]
    public void ToResponse_ApprovedWithoutAllowOnce_UsesAllowAlways()
    {
        var response = AcpPermissionOptions.ToResponse(
            [new("always", "Allow always", "allow_always"), new("no", "Reject once", "reject_once")],
            AcpPermissionDecision.Allowed
        );
        Assert.Equal(new AcpPermissionOutcome("selected", "always"), response.Outcome);
    }

    [Fact]
    public void ToResponse_NoMatchingKind_ReturnsCancelled()
    {
        var response = AcpPermissionOptions.ToResponse(
            [new("yes", "Allow once", "allow_once")],
            AcpPermissionDecision.Rejected
        );
        Assert.Equal(new AcpPermissionOutcome("cancelled"), response.Outcome);
    }

    [Theory]
    [InlineData(true, "allow")]
    [InlineData(false, "reject")]
    public async Task RunTurn_PermissionAlwaysAsk_ReportsUnannouncedToolAndSelectedOption(bool approved, string option)
    {
        using var user = EnterUser();
        await using var agent = await ScenarioAsync(new { turns = new[] { Turn(Permission(), Text("done")) } });
        var id = await agent.StartTurnAsync(permission: AgwPermissionMode.AlwaysAsk);
        var request = await agent.WaitForInteractionAsync(id);
        Assert.Equal("Allow the ACP agent to run \"Edit src/x.ts\"?", request.Prompt);
        Assert.Equal("t1", request.Source.CallId);
        Assert.Equal("edit", request.Source.ToolName);
        Assert.Equal("src/x.ts", request.Arguments!.Value.GetProperty("path").GetString());
        await agent.SubmitApprovalAsync(request.InteractionId, approved);
        await agent.WaitIdleAsync();
        await agent.AssertCompletedAsync(id);
        Assert.Equal(
            option,
            Assert.Single(agent.Events("permission")).GetProperty("outcome").GetProperty("optionId").GetString()
        );
        var calls = (await agent.ReadHistoryAsync()).SelectMany(row =>
            row.ToChatMessage()?.Contents.OfType<FunctionCallContent>() ?? []
        );
        Assert.Contains(
            calls,
            call => call.CallId == "t1" && call.AdditionalProperties?["toolTitle"]?.ToString() == "Edit src/x.ts"
        );
    }

    [Fact]
    public async Task RunTurn_PermissionAllowSameArguments_SecondCallSkipsPrompt()
    {
        using var user = EnterUser();
        await using var agent = await ScenarioAsync(
            new { turns = new[] { Turn(Permission(), Permission("t2"), Text("done")) } }
        );
        var id = await agent.StartTurnAsync(permission: AgwPermissionMode.AllowSameArguments);
        var request = await agent.WaitForInteractionAsync(id);
        await agent.SubmitApprovalAsync(request.InteractionId, true);
        await agent.WaitIdleAsync();
        await agent.AssertCompletedAsync(id);
        Assert.Single(agent.ReadMessages(id), AgwMessageClassifier.IsInteractionRequest);
        var events = agent.Events("permission");
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal("allow", e.GetProperty("outcome").GetProperty("optionId").GetString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunTurn_AllowSameArgumentsWithUnknownInput_RequestsApprovalForBothEdits(bool explicitNull)
    {
        using var user = EnterUser();
        object Edit(string id, string title) =>
            new
            {
                permission = new
                {
                    toolCall = explicitNull
                        ? (object)
                            new
                            {
                                toolCallId = id,
                                title,
                                kind = "edit",
                                rawInput = (object?)null,
                            }
                        : new
                        {
                            toolCallId = id,
                            title,
                            kind = "edit",
                        },
                    options = new[]
                    {
                        new
                        {
                            optionId = "allow",
                            name = "Allow once",
                            kind = "allow_once",
                        },
                        new
                        {
                            optionId = "reject",
                            name = "Reject once",
                            kind = "reject_once",
                        },
                    },
                },
            };
        await using var agent = await ScenarioAsync(
            new { turns = new[] { Turn(Edit("first", "Edit A"), Edit("second", "Edit B"), Text("done")) } }
        );
        var turn = await agent.StartTurnAsync(permission: AgwPermissionMode.AllowSameArguments);
        var first = await agent.WaitForInteractionAsync(turn);
        Assert.Equal("first", first.Source.CallId);
        await agent.SubmitApprovalAsync(first.InteractionId, true);
        await WaitUntilAsync(() =>
            agent.Events("permission").Count == 2
            || agent.ReadMessages(turn).Count(AgwMessageClassifier.IsInteractionRequest) == 2
        );
        Assert.Equal(2, agent.ReadMessages(turn).Count(AgwMessageClassifier.IsInteractionRequest));
        var next = await agent.WaitForInteractionAsync(turn, index: 1);
        Assert.Equal("second", next.Source.CallId);
        await agent.SubmitApprovalAsync(next.InteractionId, false);
        await agent.WaitIdleAsync();
        await agent.AssertCompletedAsync(turn);
        Assert.Equal(
            ["allow", "reject"],
            agent.Events("permission").Select(entry => entry.GetProperty("outcome").GetProperty("optionId").GetString())
        );
    }

    [Fact]
    public async Task RunTurn_PermissionFullAccess_AllowsWithoutInteraction()
    {
        using var user = EnterUser();
        await using var agent = await ScenarioAsync(new { turns = new[] { Turn(Permission(), Text("done")) } });
        var id = await agent.RunTurnAsync(permission: AgwPermissionMode.FullAccess);
        await agent.AssertCompletedAsync(id);
        Assert.DoesNotContain(agent.ReadMessages(id), AgwMessageClassifier.IsInteractionRequest);
        Assert.Equal(
            "allow",
            Assert.Single(agent.Events("permission")).GetProperty("outcome").GetProperty("optionId").GetString()
        );
    }

    [Fact]
    public async Task RunTurn_PermissionPendingWhenInterrupted_RepliesCancelled()
    {
        using var user = EnterUser();
        await using var agent = await ScenarioAsync(new { turns = new[] { Turn(Permission(), Text("done")) } });
        var id = await agent.StartTurnAsync(permission: AgwPermissionMode.AlwaysAsk);
        await agent.WaitForInteractionAsync(id);
        agent.Interrupt();
        await agent.WaitIdleAsync();
        Assert.Equal(ProjectConversationTurnStatus.Interrupted, (await agent.ReadTurnAsync(id)).Status);
        Assert.Equal(
            "cancelled",
            Assert.Single(agent.Events("permission")).GetProperty("outcome").GetProperty("outcome").GetString()
        );
    }

    [Fact]
    public async Task RunTurn_BackgroundAgentPermission_Rejects()
    {
        using var user = EnterUser();
        await using var agent = await ScenarioAsync(
            new { turns = new[] { Turn(Permission(), Text("background finished")) } }
        );
        var parentId = Guid.NewGuid();
        await agent.Runtime.QueryAsync(db =>
        {
            var relation = new ModelProviderRelation
            {
                Id = Guid.NewGuid(),
                Model = new AgwAiModel { Id = Guid.NewGuid(), Name = "model" },
                Provider = new Provider
                {
                    Id = Guid.NewGuid(),
                    Name = "model provider",
                    Endpoint = "http://127.0.0.1",
                    ProviderType = ProviderType.OpenAIChatCompletions,
                    AuthConfigs = [new ProviderAuthConfig { ApiKey = "test" }],
                },
            };
            db.ModelProviders.Add(relation);
            db.Agents.Add(
                new Agent
                {
                    Id = parentId,
                    Name = "parent",
                    DisplayName = "Parent",
                    ModelProviderId = relation.Id,
                    Tools =
                    [
                        new ToolBlockValue
                        {
                            Definition = new BackgroundAgentsToolBlockDefinition
                            {
                                Options = new BackgroundAgentsToolBlockOptions
                                {
                                    AllowedAgentIds = [agent.Runtime.AgentId],
                                },
                            },
                        },
                    ],
                }
            );
            return db.SaveChangesAsync(Ct);
        });
        var context = ExecutionTestScopes.Context(
            projectId: agent.Runtime.ProjectId,
            conversationId: agent.Runtime.ConversationId,
            contextId: ExternalAgentHarness.ContextId,
            agentId: parentId,
            workspace: agent.Runtime.WorkspacePath,
            userId: "1001"
        );
        using var execution = ExecutionTestScopes
            .Scope(context, new TurnBroadcast(context.TurnId, "1001", TimeProvider.System))
            .Push();
        var factory = agent.Runtime.Services.GetRequiredService<AgentRuntimeFactory>();
        await using var parent = await factory.CreateRuntimeAsync(
            parentId,
            new AgentExecutionTask
            {
                ProjectId = agent.Runtime.ProjectId,
                ProjectConversationId = agent.Runtime.ConversationId,
                ContextId = ExternalAgentHarness.ContextId,
            },
            new ExecutionSettings(agent.Runtime.ProjectId, agent.Runtime.ConversationId),
            Ct
        );
        var provider = Assert.IsType<BackgroundAgentsProvider>(parent!.Agent.GetService<BackgroundAgentsProvider>());
        var tools = await provider.InvokingAsync(
            new AIContextProvider.InvokingContext(parent.Agent, parent.Session, new AIContext()),
            Ct
        );
        var start = Assert.Single(tools.Tools!.OfType<AIFunction>(), f => f.Name == "background_agents_start_task");
        var response = await start.InvokeAsync(
            new AIFunctionArguments
            {
                ["agentName"] = "acp",
                ["input"] = "edit",
                ["description"] = "edit file",
            },
            Ct
        );
        Assert.Contains("started", response?.ToString());
        await WaitUntilAsync(() => agent.Events("permission").Count > 0);
        Assert.Equal(
            "reject",
            Assert.Single(agent.Events("permission")).GetProperty("outcome").GetProperty("optionId").GetString()
        );
        await provider.ReleaseSessionAsync(parent.Session, cancellationToken: Ct);
    }
}
