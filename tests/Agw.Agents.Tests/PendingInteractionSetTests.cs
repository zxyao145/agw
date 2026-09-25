using System.Text.Json;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.InProcess;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Tests;

public sealed class PendingInteractionSetTests
{
    private static readonly Guid TurnId = Guid.CreateVersion7();

    [Fact]
    public void Create_SameNodeAndRequest_ProducesStableInteractionId()
    {
        var first = InteractionIdentity.Create(TurnId, "node", 1, 2, "request-1", "call-1");
        var second = InteractionIdentity.Create(TurnId, "node", 1, 2, "request-1", "call-1");
        var otherNode = InteractionIdentity.Create(TurnId, "other", 1, 2, "request-1", "call-1");

        Assert.Equal(first, second);
        Assert.Equal(32, first.InteractionId.Length);
        Assert.NotEqual(first.InteractionId, otherNode.InteractionId);
        Assert.Equal(InteractionIdentity.ForProviderRequest("node", "request-1"), first.InteractionId);
    }

    [Fact]
    public async Task RegisterBatchAsync_SameBatchAgain_KeepsRoundCount()
    {
        var set = new InMemoryPendingInteractionSet(AgwPermissionMode.AlwaysAsk);
        var batch = Batch("batch-1", ("request-1", "call-1"), ("request-2", "call-2"));

        Assert.True(await set.RegisterBatchAsync(batch, TestContext.Current.CancellationToken));
        Assert.False(await set.RegisterBatchAsync(batch, TestContext.Current.CancellationToken));

        var snapshot = set.Snapshot();
        Assert.Equal(1, snapshot.ApprovalRounds);
        Assert.Equal(2, snapshot.Entries.Count);
        Assert.All(snapshot.Entries, entry => Assert.Equal(PendingInteractionStatus.Pending, entry.Status));
    }

