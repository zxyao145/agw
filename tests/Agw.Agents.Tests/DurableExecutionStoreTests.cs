using System.Text.Json;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Agentflows.Checkpoints.Durable;
using Agw.Agents.Execution.Configuration;
using Agw.Agents.Execution.HumanInteraction.Durable.Contracts;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Messaging.Durable;
using Agw.Agents.Execution.Outbound.Durable;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Runtimes.Durable;
using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Agents.Execution.Turns;
using Agw.Projects.Contracts.Execution;
using Agw.Projects.Contracts.Runtime;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Agw.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Tests;

/// <summary>
/// Durable 执行状态机的测试：记录经真实的受理事务写入，Segment 结果经领取的租约与写入入口提交。
/// Durable execution state machine tests: records are written by the real acceptance transaction and segment results commit through a claimed lease and its write guard.
/// </summary>
public sealed partial class DurableExecutionStoreTests : IAsyncLifetime
{
    private const string UserId = TurnPersistenceTestKit.UserId;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);

    private readonly IDisposable _user = TurnPersistenceTestKit.EnterUser();
    private readonly ManualTimeProvider _clock = new();
    private readonly Guid _agentId = Guid.CreateVersion7();
    private TurnPersistenceTestKit _kit = null!;

    public async ValueTask InitializeAsync() => _kit = await TurnPersistenceTestKit.CreateAsync(_clock);

    public async ValueTask DisposeAsync()
    {
        _user.Dispose();
        await _kit.DisposeAsync();
    }

    private DurableExecutionStore Store => _kit.ResolveScoped<DurableExecutionStore>();

    [Theory]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("WaitingForHuman")]
    public async Task ApplySegmentResultAsync_ReclaimedLease_RejectsOldInstance(string outcome)
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var id = await RegisterAsync();
        var oldLease = await ClaimAsync(id, "worker-a");
        _clock.Advance(LeaseDuration + TimeSpan.FromSeconds(1));
        var newLease = await ClaimAsync(id, "worker-b");
        using var oldOwnership = new CancellationTokenSource();
        var result = new DurableExecutionSegmentResult
        {
            ExecutionId = id,
            SegmentIndex = 0,
            Status = Enum.Parse<DurableExecutionSegmentStatus>(outcome),
            PendingInteractions = [InteractionTestData.Input("request-1")],
        };

        // Act
        var exception = await Assert.ThrowsAsync<AgwException>(() => ApplyAsync(oldLease, result, oldOwnership));

        // Assert
        Assert.Equal(ErrorCodes.DurableExecutionConflict.Code, exception.Code);
        Assert.True(oldOwnership.IsCancellationRequested);
        Assert.Equal(2, newLease.Epoch);
        Assert.Equal(DurableExecutionStatus.Running, (await Store.GetAsync(id, token)).Status);
        var accepted = await ApplyAsync(newLease, result);
        Assert.NotEqual(DurableExecutionStatus.Running, accepted.Status);
    }

    [Fact]
    public async Task AcceptAsync_SameTurn_IsIdempotent()
    {
        // Arrange
        var task = await _kit.SeedConversationAsync();
        var turnId = Guid.CreateVersion7();

        // Act
        var first = await AcceptAsync(task, turnId);
        var second = await AcceptAsync(task, turnId);

        // Assert
        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Null(second.Request.Lease);
        Assert.Equal(DurableExecutionStatus.Queued, (await _kit.ReadExecutionAsync(turnId)).Status);
        var start = Assert.Single(await _kit.ReadEventsAsync(turnId));
        Assert.Equal(1, start.TurnSequence);
    }

    [Fact]
    public async Task AcceptAsync_FullAccessJob_ReloadPreservesPermissions()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var task = await _kit.SeedConversationAsync();
        var settings = TurnPersistenceTestKit
            .CreateSettings(task.ProjectId, task.ContextId)
            .WithPermissionMode(AgwPermissionMode.FullAccess)
            .WithHumanInteractionPolicy(HumanInteractionPolicy.Reject);
        var accepted = await AcceptAsync(task, settings: settings);

        // Act
        var restored = await Store.GetAsync(accepted.Request.TurnId, token);

        // Assert
        Assert.Equal(AgwPermissionMode.FullAccess, restored.Manifest.Settings.PermissionMode);
        Assert.Equal(HumanInteractionPolicy.Reject, restored.Manifest.Settings.HumanInteractionPolicy);
    }

    [Fact]
    public async Task AcceptAsync_ExplicitOwner_PersistsOwnerAndScope()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var task = await _kit.SeedConversationAsync();

        // Act
        var accepted = await AcceptAsync(task);

        // Assert
        var snapshot = await Store.GetAsync(accepted.Request.TurnId, token);
        Assert.Equal(UserId, snapshot.Manifest.UserId);
        var record = await _kit.ReadExecutionAsync(accepted.Request.TurnId);
        Assert.Equal(UserId, record.UserId);
        Assert.Equal(task.ProjectId, record.ProjectId);
        Assert.Equal(task.ProjectConversationId, record.ProjectConversationId);
        Assert.True(record.ScopeBackfilled);
        Assert.Equal(UserId, record.CreateBy);
        Assert.Equal(UserId, record.UpdateBy);
        Assert.Equal(1, record.LastEventSequence);
        Assert.Empty(CreateManifest().UserId);
    }

    [Fact]
    public async Task GetAuthorizedAsync_DifferentUserId_ReturnsNotFound()
    {
        // Arrange
        var id = await RegisterAsync();

        // Act
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            Store.GetAuthorizedAsync(id, "another-user", TestContext.Current.CancellationToken)
        );

        // Assert
        Assert.Equal(ErrorCodes.DurableExecutionNotFound.Code, exception.Code);
    }

    [Fact]
    public async Task GetAuthorizedOutcomeAsync_QueuedState_DoesNotDeserializeManifest()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var id = await RegisterAsync();
        await using (var context = _kit.CreateContext())
        {
            var record = await context.DurableExecutions.SingleAsync(item => item.Id == id, token);
            record.ManifestJson = "not-a-valid-manifest";
            await context.SaveChangesAsync(token);
        }

        // Act
        var outcome = await Store.GetAuthorizedOutcomeAsync(id, UserId, token);

        // Assert
        Assert.Equal(id, outcome.ExecutionId);
        Assert.Equal(DurableExecutionStatus.Queued, outcome.Status);
        Assert.Null(outcome.ErrorMessage);
    }

    [Fact]
    public async Task GetAuthorizedOutcomeAsync_FailedState_LoadsDecryptedError()
    {
        // Arrange
        var id = await RegisterAsync();
        var lease = await ClaimAsync(id);
        await ApplyAsync(
            lease,
            new DurableExecutionSegmentResult
            {
                ExecutionId = id,
                SegmentIndex = 0,
                Status = DurableExecutionSegmentStatus.Failed,
                ErrorMessage = "boom",
            }
        );

        // Act
        var outcome = await Store.GetAuthorizedOutcomeAsync(id, UserId, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(DurableExecutionStatus.Failed, outcome.Status);
        Assert.Equal("boom", outcome.ErrorMessage);
    }

    [Fact]
    public async Task SegmentState_WaitingAndResponse_RestoresNextSegmentInput()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var id = await RegisterAsync();
        var lease = await ClaimAsync(id);
        var checkpoint = new DurableAgentflowCheckpoint
        {
            SessionId = "workflow-session",
            CheckpointId = "checkpoint-1",
            Payload = JsonSerializer.SerializeToElement(new { step = 1 }),
        };

        // Act
        var waiting = await ApplyAsync(
            lease,
            new DurableExecutionSegmentResult
            {
                ExecutionId = id,
                SegmentIndex = 0,
                Status = DurableExecutionSegmentStatus.WaitingForHuman,
                PendingInteractions = [InteractionTestData.Input("request-1")],
                Checkpoint = checkpoint,
            }
        );
        var resuming = await Store.SubmitHumanResponseAsync(
            new SubmitDurableHumanResponseRequest(
                id,
                new UserInputResponse
                {
                    InteractionId = "request-1",
                    Cancelled = false,
                    ResponseData = JsonSerializer.SerializeToElement(new { answer = "blue" }),
                }
            ),
            UserId,
            token
        );
        await ClaimAsync(id);
        var resumed = Assert.IsType<DurableExecutionSnapshot>(await Store.LoadClaimedAsync(id, token));

        // Assert
        Assert.Equal(DurableExecutionStatus.WaitingForHuman, waiting.Status);
        Assert.Equal(1, waiting.SegmentIndex);
        Assert.Single(waiting.GetUnansweredInteractions());
        Assert.Equal(DurableExecutionStatus.Resuming, resuming.Status);
        Assert.Empty(resuming.GetUnansweredInteractions());
        var input = resumed.CreateSegmentInput();
        Assert.Equal(1, input.SegmentIndex);
        Assert.Equal("checkpoint-1", input.Checkpoint?.CheckpointId);
        var resolved = Assert.Single(input.ResolvedInteractions);
        Assert.Equal("request-1", resolved.Request.InteractionId);
        Assert.Equal("blue", ((UserInputResponse)resolved.Response).ResponseData?.GetProperty("answer").GetString());
    }

    [Fact]
    public async Task CheckpointStore_SeededFromActivityResult_RestoresPayload()
    {
        // Arrange
        var sessionId = $"durable-{Guid.CreateVersion7():N}";
        var firstStore = new DurableAgentflowCheckpointStore();
        var first = await firstStore.CreateCheckpointAsync(
            sessionId,
            JsonSerializer.SerializeToElement(new { step = 1 })
        );
        var second = await firstStore.CreateCheckpointAsync(
            sessionId,
            JsonSerializer.SerializeToElement(new { step = 2 }),
            first
        );

        // Act
        var restoredStore = new DurableAgentflowCheckpointStore(firstStore.Latest);
        var index = (await restoredStore.RetrieveIndexAsync(sessionId)).ToArray();
        var payload = await restoredStore.RetrieveCheckpointAsync(sessionId, second);

        // Assert
        Assert.Single(index);
        Assert.Equal(second.CheckpointId, index[0].CheckpointId);
        Assert.Equal(2, payload.GetProperty("step").GetInt32());
        Assert.Equal(first.CheckpointId, restoredStore.Latest?.ParentCheckpointId);
    }

    [Fact]
    public async Task InterruptAsync_RunningSegment_CommitsFinishAndRejectsLaterResult()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var accepted = await AcceptAsync();
        var id = accepted.Request.TurnId;
        var lease = await ClaimAsync(id);
        using var ownership = new CancellationTokenSource();

        // Act
        var interrupted = await _kit.Coordinator.InterruptAsync(id, UserId, reason: null, token);
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            ApplyAsync(
                lease,
                new DurableExecutionSegmentResult
                {
                    ExecutionId = id,
                    SegmentIndex = 0,
                    Status = DurableExecutionSegmentStatus.Completed,
                },
                ownership
            )
        );

        // Assert
        Assert.True(interrupted);
        Assert.Equal(ErrorCodes.DurableExecutionConflict.Code, exception.Code);
        Assert.True(ownership.IsCancellationRequested);
        var persisted = await Store.GetAsync(id, token);
        Assert.Equal(DurableExecutionStatus.Interrupted, persisted.Status);
        Assert.Equal(
            accepted.Request.Envelope.StreamingScopeId,
            DurableExecutionCoordinator.ToStatus(persisted).StreamingScopeId
        );
        Assert.Equal(ProjectConversationTurnStatus.Interrupted, (await _kit.ReadTurnAsync(id)).Status);
        var events = await _kit.ReadEventsAsync(id);
        Assert.Equal([1L, 2L], events.Select(item => item.TurnSequence));
        var finished = DurableExecutionEvents.ToEntry(events[^1]).Message;
        Assert.True(AgwMessageClassifier.IsTurnFinished(finished));
        Assert.True(AgwMessageClassifier.TryGetTurnFinishedStatus(finished, out var status));
        Assert.Equal(AgwTurnStatus.Interrupted, status);
    }

    [Fact]
    public async Task SubmitHumanResponseAsync_SameResponse_IsIdempotent()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var id = await RegisterAsync();
        await WaitForInteractionsAsync(id, [InteractionTestData.Input("request-1")]);
        var request = new SubmitDurableHumanResponseRequest(
            id,
            new UserInputResponse
            {
                InteractionId = "request-1",
                Cancelled = false,
                ResponseData = JsonSerializer.SerializeToElement(new { answer = "blue" }),
            }
        );

        // Act
        var first = await Store.SubmitHumanResponseAsync(request, UserId, token);
        var second = await Store.SubmitHumanResponseAsync(request, UserId, token);

        // Assert
        Assert.Equal(DurableExecutionStatus.Resuming, first.Status);
        Assert.Equal(DurableExecutionStatus.Resuming, second.Status);
        Assert.Single(second.Responses);
    }

    [Fact]
    public async Task GetClaimableAsync_QueuedExecution_IsReturned()
    {
        // Arrange
        var id = await RegisterAsync();

        // Act
        var candidates = await _kit.Leases.GetClaimableAsync(limit: 10, TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(id, candidates);
    }

    [Fact]
    public async Task GetClaimableAsync_WaitingExecution_IsNotReturned()
    {
        // Arrange
        var id = await RegisterAsync();
        await WaitForInteractionsAsync(id, [InteractionTestData.Input("request-1")]);

        // Act
        var candidates = await _kit.Leases.GetClaimableAsync(limit: 10, TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain(id, candidates);
    }

    [Fact]
    public async Task GetClaimableAsync_RunningLease_IsReturnedOnlyAfterExpiry()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var id = await RegisterAsync();
        await ClaimAsync(id);

        // Act
        var whileHeld = await _kit.Leases.GetClaimableAsync(limit: 10, token);
        _clock.Advance(LeaseDuration);
        var afterExpiry = await _kit.Leases.GetClaimableAsync(limit: 10, token);

        // Assert
        Assert.DoesNotContain(id, whileHeld);
        Assert.Contains(id, afterExpiry);
    }

    [Fact]
    public async Task InterruptAsync_TerminalExecution_ReturnsFalse()
    {
        // Arrange
        var id = await RegisterAsync();
        var lease = await ClaimAsync(id);
        await ApplyAsync(
            lease,
            new DurableExecutionSegmentResult
            {
                ExecutionId = id,
                SegmentIndex = 0,
                Status = DurableExecutionSegmentStatus.Completed,
            }
        );

        // Act
        var interrupted = await _kit.Coordinator.InterruptAsync(
            id,
            UserId,
            reason: null,
            TestContext.Current.CancellationToken
        );

        // Assert
        Assert.False(interrupted);
        Assert.Single(await _kit.ReadEventsAsync(id));
    }

    [Fact]
    public void CreateSegmentInput_MissingResponse_ThrowsConflict()
    {
        // Arrange
        var snapshot = new DurableExecutionSnapshot
        {
            Manifest = CreateManifest(),
            Status = DurableExecutionStatus.Resuming,
            SegmentIndex = 1,
            PendingInteractions = [InteractionTestData.Input("request-1")],
        };

        // Act
        var exception = Assert.Throws<AgwException>(snapshot.CreateSegmentInput);

        // Assert
        Assert.Equal(ErrorCodes.DurableExecutionConflict.Code, exception.Code);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ResolvedHumanInteractionChannel_RequestAsync_ReplaysMatchedResponse(
        bool approved,
        bool expectedCancelled
    )
    {
        // Arrange
        var responseData = JsonSerializer.SerializeToElement(new { answer = "blue" });
        var channel = ExecutionTestScopes.ResolvedChannel([
            new DurableResolvedInteraction(
                InteractionTestData.Input("request-1"),
                new UserInputResponse
                {
                    InteractionId = "request-1",
                    Cancelled = !approved,
                    ResponseData = responseData,
                }
            ),
        ]);
        var original = InteractionTestData.Input("request-1");
        var request = new UserInputRequest(original.InputKind, original.Prompt, original.Payload)
        {
            Source = original.Source,
        };

        // Act
        var response = await channel.RequestAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("request-1", response.InteractionId);
        Assert.Equal(expectedCancelled, response.Cancelled);
        Assert.Equal("blue", response.ResponseData?.GetProperty("answer").GetString());
    }

    [Fact]
    public void InteractionMessageMapper_Create_RecreatesQuestionPresentation()
    {
        // Act
        var message = InteractionMessageMapper.Create(
            InteractionTestData.Input("request-1"),
            "control-message",
            Guid.CreateVersion7(),
            "message-1"
        );

        // Assert
        Assert.Equal(AgwMessageTypes.InteractionRequest, message.AdditionalProperties?["type"]);
        Assert.Equal("request-1", InteractionTestData.Read(message).InteractionId);
        Assert.Equal("call-request-1", InteractionTestData.Read(message).Source.CallId);
        Assert.Equal("message-1", message.AdditionalProperties?["streamingScopeId"]);
        var payload = Assert.IsType<UserInputInteraction>(InteractionTestData.Read(message)).Payload;
        Assert.Equal("Color?", payload.GetProperty("questions")[0].GetProperty("question").GetString());
    }

    [Fact]
    public void TurnMessageFactory_CreateStarted_CarriesTurnIdentity()
    {
        // Arrange
        var envelope = new TurnEnvelope(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            AgentRuntimeType.Agentflow,
            "message-1"
        );

        // Act
        var message = TurnMessageFactory.CreateStarted(envelope);

        // Assert
        Assert.Equal(AgwMessageTypes.TurnStart, message.AdditionalProperties?["type"]);
        Assert.Equal(envelope.TurnId.ToString("D"), message.AdditionalProperties?[TurnMessageFactory.TurnIdKey]);
        Assert.Equal(envelope.ConversationId.ToString("D"), message.AdditionalProperties?["conversationId"]);
        Assert.Equal("agentflow", message.AdditionalProperties?["agentType"]);
        Assert.Equal("message-1", message.AdditionalProperties?["streamingScopeId"]);
    }

    [Fact]
    public async Task DurableEventSink_StateCommittedMessages_AreNotWritten()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var accepted = await AcceptAsync();
        var id = accepted.Request.TurnId;
        var lease = await ClaimAsync(id);
        using var ownership = new CancellationTokenSource();
        var sink = new DurableEventSink(
            _kit.Leases.CreateGuard(lease, ownership),
            lease,
            segmentIndex: 0,
            _kit.Broadcasts.GetOrCreate(id, UserId),
            _kit.Services.GetRequiredService<DurableExecutionEventLog>(),
            new ExecutionEventStreamOptions(),
            _clock,
            ownership.Token
        );

        // Act
        await using (sink)
        {
            await sink.WriteAsync(
                InteractionMessageMapper.Create(InteractionTestData.Input("request-1"), "control-message"),
                token
            );
            await sink.WriteAsync(
                TurnMessageFactory.CreateFinished(accepted.Request.Envelope, AgwTurnStatus.Completed, 0, null),
                token
            );
        }

        // Assert
        var start = Assert.Single(await _kit.ReadEventsAsync(id));
        Assert.Equal(1, start.TurnSequence);
    }

    [Fact]
    public async Task DurableEventSink_AdjacentTextDeltas_CommitAsOneEvent()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var accepted = await AcceptAsync();
        var id = accepted.Request.TurnId;
        var lease = await ClaimAsync(id);
        using var ownership = new CancellationTokenSource();
        var broadcast = _kit.Broadcasts.GetOrCreate(id, UserId);
        var sink = new DurableEventSink(
            _kit.Leases.CreateGuard(lease, ownership),
            lease,
            segmentIndex: 0,
            broadcast,
            _kit.Services.GetRequiredService<DurableExecutionEventLog>(),
            new ExecutionEventStreamOptions { WriteIntervalMilliseconds = 60_000 },
            _clock,
            ownership.Token
        );

        // Act
        await using (sink)
        {
            await sink.WriteAsync(TextDelta("message-1", "Hel"), token);
            await sink.WriteAsync(TextDelta("message-1", "lo"), token);
            await sink.WriteAsync(
                new AgwMessage("message-2", "agent", AiRole.Assistant, [new AgwFunctionCallContent { Content = "{}" }]),
                token
            );
            await sink.WriteAsync(TextDelta("message-1", "!"), token);
        }

        // Assert
        var events = await _kit.ReadEventsAsync(id);
        Assert.Equal([1L, 2L, 3L, 4L], events.Select(entry => entry.TurnSequence));
        Assert.Equal(["Hello", "", "!"], events.Skip(1).Select(entry => TextOf(entry.PayloadJson)));
        Assert.Equal(4, (await _kit.ReadExecutionAsync(id)).LastEventSequence);
        Assert.Equal(
            ["Hello", "", "!"],
            broadcast.ReadAfter(1, out _).Select(entry => TextOf(entry.Message)).ToArray()
        );
    }

    private static AgwMessage TextDelta(string messageId, string text) =>
        new(
            messageId,
            "agent",
            AiRole.Assistant,
            [
                new AgwTextContent
                {
                    Content = text,
                    AdditionalProperties = new AdditionalPropertiesDictionary { ["blockId"] = "block:0" },
                },
            ]
        );

    private static string TextOf(string payloadJson) => TextOf(JsonUtil.Deserialize<AgwMessage>(payloadJson)!);

    private static string TextOf(AgwMessage message) =>
        string.Concat(message.Contents.OfType<AgwTextContent>().Select(content => content.Content));

    /// <summary>
    /// 经受理事务登记一条 Queued 的 Durable 执行；本测试的协调器没有本地执行能力。
    /// Registers a Queued durable execution through the acceptance transaction; this test's coordinator has no local execution capability.
    /// </summary>
    private async Task<AcceptedTurn> AcceptAsync(
        AgentExecutionTask? task = null,
        Guid? turnId = null,
        ExecutionSettings? settings = null,
        IProjectRuntimeFacade? projects = null,
        AgentRuntimeType agentType = AgentRuntimeType.Agent,
        bool stream = true
    )
    {
        task ??= await _kit.SeedConversationAsync();
        return await _kit.CreateAcceptance(
                projectTasks: null,
                projects ?? new AgentExecutionFacadeTests.WorkspaceProjects(),
                _kit.Coordinator
            )
            .AcceptAsync(
                new TurnAcceptanceRequest(
                    UserId,
                    turnId,
                    new ExecutionTarget(_agentId, agentType),
                    task.ProjectConversationId,
                    TurnPersistenceTestKit.CreateInput("hello"),
                    settings ?? TurnPersistenceTestKit.CreateSettings(task.ProjectId, task.ContextId),
                    stream
                )
                {
                    Task = task,
                },
                TestContext.Current.CancellationToken
            );
    }

    private async Task<Guid> RegisterAsync(AgwPermissionMode? permissionMode = null)
    {
        var task = await _kit.SeedConversationAsync();
        var accepted = await AcceptAsync(
            task,
            settings: TurnPersistenceTestKit
                .CreateSettings(task.ProjectId, task.ContextId)
                .WithPermissionSnapshot(permissionMode, 0)
        );
        return accepted.Request.TurnId;
    }

    private async Task<DurableLease> ClaimAsync(Guid executionId, string workerId = "worker-a") =>
        Assert.IsType<DurableLease>(
            await _kit.Leases.TryClaimAsync(executionId, workerId, LeaseDuration, TestContext.Current.CancellationToken)
        );

    /// <summary>
    /// 在租约的写入入口中提交 Segment 结果，与 Worker 提交结果的事务一致。
    /// Commits a segment result through the lease's write guard, matching the transaction the worker commits results in.
    /// </summary>
    private async Task<DurableExecutionSnapshot> ApplyAsync(
        DurableLease lease,
        DurableExecutionSegmentResult result,
        CancellationTokenSource? ownershipLost = null
    )
    {
        using var ownership = new CancellationTokenSource();
        return await _kit
            .Leases.CreateGuard(lease, ownershipLost ?? ownership)
            .RunAsync(
                (services, token) =>
                    services.GetRequiredService<DurableExecutionStore>().ApplySegmentResultAsync(result, token),
                TestContext.Current.CancellationToken
            );
    }

    private static DurableExecutionManifest CreateManifest()
    {
        var projectId = Guid.CreateVersion7();
        var task = new AgentExecutionTask
        {
            TaskId = Guid.CreateVersion7(),
            ProjectConversationId = Guid.CreateVersion7(),
            ProjectId = projectId,
            ContextId = TurnPersistenceTestKit.ContextId,
            Title = "Durable test",
            CreateTime = TimeProvider.System.GetUtcNow(),
        };
        return new DurableExecutionManifest
        {
            ExecutionId = Guid.CreateVersion7(),
            AgentId = Guid.CreateVersion7(),
            AgentType = AgentRuntimeType.Agent,
            Input = TurnPersistenceTestKit.CreateInput("hello"),
            Task = DurableExecutionMapper.FromProjection(task),
            Settings = DurableExecutionMapper.FromSettings(TurnPersistenceTestKit.CreateSettings(projectId)),
        };
    }
}
