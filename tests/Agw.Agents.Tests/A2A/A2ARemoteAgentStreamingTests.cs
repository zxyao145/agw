using global::A2A;
using Agw.Agents.Execution.Agents.History;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests.A2A;

public sealed class A2ARemoteAgentStreamingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(ProtocolBindingNames.HttpJson)]
    [InlineData(ProtocolBindingNames.JsonRpc)]
    public async Task RunStreaming_MessageReply_EmitsAssistantAndResult(string binding)
    {
        await using var server = await StartAsync(ScriptedAgentHandler.Echo(), binding);
        await using var client = new A2ARemoteAgentTestClient(server);

        var updates = await client.RunAsync("hello", Ct);

        Assert.Equal(2, updates.Count);
        Assert.Equal("hello", updates[0].Text);
        Assert.Equal(ChatRole.Assistant, updates[0].Role);
        Assert.IsType<Message>(updates[0].RawRepresentation);
        Assert.True(A2ARemoteAgentTestClient.IsResult(updates[1]));
        Assert.Equal("hello", updates[1].Text);
        Assert.Equal(updates[0].MessageId, updates[1].AdditionalProperties!["resultSourceMessageId"]);
        Assert.NotEmpty(client.Session.ContextId!);
        Assert.Null(client.Session.TaskId);
        if (binding == ProtocolBindingNames.JsonRpc)
        {
            var request = Assert.Single(server.Requests);
            Assert.Contains("\"jsonrpc\":\"2.0\"", request.Body);
            Assert.Equal("/agent", request.PathAndQuery);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunStreaming_ArtifactUpdates_UseStableIdsForAppends(bool replaces)
    {
        var handler = new ScriptedAgentHandler(
            async (context, queue, ct) =>
            {
                var updater = new TaskUpdater(queue, context.TaskId, context.ContextId);
                await updater.SubmitAsync(ct);
                await updater.StartWorkAsync(Status(context, "searching"), ct);
                await updater.AddArtifactAsync(
                    [Part.FromText("a")],
                    "artifact",
                    lastChunk: replaces,
                    cancellationToken: ct
                );
                await updater.AddArtifactAsync(
                    [Part.FromText("b")],
                    "artifact",
                    lastChunk: false,
                    append: !replaces,
                    cancellationToken: ct
                );
                await updater.AddArtifactAsync([Part.FromText("c")], "artifact", append: true, cancellationToken: ct);
                await updater.CompleteAsync(cancellationToken: ct);
            }
        );
        await using var server = await StartAsync(handler, ProtocolBindingNames.JsonRpc);
        await using var client = new A2ARemoteAgentTestClient(server);

        var updates = await client.RunAsync("go", Ct);

        var artifacts = updates
            .Where(update =>
                update.RawRepresentation is TaskArtifactUpdateEvent && !A2ARemoteAgentTestClient.IsResult(update)
            )
            .ToArray();
        Assert.Equal(3, artifacts.Length);
        Assert.Equal($"{client.Session.TaskId}:artifact", artifacts[0].MessageId);
        Assert.Equal(replaces ? $"{artifacts[0].MessageId}#1" : artifacts[0].MessageId, artifacts[1].MessageId);
        Assert.Equal(artifacts[1].MessageId, artifacts[2].MessageId);
        Assert.Contains(
            updates.SelectMany(update => update.Contents),
            content => content is TextReasoningContent { Text: "searching" }
        );
        Assert.Equal("abc", Assert.Single(updates, A2ARemoteAgentTestClient.IsResult).Text);
        Assert.Equal(TaskState.Completed, client.Session.TaskState);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunStreaming_NextInput_ContinuesOnlyWhenRemoteRequiresIt(bool inputRequired)
    {
        var handler = new ScriptedAgentHandler(
            async (context, queue, ct) =>
            {
                var updater = new TaskUpdater(queue, context.TaskId, context.ContextId);
                if (inputRequired && context.UserText == "go")
                {
                    await updater.SubmitAsync(ct);
                    await updater.RequireInputAsync(Status(context, "Which city?"), ct);
                }
                else
                {
                    await updater.CompleteAsync(Status(context, "done"), ct);
                }
            }
        );
        await using var server = await StartAsync(handler);
        await using var client = new A2ARemoteAgentTestClient(server);

        var first = await client.RunAsync("go", Ct);
        var remoteContext = client.Session.ContextId;
        var task = client.Session.TaskId;
        Assert.Equal(inputRequired ? TaskState.InputRequired : TaskState.Completed, client.Session.TaskState);
        Assert.Equal(
            inputRequired ? "Which city?" : "done",
            Assert.Single(first, A2ARemoteAgentTestClient.IsResult).Text
        );
        await client.RunAsync("Paris", Ct);

        var second = A2ARemoteAgentTestClient.ReadInput(server.Requests[1]);
        Assert.Equal(remoteContext, second.ContextId);
        Assert.Equal(inputRequired ? task : null, second.TaskId);
    }

    [Fact]
    public async Task RunStreaming_MultipleInputMessages_MergesPartsIntoOneMessage()
    {
        await using var server = await StartAsync(ScriptedAgentHandler.Echo());
        await using var client = new A2ARemoteAgentTestClient(server);
        var input = new[] { new ChatMessage(ChatRole.System, "prefix"), new ChatMessage(ChatRole.User, "question") };

        await client.Agent.RunStreamingAsync(input, client.Session, cancellationToken: Ct).ToListAsync(Ct);

        var message = A2ARemoteAgentTestClient.ReadInput(Assert.Single(server.Requests));
        Assert.Equal(Role.User, message.Role);
        Assert.Equal(["prefix", "question"], message.Parts.Select(part => part.Text));
    }

    [Fact]
    public async Task RunStreaming_NoConvertibleContent_ThrowsInvalidParam()
    {
        await using var server = await StartAsync(ScriptedAgentHandler.Echo());
        await using var client = new A2ARemoteAgentTestClient(server);
        var input = new ChatMessage(ChatRole.User, [new UsageContent(new UsageDetails())]);

        var error = await Assert.ThrowsAsync<AgwException>(async () =>
            await client.Agent.RunStreamingAsync(input, client.Session, cancellationToken: Ct).ToListAsync(Ct)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData(TaskState.Failed)]
    [InlineData(TaskState.Rejected)]
    [InlineData(TaskState.Canceled)]
    public async Task RunStreaming_RemoteTaskFailure_ThrowsTaskFailed(TaskState state)
    {
        var handler = new ScriptedAgentHandler(
            async (context, queue, ct) =>
            {
                var updater = new TaskUpdater(queue, context.TaskId, context.ContextId);
                if (state == TaskState.Failed)
                    await updater.FailAsync(Status(context, "quota exceeded"), ct);
                else if (state == TaskState.Rejected)
                    await updater.RejectAsync(Status(context, "rejected"), ct);
                else
                    await updater.CancelAsync(ct);
            }
        );
        await using var server = await StartAsync(handler);
        await using var client = new A2ARemoteAgentTestClient(server);

        var error = await ReadFailureAsync(client);

        Assert.Equal(ErrorCodes.A2ARemoteTaskFailed.Code, error.Code);
        Assert.Equal(state, client.Session.TaskState);
        if (state == TaskState.Failed)
            Assert.Contains("quota exceeded", error.Message);
    }

    [Fact]
    public async Task RunStreaming_Unauthorized_ThrowsUnauthorized()
    {
        await using var server = await A2ATestAgentServer.StartAsync(
            new(ProtocolBindingNames.HttpJson, true, ScriptedAgentHandler.Echo())
            {
                RequiredInterfaceHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer valid" },
            },
            Ct
        );
        await using var client = new A2ARemoteAgentTestClient(server);

        var error = await ReadFailureAsync(client);

        Assert.Equal(ErrorCodes.A2ARemoteAgentUnauthorized.Code, error.Code);
        Assert.Contains("401", error.Message);
        var cause = Assert.IsAssignableFrom<Exception>(error.InnerException);
        Assert.Contains("A2AOriginCredentialHandler", cause.StackTrace);
    }

    [Fact]
    public async Task RunStreaming_NoEvents_ThrowsRequestFailed()
    {
        await using var server = await StartAsync(new ScriptedAgentHandler((_, _, _) => Task.CompletedTask));
        await using var client = new A2ARemoteAgentTestClient(server);

        Assert.Equal(ErrorCodes.A2ARemoteAgentRequestFailed.Code, (await ReadFailureAsync(client)).Code);
    }

    [Fact]
    public async Task RunStreaming_ContextIdChanged_ThrowsRequestFailed()
    {
        var handler = new ScriptedAgentHandler(
            (_, queue, ct) => new MessageResponder(queue, "unexpected").ReplyAsync("hello", ct).AsTask()
        );
        await using var server = await StartAsync(handler);
        await using var client = new A2ARemoteAgentTestClient(server);
        client.Session.BindEndpointRevision(client.Revision);
        client.Session.RecordContext("old");

        Assert.Equal(ErrorCodes.A2ARemoteAgentRequestFailed.Code, (await ReadFailureAsync(client)).Code);
    }

    [Fact]
    public async Task RunStreaming_ContinuationTaskNotFound_ClearsRemoteState()
    {
        await using var server = await StartAsync(ScriptedAgentHandler.Echo(), ProtocolBindingNames.JsonRpc);
        await using var client = new A2ARemoteAgentTestClient(server);
        client.Session.BindEndpointRevision(client.Revision);
        client.Session.RecordContext("old");
        client.Session.RecordTask("missing", TaskState.InputRequired);

        var error = await ReadFailureAsync(client);

        Assert.Equal(ErrorCodes.A2ARemoteAgentRequestFailed.Code, error.Code);
        Assert.Contains("TaskNotFound", error.Message);
        var cause = Assert.IsAssignableFrom<Exception>(error.InnerException);
        Assert.Contains(nameof(A2AException), cause.Message);
        Assert.Contains(nameof(A2AClient), cause.StackTrace);
        Assert.Null(client.Session.ContextId);
        Assert.Null(client.Session.TaskId);
        Assert.Null(client.Session.TaskState);
        await client.RunAsync("new", Ct);
        var message = A2ARemoteAgentTestClient.ReadInput(server.Requests[1]);
        Assert.Null(message.ContextId);
        Assert.Null(message.TaskId);
    }

    [Fact]
    public async Task RunStreaming_ChangedInterfaceRevision_StartsNewContext()
    {
        await using var server = await StartAsync(ScriptedAgentHandler.Echo());
        await using var client = new A2ARemoteAgentTestClient(server);
        client.Session.BindEndpointRevision(Guid.NewGuid());
        client.Session.RecordContext("old");
        client.Session.RecordTask("old-task", TaskState.InputRequired);

        await client.RunAsync("new", Ct);

        var message = A2ARemoteAgentTestClient.ReadInput(Assert.Single(server.Requests));
        Assert.Null(message.ContextId);
        Assert.Null(message.TaskId);
        Assert.Equal(client.Revision, client.Session.InterfaceRevision);
    }

    [Fact]
    public void Map_UserRoleUpdate_IsExcludedFromModelHistory()
    {
        var adapter = new A2AMessageAdapter();

        Assert.True(ConversationHistoryMetadata.IsModelHistoryExcluded(adapter.Map(new(ChatRole.User, "user"))!));
        Assert.False(
            ConversationHistoryMetadata.IsModelHistoryExcluded(adapter.Map(new(ChatRole.Assistant, "assistant"))!)
        );
    }

    internal static Message Status(RequestContext context, string text) =>
        new()
        {
            MessageId = Guid.NewGuid().ToString("N"),
            ContextId = context.ContextId,
            TaskId = context.TaskId,
            Role = Role.Agent,
            Parts = [Part.FromText(text)],
        };

    internal static Task<A2ATestAgentServer> StartAsync(
        IAgentHandler handler,
        string binding = ProtocolBindingNames.HttpJson,
        bool streaming = true
    ) => A2ATestAgentServer.StartAsync(new(binding, streaming, handler), Ct);

    private static Task<AgwException> ReadFailureAsync(A2ARemoteAgentTestClient client) =>
        Assert.ThrowsAsync<AgwException>(async () =>
        {
            await foreach (var update in client.StreamAsync("go", Ct))
            {
                Assert.False(A2ARemoteAgentTestClient.IsResult(update));
            }
        });
}