    [Fact]
    public async Task RegisterBatchAsync_SameBatchWithDifferentRequest_ThrowsConflict()
    {
        var set = new InMemoryPendingInteractionSet(AgwPermissionMode.AlwaysAsk);
        await set.RegisterBatchAsync(Batch("batch-1", ("request-1", "call-1")), TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await set.RegisterBatchAsync(
                Batch("batch-1", ("request-1", "call-other")),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.DurableExecutionConflict.Code, exception.Code);
    }

    [Fact]
    public async Task RegisterBatchAsync_IdentityDiffersFromRequest_ThrowsConflict()
    {
        var set = new InMemoryPendingInteractionSet(AgwPermissionMode.AlwaysAsk);
        var identity = InteractionIdentity.Create(TurnId, "standalone", 0, 1, "request-1", "call-1");
        var batch = new InteractionBatch(
            "batch-1",
            [new InteractionBatchItem(identity, InteractionTestData.Tool("different-id", callId: "call-1"))]
        );

        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await set.RegisterBatchAsync(batch, TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.DurableExecutionConflict.Code, exception.Code);
    }

    [Fact]
    public async Task RegisterBatchAsync_BeyondRoundLimit_ThrowsAgentExecutionFailed()
    {
        var set = new InMemoryPendingInteractionSet(AgwPermissionMode.AlwaysAsk);
        for (var round = 0; round < PendingInteractionSet.MaxToolApprovalRounds; round++)
            await set.RegisterBatchAsync(
                Batch($"batch-{round}", ($"request-{round}", $"call-{round}")),
                TestContext.Current.CancellationToken
            );

        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await set.RegisterBatchAsync(
                Batch("batch-extra", ("request-x", "call-x")),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.AgentExecutionFailed.Code, exception.Code);
        Assert.Equal(PendingInteractionSet.MaxToolApprovalRounds, set.Snapshot().ApprovalRounds);
    }

    [Fact]
    public async Task TryResolveAsync_PartialThenComplete_CompletesWaitOnlyAfterLastAnswer()
    {
        var set = new InMemoryPendingInteractionSet(AgwPermissionMode.AlwaysAsk);
        var batch = Batch("batch-1", ("request-1", "call-1"), ("request-2", "call-2"));
        await set.RegisterBatchAsync(batch, TestContext.Current.CancellationToken);
        var wait = set.WaitForAnswersAsync("batch-1", TestContext.Current.CancellationToken).AsTask();

        var first = await set.TryResolveAsync(
            Approve(batch.Items[0].Identity.InteractionId),
            TestContext.Current.CancellationToken
        );
        Assert.NotNull(first);
        Assert.False(first.BatchComplete);
        Assert.False(wait.IsCompleted);

        var repeated = await set.TryResolveAsync(
            Approve(batch.Items[0].Identity.InteractionId),
            TestContext.Current.CancellationToken
        );
        Assert.Null(repeated);

        var second = await set.TryResolveAsync(
            Approve(batch.Items[1].Identity.InteractionId),
            TestContext.Current.CancellationToken
        );
        Assert.NotNull(second);
        Assert.True(second.BatchComplete);
        Assert.True(await wait);
        Assert.Equal(1, set.Snapshot().ApprovalRounds);
    }

    [Fact]
    public async Task TryResolveAsync_AllowSameArguments_NormalizesApprovalScope()
    {
        var set = new InMemoryPendingInteractionSet(AgwPermissionMode.AllowSameArguments);
        var batch = Batch("batch-1", ("request-1", "call-1"));
        await set.RegisterBatchAsync(batch, TestContext.Current.CancellationToken);

        var result = await set.TryResolveAsync(
            new ToolApprovalDecision
            {
                InteractionId = batch.Items[0].Identity.InteractionId,
                Approved = true,
                Scope = ApprovalScope.AlwaysTool,
            },
            TestContext.Current.CancellationToken
        );

        var decision = Assert.IsType<ToolApprovalDecision>(result?.Entry.Response);
        Assert.Equal(ApprovalScope.AlwaysArguments, decision.Scope);
    }

    [Fact]
    public async Task MarkConsumedAsync_UnansweredRequest_ThrowsConflict()
    {
        var set = new InMemoryPendingInteractionSet(AgwPermissionMode.AlwaysAsk);
        var batch = Batch("batch-1", ("request-1", "call-1"));
        await set.RegisterBatchAsync(batch, TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await set.MarkConsumedAsync([batch.Items[0].Identity.InteractionId], TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.DurableExecutionConflict.Code, exception.Code);
    }

    [Fact]
    public async Task Snapshot_RestoredSet_KeepsEntriesAndRounds()
    {
        var set = new InMemoryPendingInteractionSet(AgwPermissionMode.AlwaysAsk);
        var batch = Batch("batch-1", ("request-1", "call-1"));
        await set.RegisterBatchAsync(batch, TestContext.Current.CancellationToken);
        await set.TryResolveAsync(
            Approve(batch.Items[0].Identity.InteractionId),
            TestContext.Current.CancellationToken
        );
        await set.MarkConsumedAsync([batch.Items[0].Identity.InteractionId], TestContext.Current.CancellationToken);

        var restored = new InMemoryPendingInteractionSet(AgwPermissionMode.AlwaysAsk, set.Snapshot());

        Assert.Equal(set.Snapshot(), restored.Snapshot(), new SnapshotComparer());
        Assert.False(await restored.RegisterBatchAsync(batch, TestContext.Current.CancellationToken));
        Assert.Equal(1, restored.Snapshot().ApprovalRounds);
    }

    [Fact]
    public async Task RequestAsync_MatchingAnsweredInput_ReturnsSavedAnswer()
    {
        var (set, request, answer) = await AnsweredInputAsync();
        var channel = new ResolvedHumanInteractionChannel(set);

        var response = await channel.RequestAsync(request, TestContext.Current.CancellationToken);

        Assert.Same(answer, response);
    }

    [Theory]
    [InlineData("other-node", "call-1")]
    [InlineData("writer", "other-call")]
    public async Task RequestAsync_DifferentNodeOrCall_ThrowsConflict(string nodeId, string callId)
    {
        var (set, request, _) = await AnsweredInputAsync();
        var channel = new ResolvedHumanInteractionChannel(set);

        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await channel.RequestAsync(
                request with
                {
                    Source = request.Source with { NodeId = nodeId, CallId = callId },
                },
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.DurableExecutionConflict.Code, exception.Code);
    }

    [Fact]
    public async Task RequestAsync_PendingInput_ThrowsConflict()
    {
        var set = new InMemoryPendingInteractionSet(AgwPermissionMode.FullAccess);
        var (identity, input, request) = Input();
        await set.RegisterBatchAsync(
            new InteractionBatch("batch-1", [new InteractionBatchItem(identity, input)]),
            TestContext.Current.CancellationToken
        );
        var channel = new ResolvedHumanInteractionChannel(set);

        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await channel.RequestAsync(request, TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.DurableExecutionConflict.Code, exception.Code);
    }

    private static async Task<(
        InMemoryPendingInteractionSet Set,
        UserInputRequest Request,
        UserInputResponse Answer
    )> AnsweredInputAsync()
    {
        var set = new InMemoryPendingInteractionSet(AgwPermissionMode.FullAccess);
        var (identity, input, request) = Input();
        await set.RegisterBatchAsync(
            new InteractionBatch("batch-1", [new InteractionBatchItem(identity, input)]),
            TestContext.Current.CancellationToken
        );
        var answer = new UserInputResponse
        {
            InteractionId = identity.InteractionId,
            Cancelled = false,
            ResponseData = JsonSerializer.SerializeToElement(new { answer = "yes" }),
        };
        var result = await set.TryResolveAsync(answer, TestContext.Current.CancellationToken);
        return (set, request, Assert.IsType<UserInputResponse>(result?.Entry.Response));
    }

    private static (InteractionIdentity Identity, UserInputInteraction Input, UserInputRequest Request) Input()
    {
        var identity = InteractionIdentity.Create(TurnId, "writer", 0, 1, "request-1", "call-1");
        var payload = JsonSerializer.SerializeToElement(new { question = "Continue?" });
        var source = new InteractionSource
        {
            NodeId = "writer",
            ToolName = "ask_user_question",
            CallId = "call-1",
            ProviderRequestId = "request-1",
        };
        return (
            identity,
            new UserInputInteraction
            {
                InteractionId = identity.InteractionId,
                Prompt = "Continue?",
                Source = source,
                InputKind = "questions",
                Payload = payload,
            },
            new UserInputRequest("questions", "Continue?", payload) { Source = source }
        );
    }

    private static InteractionBatch Batch(string batchId, params (string RequestId, string CallId)[] requests) =>
        new(
            batchId,
            requests
                .Select(request =>
                {
                    var identity = InteractionIdentity.Create(
                        TurnId,
                        "standalone",
                        0,
                        1,
                        request.RequestId,
                        request.CallId
                    );
                    return new InteractionBatchItem(
                        identity,
                        InteractionTestData.Tool(identity.InteractionId, callId: request.CallId)
                    );
                })
                .ToArray()
        );

    private static ToolApprovalDecision Approve(string interactionId) =>
        new() { InteractionId = interactionId, Approved = true };

    private sealed class SnapshotComparer : IEqualityComparer<PendingInteractionSnapshot>
    {
        public bool Equals(PendingInteractionSnapshot? x, PendingInteractionSnapshot? y) =>
            x != null && y != null && x.ApprovalRounds == y.ApprovalRounds && x.Entries.SequenceEqual(y.Entries);

        public int GetHashCode(PendingInteractionSnapshot obj) => obj.ApprovalRounds;
    }
}
