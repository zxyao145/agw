using global::A2A;
using Agw.Agents.Definitions.Contracts;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.Agents.Sessions;
using Agw.Agents.Execution.Runtimes;
using Agw.Providers.Contracts;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Exceptions;
using Agw.Shared.Tooling;
using Microsoft.Extensions.DependencyInjection;
using static Agw.Agents.Tests.A2A.A2ARemoteAgentStreamingTests;

namespace Agw.Agents.Tests.A2A;

public sealed class A2ARemoteAgentExecutionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunTurn_WhetherWorkspaceExists_EmitsAssistantAndResult(bool workspaceExists)
    {
        using var user = ExternalAgentHarness.EnterUser();
        await using var server = await A2ATestAgentServer.StartAsync(
            new(ProtocolBindingNames.HttpJson, true, ScriptedAgentHandler.Echo())
            {
                RequiredInterfaceHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer interface" },
            },
            Ct
        );
        await using var harness = await ExternalAgentHarness.CreateAsync(
            server,
            [Credentials(server.InterfaceUrl, "Bearer interface")],
            createWorkspace: workspaceExists
        );

        var turn = await harness.RunTurnAsync("hello remote");

        Assert.Equal(ProjectConversationTurnStatus.Completed, (await harness.ReadTurnAsync(turn)).Status);
        var messages = harness.ReadMessages(turn);
        Assert.Equal(AgwMessageTypes.TurnStart, messages[0].AdditionalProperties?["type"]);
        Assert.Equal(AgwMessageTypes.TurnFinished, messages[^1].AdditionalProperties?["type"]);
        Assert.Contains(messages, message => message.Role == AiRole.Assistant && HasText(message, "hello remote"));
        Assert.Contains(messages, message => IsResult(message) && HasText(message, "hello remote"));
        Assert.Equal(
            "Bearer interface",
            Assert.Single(server.Requests, request => request.Method == "POST").Headers["Authorization"]
        );
        Assert.Equal(workspaceExists, Directory.Exists(harness.WorkspacePath));
        var history = await harness.ReadHistoryAsync();
        Assert.Contains(
            history,
            row =>
                row.Purpose == ConversationMessagePurpose.Input
                && row.ConversationPayload?.Contains("hello remote", StringComparison.Ordinal) == true
        );
        Assert.Contains(
            history,
            row =>
                row.Purpose == ConversationMessagePurpose.Message
                && row.ConversationPayload?.Contains("hello remote", StringComparison.Ordinal) == true
        );
    }

    [Fact]
    public async Task RunTurn_JsonRpcArtifactChunks_UseOneHistoryMessage()
    {
        using var user = ExternalAgentHarness.EnterUser();
        var handler = new ScriptedAgentHandler(
            async (context, queue, ct) =>
            {
                var updater = new TaskUpdater(queue, context.TaskId, context.ContextId);
                await updater.SubmitAsync(ct);
                await updater.StartWorkAsync(Status(context, "searching"), ct);
                await updater.AddArtifactAsync(
                    [Part.FromText("alpha ")],
                    "answer",
                    lastChunk: false,
                    cancellationToken: ct
                );
                await updater.AddArtifactAsync(
                    [Part.FromText("beta ")],
                    "answer",
                    lastChunk: false,
                    append: true,
                    cancellationToken: ct
                );
                await updater.AddArtifactAsync([Part.FromText("gamma")], "answer", append: true, cancellationToken: ct);
                await updater.CompleteAsync(cancellationToken: ct);
            }
        );
        await using var server = await StartAsync(handler, ProtocolBindingNames.JsonRpc);
        await using var harness = await ExternalAgentHarness.CreateAsync(server);

        var turn = await harness.RunTurnAsync("go");

        Assert.Equal(ProjectConversationTurnStatus.Completed, (await harness.ReadTurnAsync(turn)).Status);
        var history = await harness.ReadHistoryAsync();
        Assert.Single(
            history,
            row =>
                row.Purpose == ConversationMessagePurpose.Message
                && row.ConversationPayload?.Contains("alpha beta gamma", StringComparison.Ordinal) == true
        );
        Assert.Contains(
            harness.ReadMessages(turn).SelectMany(message => message.Contents),
            content => content is AgwTextReasoningContent { Content: "searching" }
        );
    }

    [Fact]
    public async Task RunTurn_AfterRuntimeRebuild_ReusesContext()
    {
        using var user = ExternalAgentHarness.EnterUser();
        await using var server = await StartAsync(ScriptedAgentHandler.Echo());
        await using var harness = await ExternalAgentHarness.CreateAsync(server);
        await harness.RunTurnAsync("go");
        var previous = await harness.ReadSessionAsync();

        await harness.RebuildCoordinatorAsync();
        var turn = await harness.RunTurnAsync("Paris");

        Assert.Equal(ProjectConversationTurnStatus.Completed, (await harness.ReadTurnAsync(turn)).Status);
        Assert.NotNull(previous.ContextId);
        var request = A2ARemoteAgentTestClient.ReadInput(server.Requests[^1]);
        Assert.Equal(previous.ContextId, request.ContextId);
        Assert.Null(request.TaskId);
    }

    [Fact]
    public async Task RunTurn_AfterConversationCleared_StartsNewContext()
    {
        using var user = ExternalAgentHarness.EnterUser();
        await using var server = await StartAsync(ScriptedAgentHandler.Echo());
        await using var harness = await ExternalAgentHarness.CreateAsync(server);
        await harness.RunTurnAsync("before");

        await harness.ClearConversationAsync();
        var turn = await harness.RunTurnAsync("after");

        Assert.Equal(ProjectConversationTurnStatus.Completed, (await harness.ReadTurnAsync(turn)).Status);
        Assert.Null(A2ARemoteAgentTestClient.ReadInput(server.Requests[^1]).ContextId);
    }

    [Fact]
    public async Task RunTurn_RemoteTaskFailed_FinishesFailedAndSavesSession()
    {
        using var user = ExternalAgentHarness.EnterUser();
        var handler = new ScriptedAgentHandler(
            async (context, queue, ct) =>
            {
                var updater = new TaskUpdater(queue, context.TaskId, context.ContextId);
                await updater.SubmitAsync(ct);
                await updater.FailAsync(Status(context, "remote failure"), ct);
            }
        );
        await using var server = await StartAsync(handler);
        await using var harness = await ExternalAgentHarness.CreateAsync(server);

        var turn = await harness.RunTurnAsync("go");

        var outcome = await harness.ReadTurnAsync(turn);
        Assert.Equal(ProjectConversationTurnStatus.Failed, outcome.Status);
        Assert.Equal("5020012", outcome.ErrorCode);
        var messages = harness.ReadMessages(turn);
        Assert.Equal("5020012", messages[^1].AdditionalProperties?["errorCode"]);
        var error = Assert.Single(messages.SelectMany(message => message.Contents).OfType<AgwErrorContent>());
        Assert.Contains("remote failure", error.Content);
        var session = await harness.ReadSessionAsync();
        Assert.NotNull(session.ContextId);
        Assert.Equal(TaskState.Failed, session.TaskState);
    }

    [Fact]
    public async Task RunTurn_Interrupted_CancelsAndPersistsRemoteTask()
    {
        using var user = ExternalAgentHarness.EnterUser();
        var handler = new ScriptedAgentHandler(
            async (context, queue, ct) =>
            {
                var updater = new TaskUpdater(queue, context.TaskId, context.ContextId);
                await updater.SubmitAsync(ct);
                await updater.StartWorkAsync(Status(context, "working"), ct);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        );
        await using var server = await StartAsync(handler);
        await using var harness = await ExternalAgentHarness.CreateAsync(server);
        var turn = await harness.StartTurnAsync("go");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (
            !harness
                .ReadMessages(turn)
                .SelectMany(message => message.Contents)
                .Any(content => content is AgwTextReasoningContent { Content: "working" })
        )
            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);

        harness.Interrupt();
        await harness.WaitIdleAsync();

        Assert.Equal(ProjectConversationTurnStatus.Interrupted, (await harness.ReadTurnAsync(turn)).Status);
        var session = await harness.ReadSessionAsync();
        Assert.Equal(TaskState.Canceled, session.TaskState);
        Assert.Equal(session.TaskId, Assert.Single(server.CanceledTaskIds));
    }

    [Fact]
    public async Task RunTurn_EndpointUpdated_UsesMatchingOriginCredentialsAndRevision()
    {
        using var user = ExternalAgentHarness.EnterUser();
        await using var card = await A2ATestAgentServer.StartAsync(
            new(ProtocolBindingNames.HttpJson, true, ScriptedAgentHandler.Echo())
            {
                RequiredCardHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer card" },
            },
            Ct
        );
        await using var peer = await StartAsync(ScriptedAgentHandler.Echo());
        card.Card.SupportedInterfaces[0].Url = peer.InterfaceUrl.AbsoluteUri;
        await using var harness = await ExternalAgentHarness.CreateAsync(
            card,
            [Credentials(card.CardUrl, "Bearer card")]
        );
        await harness.RunTurnAsync("first");
        var initialContext = (await harness.ReadSessionAsync()).ContextId;
        Assert.DoesNotContain("Authorization", Assert.Single(peer.Requests).Headers.Keys);

        await harness.UpdateEndpointAsync(
            new(null, [Credentials(card.CardUrl, null), Credentials(peer.InterfaceUrl, "Bearer interface")])
        );
        await harness.RunTurnAsync("second");
        Assert.Equal(initialContext, A2ARemoteAgentTestClient.ReadInput(peer.Requests[^1]).ContextId);
        Assert.Equal("Bearer interface", peer.Requests[^1].Headers["Authorization"]);

        card.Card.SupportedInterfaces[0].Url = peer.InterfaceUrl.AbsoluteUri + "?revision=2";
        await harness.UpdateEndpointAsync(new(null, null));
        var turn = await harness.RunTurnAsync("third");

        Assert.Equal(ProjectConversationTurnStatus.Completed, (await harness.ReadTurnAsync(turn)).Status);
        Assert.EndsWith("?revision=2", peer.Requests[^1].PathAndQuery);
        Assert.Null(A2ARemoteAgentTestClient.ReadInput(peer.Requests[^1]).ContextId);
        Assert.All(card.Requests, request => Assert.Equal("Bearer card", request.Headers["Authorization"]));
    }

    [Fact]
    public async Task RefreshCard_InterfaceOriginChanged_DoesNotReuseCardCredentials()
    {
        using var user = ExternalAgentHarness.EnterUser();
        var authorization = new Dictionary<string, string> { ["Authorization"] = "Bearer card" };
        await using var card = await A2ATestAgentServer.StartAsync(
            new(ProtocolBindingNames.HttpJson, true, ScriptedAgentHandler.Echo())
            {
                RequiredCardHeaders = authorization,
                RequiredInterfaceHeaders = authorization,
            },
            Ct
        );
        await using var peer = await StartAsync(ScriptedAgentHandler.Echo());
        await using var harness = await ExternalAgentHarness.CreateAsync(
            card,
            [Credentials(card.CardUrl, "Bearer card")]
        );
        var first = await harness.RunTurnAsync("first");
        Assert.Equal(ProjectConversationTurnStatus.Completed, (await harness.ReadTurnAsync(first)).Status);
        var session = await harness.ReadSessionAsync();

        card.Card.SupportedInterfaces[0].Url = peer.InterfaceUrl.AbsoluteUri;
        var refreshed = await harness.RefreshCardAsync();
        var second = await harness.RunTurnAsync("second");

        Assert.Equal(peer.InterfaceUrl.AbsoluteUri, refreshed.InterfaceUrl);
        Assert.NotEqual(session.InterfaceRevision, refreshed.InterfaceRevision);
        Assert.Equal(ProjectConversationTurnStatus.Completed, (await harness.ReadTurnAsync(second)).Status);
        Assert.Equal(2, card.Requests.Count(request => request.Method == "GET"));
        Assert.Single(card.Requests, request => request.Method == "POST");
        Assert.All(card.Requests, request => Assert.Equal("Bearer card", request.Headers["Authorization"]));
        var request = Assert.Single(peer.Requests, request => request.Method == "POST");
        Assert.DoesNotContain("Authorization", request.Headers.Keys);
        Assert.Null(A2ARemoteAgentTestClient.ReadInput(request).ContextId);
    }

    [Fact]
    public async Task RunTurn_PermissionModeAlwaysAsk_IsRejected()
    {
        using var user = ExternalAgentHarness.EnterUser();
        await using var server = await StartAsync(ScriptedAgentHandler.Echo());
        await using var harness = await ExternalAgentHarness.CreateAsync(server);

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            harness.StartTurnAsync("go", AgwPermissionMode.AlwaysAsk)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task GetOrCreateForNodeAsync_A2AAgentTwoNodes_KeepsSeparateSessions()
    {
        using var user = ExternalAgentHarness.EnterUser();
        await using var server = await StartAsync(ScriptedAgentHandler.Echo());
        await using var harness = await ExternalAgentHarness.CreateAsync(server);
        await using var client = new A2ARemoteAgentTestClient(server);
        var store = harness.Services.GetRequiredService<AgentSessionStateStore>();
        var scopes = new[] { "flow:node-a", "flow:node-b" }
            .Select(node => new AgentSessionStateScope(
                harness.ConversationId,
                harness.ProjectId,
                ExternalAgentHarness.ContextId,
                harness.AgentId,
                node
            ))
            .ToArray();
        var contexts = new[] { "c-a", "c-b" };
        for (var index = 0; index < scopes.Length; index++)
        {
            var session = Assert.IsType<Agw.Agents.Execution.Agents.ExternalAgents.A2A.A2ARemoteAgentSession>(
                await client.Agent.CreateSessionAsync(Ct)
            );
            session.BindEndpointRevision(client.Revision);
            session.RecordContext(contexts[index]);
            await store.SaveForNodeAsync(scopes[index], client.Agent, session, Ct);
        }

        for (var index = 0; index < scopes.Length; index++)
        {
            var restored = Assert.IsType<Agw.Agents.Execution.Agents.ExternalAgents.A2A.A2ARemoteAgentSession>(
                await store.GetOrCreateForNodeAsync(client.Agent, scopes[index], Ct)
            );
            Assert.Equal(contexts[index], restored.ContextId);
        }
    }

    [Fact]
    public async Task CreateRuntime_SystemAgentAllowingA2ABackgroundAgent_BuildsRuntimeWithoutFetchingCard()
    {
        using var user = ExternalAgentHarness.EnterUser();
        await using var server = await StartAsync(ScriptedAgentHandler.Echo());
        await using var harness = await ExternalAgentHarness.CreateAsync(server);
        var parent = Guid.CreateVersion7();
        await harness.QueryAsync(db =>
        {
            var provider = new Provider
            {
                Id = Guid.CreateVersion7(),
                Name = "test-provider",
                Endpoint = server.InterfaceUrl.AbsoluteUri,
                ProviderType = ProviderType.OpenAIChatCompletions,
                AuthConfigs = [new ProviderAuthConfig { ApiKey = "test-key" }],
            };
            var model = new AgwAiModel { Id = Guid.CreateVersion7(), Name = "test-model" };
            var relation = new ModelProviderRelation
            {
                Id = Guid.CreateVersion7(),
                Model = model,
                Provider = provider,
            };
            db.ModelProviders.Add(relation);
            db.Agents.Add(
                new Agent
                {
                    Id = parent,
                    Name = "parent",
                    DisplayName = "Parent",
                    ModelProviderId = relation.Id,
                    Tools =
                    [
                        new ToolBlockValue
                        {
                            Definition = new BackgroundAgentsToolBlockDefinition
                            {
                                Options = new BackgroundAgentsToolBlockOptions { AllowedAgentIds = [harness.AgentId] },
                            },
                        },
                    ],
                }
            );
            return db.SaveChangesAsync(Ct);
        });

        var factory = harness.Services.GetRequiredService<AgentRuntimeFactory>();
        await using var runtime = await factory.CreateRuntimeAsync(
            parent,
            new AgentExecutionTask
            {
                ProjectId = harness.ProjectId,
                ProjectConversationId = harness.ConversationId,
                ContextId = ExternalAgentHarness.ContextId,
            },
            new ExecutionSettings(harness.ProjectId, harness.ConversationId),
            Ct
        );

        Assert.NotNull(runtime);
        Assert.Single(server.Requests);
    }

    internal static bool HasText(AgwMessage message, string text) =>
        message.Contents.OfType<AgwTextContent>().Any(content => content.Content == text);

    internal static bool IsResult(AgwMessage message) => AgwMessageClassifier.IsResult(message);

    private static A2AOriginCredentialsRequest Credentials(Uri url, string? authorization) =>
        new(url.GetLeftPart(UriPartial.Authority), new() { ["Authorization"] = authorization });
}
