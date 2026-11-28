using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Definitions.Persistence;
using Agw.Agents.Execution.Agentflows.Checkpoints;
using Agw.Agents.Execution.Agentflows.Checkpoints.Durable;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Auth.Application;
using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Settings;
using Agw.Projects.Contracts.History;
using Agw.Settings;
using Agw.Settings.Application.Persistence;
using Agw.Shared.Contracts;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public sealed class AgentflowCheckpointStoreTests : IDisposable
{
    private readonly IDisposable _userScope = UserInfoUtil.Push(
        new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user-id")], authenticationType: "Test")
        )
    );

    public void Dispose() => _userScope.Dispose();

    [Fact]
    public async Task InProcessOccurrences_RepeatedNode_RestoreExactBoundary()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await database.SeedAsync();
        var store = database.CreateStore();
        var cancellationToken = TestContext.Current.CancellationToken;

        var first = await store.RecordAsync(
            fixture.SourceTurnId,
            fixture.ProjectId,
            fixture.ConversationId,
            fixture.ContextId,
            fixture.TaskId,
            fixture.AgentflowId,
            1,
            CreateRevisionSnapshot(),
            "user-id",
            isDurable: false,
            CreateCheckpoint("checkpoint-1"),
            new Dictionary<string, string> { ["checkpoint-node"] = "Review saved" },
            cancellationToken
        );
        Assert.NotNull(first);

        await database.AppendHistoryAsync(fixture, sequence: 2, "after first");
        var second = await store.RecordAsync(
            fixture.SourceTurnId,
            fixture.ProjectId,
            fixture.ConversationId,
            fixture.ContextId,
            fixture.TaskId,
            fixture.AgentflowId,
            1,
            CreateRevisionSnapshot(),
            "user-id",
            isDurable: false,
            CreateCheckpoint("checkpoint-2"),
            new Dictionary<string, string> { ["checkpoint-node"] = "Review saved" },
            cancellationToken
        );
        Assert.NotNull(second);
        Assert.NotEqual(first.Snapshot.OccurrenceId, second.Snapshot.OccurrenceId);

        var availability = await store.ListAsync(
            fixture.ProjectId,
            fixture.ContextId,
            fixture.AgentflowId,
            "user-id",
            new HashSet<Guid> { first.Snapshot.OccurrenceId, second.Snapshot.OccurrenceId },
            cancellationToken
        );
        Assert.Equal(2, availability.Count);
        Assert.All(availability, checkpoint => Assert.True(checkpoint.Available));

        var restored = await store.PrepareInProcessResumeAsync(
            first.Snapshot.OccurrenceId,
            fixture.ProjectId,
            fixture.ContextId,
            fixture.AgentflowId,
            "user-id",
            cancellationToken
        );

        Assert.Equal(first.Snapshot.BoundarySequence, restored.BoundarySequence);
        await using var context = database.CreateContext();
        Assert.Equal(
            [0L, first.Snapshot.BoundarySequence],
            await context
                .ProjectConversationChatHistories.OrderBy(item => item.ConversationSequence)
                .Select(item => item.ConversationSequence!.Value)
                .ToArrayAsync(cancellationToken)
        );
        var remaining = await context.AgentflowCheckpoints.SingleAsync(cancellationToken);
        Assert.Equal(first.Snapshot.OccurrenceId, remaining.Id);
    }

    [Fact]
    public async Task Record_ParallelMarkers_ShareOccurrenceAndBoundary()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await database.SeedAsync();
        var store = database.CreateStore();
        var cancellationToken = TestContext.Current.CancellationToken;

        var recorded = await store.RecordAsync(
            fixture.SourceTurnId,
            fixture.ProjectId,
            fixture.ConversationId,
            fixture.ContextId,
            fixture.TaskId,
            fixture.AgentflowId,
            1,
            CreateRevisionSnapshot(),
            "user-id",
            isDurable: false,
            CreateCheckpoint("checkpoint-1"),
            new Dictionary<string, string> { ["checkpoint-a"] = "Review A", ["checkpoint-b"] = "Review B" },
            cancellationToken
        );

        Assert.NotNull(recorded);
        Assert.Equal(2, recorded.Messages.Count);
        Assert.Equal(2, recorded.Snapshot.Markers.Count);
        Assert.All(
            recorded.Messages,
            message =>
                Assert.Equal(
                    recorded.Snapshot.OccurrenceId.ToString("D"),
                    message.AdditionalProperties!["checkpointOccurrenceId"]
                )
        );

        await using var context = database.CreateContext();
        var checkpoint = await context.AgentflowCheckpoints.SingleAsync(cancellationToken);
        Assert.Equal(recorded.Snapshot.OccurrenceId, checkpoint.Id);
        Assert.Equal(2, checkpoint.BoundarySequence);
        Assert.Equal("user-id", checkpoint.UserId);
        Assert.Equal("user-id", checkpoint.CreateBy);
        Assert.Equal("user-id", checkpoint.UpdateBy);
        var checkpointMessages = await context
            .ProjectConversationChatHistories.Where(item => item.ConversationSequence > 0)
            .ToArrayAsync(cancellationToken);
        Assert.Equal(2, checkpointMessages.Length);
        var checkpointSequences = await context
            .ProjectConversationChatHistories.Where(item => item.ConversationSequence > 0)
            .OrderBy(item => item.ConversationSequence)
            .Select(item => item.ConversationSequence!.Value)
            .ToArrayAsync(cancellationToken);
        Assert.Equal([1L, 2L], checkpointSequences);
    }

    [Theory]
    [InlineData("source-turn-missing", false)]
    [InlineData("source-execution-missing", false)]
    [InlineData("turn-snapshot-missing", false)]
    [InlineData("manifest-snapshot-missing", false)]
    [InlineData("snapshot-mismatch", false)]
    [InlineData("matching-snapshots", true)]
    public async Task GetCheckpoints_DurableSnapshotMissingOrMismatched_ReportsAvailability(
        string scenario,
        bool expectedAvailable
    )
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await database.SeedAsync();
        var store = database.CreateStore();
        var token = TestContext.Current.CancellationToken;
        await database.AddDurableExecutionAsync(fixture, fixture.SourceTurnId, DurableExecutionStatus.Completed);
        var recorded = await store.RecordAsync(
            fixture.SourceTurnId,
            fixture.ProjectId,
            fixture.ConversationId,
            fixture.ContextId,
            fixture.TaskId,
            fixture.AgentflowId,
            1,
            CreateRevisionSnapshot(),
            "user-id",
            isDurable: true,
            CreateCheckpoint("availability"),
            new Dictionary<string, string> { ["checkpoint-node"] = "Saved" },
            token
        );
        Assert.NotNull(recorded);

        await using (var context = database.CreateContext())
        {
            switch (scenario)
            {
                case "source-turn-missing":
                    await context
                        .ProjectConversationTurns.Where(turn => turn.Id == fixture.SourceTurnId)
                        .ExecuteDeleteAsync(token);
                    break;
                case "source-execution-missing":
                    await context
                        .DurableExecutions.Where(execution => execution.Id == fixture.SourceTurnId)
                        .ExecuteDeleteAsync(token);
                    break;
                case "turn-snapshot-missing":
                    await context.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE project_conversation_turn SET extra = '{{}}' WHERE id = {fixture.SourceTurnId}",
                        token
                    );
                    break;
                case "manifest-snapshot-missing":
                case "snapshot-mismatch":
                    var execution = await context.DurableExecutions.SingleAsync(
                        item => item.Id == fixture.SourceTurnId,
                        token
                    );
                    var manifest = System.Text.Json.Nodes.JsonNode.Parse(execution.ManifestJson)!;
                    if (scenario == "manifest-snapshot-missing")
                    {
                        Assert.True(manifest.AsObject().Remove("agentflowDependencies"));
                    }
                    else
                    {
                        manifest["agentflowDependencies"] = JsonSerializer.SerializeToNode(
                            new[]
                            {
                                new AgentflowDependencyRevision(
                                    Guid.CreateVersion7(),
                                    1,
                                    "nested-flow",
                                    null,
                                    Guid.CreateVersion7(),
                                    1
                                ),
                            },
                            new JsonSerializerOptions(JsonSerializerDefaults.Web)
                        );
                    }
                    execution.ManifestJson = manifest.ToJsonString();
                    await context.SaveChangesAsync(token);
                    break;
            }
        }

        var availability = await store.ListAsync(
            fixture.ProjectId,
            fixture.ContextId,
            fixture.AgentflowId,
            "user-id",
            inProcessOccurrences: null,
            token
        );

        Assert.Equal(expectedAvailable, Assert.Single(availability).Available);
    }

    [Fact]
    public async Task RecordAsync_MissingConversation_ReturnsNullWithoutPersistence()
    {
        // Arrange
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await database.SeedAsync();
        var store = database.CreateStore();
        var cancellationToken = TestContext.Current.CancellationToken;

        // Act
        var recorded = await store.RecordAsync(
            fixture.SourceTurnId,
            fixture.ProjectId,
            Guid.CreateVersion7(),
            fixture.ContextId,
            fixture.TaskId,
            fixture.AgentflowId,
            1,
            CreateRevisionSnapshot(),
            "user-id",
            isDurable: false,
            CreateCheckpoint("missing-conversation"),
            new Dictionary<string, string> { ["checkpoint-node"] = "Saved" },
            cancellationToken
        );

        // Assert
        Assert.Null(recorded);
        await using var context = database.CreateContext();
        Assert.Single(await context.ProjectConversationChatHistories.ToListAsync(cancellationToken));
        Assert.Empty(await context.AgentflowCheckpoints.ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task RecordAsync_CheckpointInsertFails_RollsBackConversationHistory()
    {
        // Arrange
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await database.SeedAsync();
        var store = database.CreateStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        await database.RejectCheckpointInsertsAsync(cancellationToken);

        // Act
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            store.RecordAsync(
                fixture.SourceTurnId,
                fixture.ProjectId,
                fixture.ConversationId,
                fixture.ContextId,
                fixture.TaskId,
                fixture.AgentflowId,
                1,
                CreateRevisionSnapshot(),
                "user-id",
                isDurable: false,
                CreateCheckpoint("rejected-checkpoint"),
                new Dictionary<string, string> { ["checkpoint-node"] = "Saved" },
                cancellationToken
            )
        );

        // Assert
        await using var context = database.CreateContext();
        Assert.Single(await context.ProjectConversationChatHistories.ToListAsync(cancellationToken));
        Assert.Empty(await context.AgentflowCheckpoints.ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task Resume_NewRevisionPublished_UsesSourceTurnSnapshot()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await database.SeedAsync();
        var store = database.CreateStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        var recorded = await store.RecordAsync(
            fixture.SourceTurnId,
            fixture.ProjectId,
            fixture.ConversationId,
            fixture.ContextId,
            fixture.TaskId,
            fixture.AgentflowId,
            1,
            CreateRevisionSnapshot(),
            "user-id",
            isDurable: false,
            CreateCheckpoint("checkpoint-1"),
            new Dictionary<string, string> { ["checkpoint-node"] = "Saved" },
            cancellationToken
        );
        Assert.NotNull(recorded);
        await database.AppendHistoryAsync(fixture, sequence: 2, "must remain");
        await database.PublishRevisionAsync(fixture.AgentflowId, revision: 2);

        var restored = await store.PrepareInProcessResumeAsync(
            recorded.Snapshot.OccurrenceId,
            fixture.ProjectId,
            fixture.ContextId,
            fixture.AgentflowId,
            "user-id",
            cancellationToken
        );

        Assert.Equal(1, restored.RevisionSnapshot.RootRevision);
        await using var context = database.CreateContext();
        Assert.Equal(2, await context.ProjectConversationChatHistories.CountAsync(cancellationToken));
        Assert.Equal(1, await context.AgentflowCheckpoints.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task DistributedResume_NestedRevisionPublished_UsesAcceptedSnapshotOnRetry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await database.SeedAsync();
        var nestedAgentflowId = Guid.CreateVersion7();
        var dependency = TestDatabase.CreateNestedDependency(fixture.AgentflowId, nestedAgentflowId);
        await database.SeedNestedDependencyAsync(fixture, dependency, cancellationToken);
        var store = database.CreateStore();
        var acceptedSnapshot = new AgentflowRevisionSnapshot(1, [dependency]);
        var recorded = await database.RecordDurableCheckpointAsync(store, fixture, acceptedSnapshot, cancellationToken);
        Assert.NotNull(recorded);
        await database.PublishRevisionAsync(nestedAgentflowId, revision: 2);

        var resumeTurnId = Guid.CreateVersion7();
        var restored = await database.ResumeDistributedAsync(
            store,
            fixture,
            recorded.Snapshot.OccurrenceId,
            resumeTurnId,
            cancellationToken
        );
        var retry = await database.ResumeDistributedAsync(
            store,
            fixture,
            recorded.Snapshot.OccurrenceId,
            resumeTurnId,
            cancellationToken
        );

        Assert.Equal(acceptedSnapshot.RootRevision, restored.RevisionSnapshot.RootRevision);
        Assert.Equal(acceptedSnapshot.Dependencies, restored.RevisionSnapshot.Dependencies);
        Assert.Equal(acceptedSnapshot.Dependencies, retry.RevisionSnapshot.Dependencies);
        await database.AssertResumedDependencySnapshotAsync(resumeTurnId, acceptedSnapshot, cancellationToken);
    }

    [Fact]
    public async Task DistributedResume_StartEvent_CarriesSourceRevision()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await database.SeedAsync();
        var store = database.CreateStore();
        var recorded = await database.RecordDurableCheckpointAsync(
            store,
            fixture,
            CreateRevisionSnapshot(),
            cancellationToken
        );
        Assert.NotNull(recorded);
        await database.PublishRevisionAsync(fixture.AgentflowId, revision: 2);

        var resumeTurnId = Guid.CreateVersion7();
        await database.ResumeDistributedAsync(
            store,
            fixture,
            recorded.Snapshot.OccurrenceId,
            resumeTurnId,
            cancellationToken
        );

        await using var context = database.CreateContext();
        var record = await context.DurableExecutionEvents.SingleAsync(
            item => item.TurnId == resumeTurnId && item.TurnSequence == 1,
            cancellationToken
        );
        Assert.Equal(AgwMessageTypes.TurnStart, record.MessageType);
        var message = DurableExecutionEvents.ToEntry(record).Message;
        Assert.Equal("1", message.AdditionalProperties![AgwMessageClassifier.AgentflowRevisionKey]?.ToString());
    }

    [Theory]
    [InlineData("source-turn-missing")]
    [InlineData("turn-snapshot-missing")]
    public async Task ResumeCheckpoint_MissingSourceTurnOrDependencySnapshot_FailsBeforeHistoryMutation(string scenario)
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await database.SeedAsync();
        var store = database.CreateStore();
        var token = TestContext.Current.CancellationToken;
        var checkpoint = await store.RecordAsync(
            fixture.SourceTurnId,
            fixture.ProjectId,
            fixture.ConversationId,
            fixture.ContextId,
            fixture.TaskId,
            fixture.AgentflowId,
            1,
            CreateRevisionSnapshot(),
            "user-id",
            isDurable: false,
            CreateCheckpoint("missing-snapshot"),
            new Dictionary<string, string> { ["checkpoint-node"] = "Saved" },
            token
        );
        Assert.NotNull(checkpoint);
        await database.AppendHistoryAsync(fixture, sequence: 2, "later message");

        await using (var context = database.CreateContext())
        {
            if (scenario == "source-turn-missing")
            {
                await context
                    .ProjectConversationTurns.Where(turn => turn.Id == fixture.SourceTurnId)
                    .ExecuteDeleteAsync(token);
            }
            else
            {
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE project_conversation_turn SET extra = '{{}}' WHERE id = {fixture.SourceTurnId}",
                    token
                );
            }
        }

        await using var verification = database.CreateContext();
        var historyCount = await verification.ProjectConversationChatHistories.CountAsync(token);
        var error = await Assert.ThrowsAsync<AgwException>(() =>
            store.PrepareInProcessResumeAsync(
                checkpoint.Snapshot.OccurrenceId,
                fixture.ProjectId,
                fixture.ContextId,
                fixture.AgentflowId,
                "user-id",
                token
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
        Assert.Equal(historyCount, await verification.ProjectConversationChatHistories.CountAsync(token));
        Assert.Equal(1, await verification.AgentflowCheckpoints.CountAsync(token));
        Assert.Empty(await verification.DurableExecutions.ToListAsync(token));
    }

    [Fact]
    public async Task ResumeCheckpoint_TurnAndManifestSnapshotDisagree_FailsBeforeHistoryMutation()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await database.SeedAsync();
        var store = database.CreateStore();
        var token = TestContext.Current.CancellationToken;
        await database.AddDurableExecutionAsync(fixture, fixture.SourceTurnId, DurableExecutionStatus.Completed);
        var checkpoint = await store.RecordAsync(
            fixture.SourceTurnId,
            fixture.ProjectId,
            fixture.ConversationId,
            fixture.ContextId,
            fixture.TaskId,
            fixture.AgentflowId,
            1,
            CreateRevisionSnapshot(),
            "user-id",
            isDurable: true,
            CreateCheckpoint("mismatched-snapshot"),
            new Dictionary<string, string> { ["checkpoint-node"] = "Saved" },
            token
        );
        Assert.NotNull(checkpoint);
        await database.AppendHistoryAsync(fixture, sequence: 2, "later message");

        await using (var context = database.CreateContext())
        {
            var execution = await context.DurableExecutions.SingleAsync(item => item.Id == fixture.SourceTurnId, token);
            var manifest = System.Text.Json.Nodes.JsonNode.Parse(execution.ManifestJson)!;
            manifest["agentflowDependencies"] = JsonSerializer.SerializeToNode(
                new[]
                {
                    new AgentflowDependencyRevision(
                        Guid.CreateVersion7(),
                        1,
                        "nested-flow",
                        null,
                        Guid.CreateVersion7(),
                        1
                    ),
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            );
            execution.ManifestJson = manifest.ToJsonString();
            await context.SaveChangesAsync(token);
        }

        await using var verification = database.CreateContext();
        var historyCount = await verification.ProjectConversationChatHistories.CountAsync(token);
        var error = await Assert.ThrowsAsync<AgwException>(() =>
            store.PrepareDistributedResumeAsync(
                checkpoint.Snapshot.OccurrenceId,
                Guid.CreateVersion7(),
                fixture.ProjectId,
                fixture.ContextId,
                fixture.AgentflowId,
                "user-id",
                token
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
        Assert.Equal(historyCount, await verification.ProjectConversationChatHistories.CountAsync(token));
        Assert.Equal(1, await verification.AgentflowCheckpoints.CountAsync(token));
        Assert.Single(await verification.DurableExecutions.ToListAsync(token));
    }

    [Fact]
    public async Task ResumeCheckpoint_PermissionValidationFailure_DoesNotChangeHistory()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await database.SeedAsync();
        var store = database.CreateStore();
        var token = TestContext.Current.CancellationToken;
        await database.AddDurableExecutionAsync(fixture, fixture.SourceTurnId, DurableExecutionStatus.Completed);
        var checkpoint = await store.RecordAsync(
            fixture.SourceTurnId,
            fixture.ProjectId,
            fixture.ConversationId,
            fixture.ContextId,
            fixture.TaskId,
            fixture.AgentflowId,
            1,
            CreateRevisionSnapshot(),
            "user-id",
            isDurable: true,
            CreateCheckpoint("permission-failure"),
            new Dictionary<string, string> { ["checkpoint-node"] = "Saved" },
            token
        );
        Assert.NotNull(checkpoint);
        await database.AppendHistoryAsync(fixture, sequence: 2, "later message");
        await using var verification = database.CreateContext();
        var historyCount = await verification.ProjectConversationChatHistories.CountAsync(token);

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            store.PrepareDistributedResumeAsync(
                checkpoint.Snapshot.OccurrenceId,
                Guid.CreateVersion7(),
                fixture.ProjectId,
                fixture.ContextId,
                fixture.AgentflowId,
                "user-id",
                token,
                validateSnapshot: static (_, _) => Task.FromException(new AgwException(ErrorCodes.InvalidParam))
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
        Assert.Equal(historyCount, await verification.ProjectConversationChatHistories.CountAsync(token));
        Assert.Equal(1, await verification.AgentflowCheckpoints.CountAsync(token));
        Assert.Single(await verification.DurableExecutions.ToListAsync(token));
    }

    [Fact]
    public async Task DistributedResume_IdempotentRetry_DoesNotRecoverUnrelatedRows()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await database.SeedAsync();
        var store = database.CreateStore();
        var sourceId = fixture.SourceTurnId;
        await database.AddDurableExecutionAsync(fixture, sourceId, DurableExecutionStatus.Completed);
        var recorded = await store.RecordAsync(
            sourceId,
            fixture.ProjectId,
            fixture.ConversationId,
            fixture.ContextId,
            fixture.TaskId,
            fixture.AgentflowId,
            1,
            CreateRevisionSnapshot(),
            "user-id",
            true,
            CreateCheckpoint("checkpoint-1"),
            new Dictionary<string, string> { ["checkpoint-node"] = "Saved" },
            token
        );
        Assert.NotNull(recorded);
        var resumeId = Guid.CreateVersion7();
        await store.PrepareDistributedResumeAsync(
            recorded.Snapshot.OccurrenceId,
            resumeId,
            fixture.ProjectId,
            fixture.ContextId,
            fixture.AgentflowId,
            "user-id",
            token
        );
        var unrelated = DurableExecutionScopeMaintenanceTests.CreateExecution(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "user-id",
            "broken"
        );
        await using (var seed = database.CreateContext())
        {
            seed.Add(unrelated);
            await seed.SaveChangesAsync(token);
        }
        database.ResetTransactionCount();

        // Act
        await store.PrepareDistributedResumeAsync(
            recorded.Snapshot.OccurrenceId,
            resumeId,
            fixture.ProjectId,
            fixture.ContextId,
            fixture.AgentflowId,
            "user-id",
            token
        );

        // Assert
        await using var context = database.CreateContext();
        var unchanged = await context
            .DurableExecutions.Where(row => row.Id == unrelated.Id)
            .Select(row => new { row.ScopeBackfilled, row.Status })
            .SingleAsync(token);
        Assert.False(unchanged.ScopeBackfilled);
        Assert.Equal(DurableExecutionStatus.Queued, unchanged.Status);
        Assert.Equal(0, database.TransactionCount);
    }

    [Theory]
    [InlineData("userId")]
    [InlineData("workspaceSnapshot")]
    public async Task DistributedResume_IncompleteManifest_RejectsBeforeChangingHistory(string field)
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await database.SeedAsync();
        var store = database.CreateStore();
        var token = TestContext.Current.CancellationToken;
        var sourceId = fixture.SourceTurnId;
        await database.AddDurableExecutionAsync(fixture, sourceId, DurableExecutionStatus.Completed);
        var recorded = await store.RecordAsync(
            sourceId,
            fixture.ProjectId,
            fixture.ConversationId,
            fixture.ContextId,
            fixture.TaskId,
            fixture.AgentflowId,
            1,
            CreateRevisionSnapshot(),
            "user-id",
            true,
            CreateCheckpoint("checkpoint-1"),
            new Dictionary<string, string> { ["checkpoint-node"] = "Saved" },
            token
        );
        Assert.NotNull(recorded);
        await database.AppendHistoryAsync(fixture, sequence: 2, "later message");
        await using var context = database.CreateContext();
        var original = await context.DurableExecutions.SingleAsync(token);
        var json = System.Text.Json.Nodes.JsonNode.Parse(original.ManifestJson)!;
        Assert.True(json.AsObject().Remove(field));
        original.ManifestJson = json.ToJsonString();
        await context.SaveChangesAsync(token);
        var count = await context.ProjectConversationChatHistories.CountAsync(token);

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            store.PrepareDistributedResumeAsync(
                recorded.Snapshot.OccurrenceId,
                Guid.CreateVersion7(),
                fixture.ProjectId,
                fixture.ContextId,
                fixture.AgentflowId,
                "user-id",
                token
            )
        );

        Assert.Equal(ErrorCodes.DurableExecutionConflict.Code, error.Code);
        Assert.Equal(count, await context.ProjectConversationChatHistories.CountAsync(token));
        Assert.Equal(1, await context.DurableExecutions.CountAsync(token));
    }

    [Fact]
    public async Task DistributedResume_SourceStillRunning_DoesNotDeleteHistory()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await database.SeedAsync();
        var store = database.CreateStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        var sourceTurnId = fixture.SourceTurnId;
        await database.AddDurableExecutionAsync(fixture, sourceTurnId, DurableExecutionStatus.Running);
        var recorded = await store.RecordAsync(
            sourceTurnId,
            fixture.ProjectId,
            fixture.ConversationId,
            fixture.ContextId,
            fixture.TaskId,
            fixture.AgentflowId,
            1,
            CreateRevisionSnapshot(),
            "user-id",
            isDurable: true,
            CreateCheckpoint("checkpoint-1"),
            new Dictionary<string, string> { ["checkpoint-node"] = "Saved" },
            cancellationToken
        );
        Assert.NotNull(recorded);
        await database.AppendHistoryAsync(fixture, sequence: 2, "must remain");

        await Assert.ThrowsAsync<AgwException>(() =>
            store.PrepareDistributedResumeAsync(
                recorded.Snapshot.OccurrenceId,
                Guid.CreateVersion7(),
                fixture.ProjectId,
                fixture.ContextId,
                fixture.AgentflowId,
                "user-id",
                cancellationToken
            )
        );

        await using var context = database.CreateContext();
        Assert.Equal(3, await context.ProjectConversationChatHistories.CountAsync(cancellationToken));
        Assert.Equal(1, await context.DurableExecutions.CountAsync(cancellationToken));
        Assert.Equal(1, await context.AgentflowCheckpoints.CountAsync(cancellationToken));
    }

    private static AgentflowRevisionSnapshot CreateRevisionSnapshot() => new(1, []);

    private static DurableAgentflowCheckpoint CreateCheckpoint(string checkpointId) =>
        new()
        {
            SessionId = "session-1",
            CheckpointId = checkpointId,
            Payload = JsonSerializer.SerializeToElement(new { checkpointId }),
        };

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task DistributedResume_CorruptExecution_DoesNotPoisonOtherCheckpoints(
        bool indexed,
        bool sameConversation,
        bool corruptCiphertext
    )
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await database.SeedAsync();
        var store = database.CreateStore();
        var sourceId = fixture.SourceTurnId;
        await database.AddDurableExecutionAsync(fixture, sourceId, DurableExecutionStatus.Completed);
        var checkpoint = await store.RecordAsync(
            sourceId,
            fixture.ProjectId,
            fixture.ConversationId,
            fixture.ContextId,
            fixture.TaskId,
            fixture.AgentflowId,
            1,
            CreateRevisionSnapshot(),
            "user-id",
            true,
            CreateCheckpoint("poison-test"),
            new Dictionary<string, string> { ["checkpoint-node"] = "Saved" },
            token
        );
        Assert.NotNull(checkpoint);
        var brokenId = Guid.CreateVersion7();
        await using (var seed = database.CreateContext())
        {
            seed.DurableExecutions.Add(
                new DurableExecutionRecord
                {
                    Id = brokenId,
                    UserId = "user-id",
                    ManifestJson = "broken-manifest",
                    ProjectId = indexed ? (sameConversation ? fixture.ProjectId : Guid.CreateVersion7()) : null,
                    ProjectConversationId = indexed
                        ? (sameConversation ? fixture.ConversationId : Guid.CreateVersion7())
                        : null,
                    ScopeBackfilled = indexed,
                    Status = DurableExecutionStatus.WaitingForHuman,
                    StateVersion = Guid.CreateVersion7(),
                    StateChangedAt = TimeProvider.System.GetUtcNow(),
                }
            );
            await seed.SaveChangesAsync(token);
            if (corruptCiphertext)
            {
                await seed.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE durable_execution SET manifest_json = 'agwenc:v1:corrupt' WHERE id = {brokenId}",
                    token
                );
            }
        }

        // Act
        var resumeId = Guid.CreateVersion7();
        await store.PrepareDistributedResumeAsync(
            checkpoint.Snapshot.OccurrenceId,
            resumeId,
            fixture.ProjectId,
            fixture.ContextId,
            fixture.AgentflowId,
            "user-id",
            token
        );

        // Assert
        await using var verification = database.CreateContext();
        var resumed = await verification
            .DurableExecutions.Where(row => row.Id == resumeId)
            .Select(row => new
            {
                row.Status,
                row.ProjectId,
                row.ProjectConversationId,
            })
            .SingleAsync(token);
        Assert.Equal(DurableExecutionStatus.Resuming, resumed.Status);
        Assert.Equal(fixture.ProjectId, resumed.ProjectId);
        Assert.Equal(fixture.ConversationId, resumed.ProjectConversationId);
        var broken = await verification
            .DurableExecutions.Where(row => row.Id == brokenId)
            .Select(row => new { row.Status, row.ScopeBackfilled })
            .SingleAsync(token);
        Assert.Equal(indexed, broken.ScopeBackfilled);
        Assert.Equal(
            indexed && sameConversation ? DurableExecutionStatus.Failed : DurableExecutionStatus.WaitingForHuman,
            broken.Status
        );
    }

    private sealed record Fixture(
        Guid ProjectId,
        Guid ConversationId,
        string ContextId,
        Guid TaskId,
        Guid AgentflowId,
        Guid SourceTurnId
    );

    private sealed class TestDatabase : IAsyncDisposable
    {
        public static AgentflowDependencyRevision CreateNestedDependency(
            Guid rootAgentflowId,
            Guid nestedAgentflowId
        ) => new(rootAgentflowId, 1, "nested-flow", null, nestedAgentflowId, 1);

        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AgwDbContext> _options;
        private readonly ServiceProvider _serviceProvider;
        private readonly CountingTransactionInterceptor _transactionInterceptor;

        private TestDatabase(
            SqliteConnection connection,
            DbContextOptions<AgwDbContext> options,
            ServiceProvider serviceProvider,
            CountingTransactionInterceptor transactionInterceptor
        )
        {
            _connection = connection;
            _options = options;
            _serviceProvider = serviceProvider;
            _transactionInterceptor = transactionInterceptor;
        }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var transactionInterceptor = new CountingTransactionInterceptor();
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(transactionInterceptor)
                .Options;
            await using (var context = new AgwDbContext(options))
            {
                await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            }

            var services = new ServiceCollection();
            services.AddScoped(_ => new AgwDbContext(options));
            services.AddScoped<DbContext>(serviceProvider => serviceProvider.GetRequiredService<AgwDbContext>());
            services.AddScoped<IAgentsDbContext>(serviceProvider => serviceProvider.GetRequiredService<AgwDbContext>());
            services.AddScoped<IAgentflowCheckpointPersistence, AgentflowCheckpointPersistence>();
            services.AddScoped<IConversationTurnStore>(provider => new ConversationTurnStore(
                provider.GetRequiredService<AgwDbContext>(),
                TimeProvider.System
            ));
            services.AddScoped<IAgentflowRevisionSnapshotReader, AgentflowRevisionSnapshotReader>();
            services.AddScoped<IDurableExecutionScopeMaintenance>(provider =>
                TestDurablePersistence.Create(provider.GetRequiredService<AgwDbContext>())
            );
            var dataRoot = Path.Combine(
                AppContext.BaseDirectory,
                ".scratch",
                "agentflow-checkpoint",
                Guid.CreateVersion7().ToString("N")
            );
            var extensionConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Extensions:Directory"] = Path.Combine(dataRoot, "extensions") }
                )
                .Build();
            services.AddSingleton(
                AgwDataPaths.Resolve(Path.Combine(dataRoot, "data"), dataRoot, Path.Combine(dataRoot, "logs"))
            );
            services.AddScoped<ISettingsDbContext>(provider => provider.GetRequiredService<AgwDbContext>());
            services.AddScoped<ISettingsPersistence, EfSettingsPersistence>();
            services.AddScoped<ICurrentUser, UserInfoService>();
            services.AddScoped<IUserInfoService, UserInfoService>();
            services.AddSettings();
            services.AddTestExtensions(extensionConfiguration);
            var serviceProvider = services.BuildServiceProvider();
            return new TestDatabase(connection, options, serviceProvider, transactionInterceptor);
        }

        public int TransactionCount => _transactionInterceptor.TransactionCount;

        public void ResetTransactionCount() => _transactionInterceptor.Reset();

        public AgwDbContext CreateContext() => new(_options);

        public AgentflowCheckpointStore CreateStore() =>
            new(
                _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                new InMemoryApplicationLock(),
                TimeProvider.System,
                NullLogger<AgentflowCheckpointStore>.Instance
            );

        public async Task<Fixture> SeedAsync()
        {
            var now = TimeProvider.System.GetUtcNow();
            var fixture = new Fixture(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                "context-1",
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7()
            );
            await using var context = CreateContext();
            context.Projects.Add(
                new Project
                {
                    Id = fixture.ProjectId,
                    Name = "Project",
                    Workspace = "/tmp",
                    CreateBy = "user-id",
                    CreateTime = now,
                }
            );
            context.ProjectConversations.Add(
                new ProjectConversation
                {
                    Id = fixture.ConversationId,
                    ProjectId = fixture.ProjectId,
                    ContextId = fixture.ContextId,
                    Title = "Conversation",
                    CreateBy = "user-id",
                    CreateTime = now,
                }
            );
            context.Agentflows.Add(
                new Agentflow
                {
                    Id = fixture.AgentflowId,
                    Name = "Flow",
                    SystemPrompt = "original",
                    LatestRevision = 1,
                    PublishedRevision = 1,
                    CreateBy = "user-id",
                    CreateTime = now,
                }
            );
            context.AgentflowRevisions.Add(
                new AgentflowRevision
                {
                    AgentflowId = fixture.AgentflowId,
                    Revision = 1,
                    CreateBy = "user-id",
                    CreateTime = now,
                }
            );
            context.AgentflowNodes.Add(
                new AgentflowNode
                {
                    AgentflowId = fixture.AgentflowId,
                    AgentflowRevision = 1,
                    NodeId = "checkpoint-node",
                    Kind = AgentflowNodeKind.CheckpointMarker,
                    Name = "Saved",
                    ConfigJson = "{\"checkpointName\":\"Saved\"}",
                    CreateTime = now,
                }
            );
            context.ProjectConversationChatHistories.Add(
                new ProjectConversationChatHistory
                {
                    Id = Guid.CreateVersion7(),
                    ConversationId = fixture.ConversationId,
                    TaskId = fixture.TaskId,
                    Status = TaskExecutionStatus.Succeeded,
                    ConversationSequence = 0,
                    ConversationPayload = "{}",
                    CreateTime = now,
                }
            );
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await new ConversationTurnStore(context, TimeProvider.System).AcceptAsync(
                new AcceptConversationTurnRequest
                {
                    TurnId = fixture.SourceTurnId,
                    ProjectId = fixture.ProjectId,
                    ContextId = fixture.ContextId,
                    ConversationId = fixture.ConversationId,
                    Generation = 0,
                    TaskId = fixture.TaskId,
                    TargetId = fixture.AgentflowId,
                    TargetType = ConversationTurnTargetType.Agentflow,
                    AgentflowRevision = 1,
                    AgentflowDependencies = [],
                },
                TestContext.Current.CancellationToken
            );
            return fixture;
        }

        public async Task SeedNestedDependencyAsync(
            Fixture fixture,
            AgentflowDependencyRevision dependency,
            CancellationToken cancellationToken
        )
        {
            await SeedNestedFlowAsync(dependency.TargetAgentflowId, cancellationToken);
            await AddNestedReferenceAsync(fixture, dependency, cancellationToken);
        }

        private async Task SeedNestedFlowAsync(Guid nestedAgentflowId, CancellationToken cancellationToken)
        {
            var now = TimeProvider.System.GetUtcNow();
            await using var context = CreateContext();
            context.Agentflows.Add(
                new Agentflow
                {
                    Id = nestedAgentflowId,
                    Name = "Nested",
                    LatestRevision = 1,
                    PublishedRevision = 1,
                    CreateBy = "user-id",
                    CreateTime = now,
                }
            );
            context.AgentflowRevisions.Add(
                new AgentflowRevision
                {
                    AgentflowId = nestedAgentflowId,
                    Revision = 1,
                    CreateBy = "user-id",
                    CreateTime = now,
                }
            );
            context.AgentflowNodes.Add(
                new AgentflowNode
                {
                    AgentflowId = nestedAgentflowId,
                    AgentflowRevision = 1,
                    NodeId = "nested-input",
                    Kind = AgentflowNodeKind.Input,
                    CreateBy = "user-id",
                    CreateTime = now,
                }
            );
            await context.SaveChangesAsync(cancellationToken);
        }

        private async Task AddNestedReferenceAsync(
            Fixture fixture,
            AgentflowDependencyRevision dependency,
            CancellationToken cancellationToken
        )
        {
            await using var context = CreateContext();
            context.AgentflowNodes.Add(
                new AgentflowNode
                {
                    AgentflowId = fixture.AgentflowId,
                    AgentflowRevision = dependency.ParentRevision,
                    NodeId = dependency.ParentNodeId,
                    Kind = AgentflowNodeKind.WorkflowAsAgent,
                    RelateId = dependency.TargetAgentflowId,
                    CreateBy = "user-id",
                    CreateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            var turn = await context.ProjectConversationTurns.SingleAsync(
                item => item.Id == fixture.SourceTurnId,
                cancellationToken
            );
            turn.Extra = turn.Extra with
            {
                AgentflowDependencies =
                [
                    new ProjectConversationTurnAgentflowDependency(
                        dependency.ParentAgentflowId,
                        dependency.ParentRevision,
                        dependency.ParentNodeId,
                        dependency.BlockMemberId,
                        dependency.TargetAgentflowId,
                        dependency.TargetRevision
                    ),
                ],
            };
            await context.SaveChangesAsync(cancellationToken);
        }

        public async Task<RecordedAgentflowCheckpoint?> RecordDurableCheckpointAsync(
            AgentflowCheckpointStore store,
            Fixture fixture,
            AgentflowRevisionSnapshot snapshot,
            CancellationToken cancellationToken
        )
        {
            await AddDurableExecutionAsync(
                fixture,
                fixture.SourceTurnId,
                DurableExecutionStatus.Completed,
                dependencies: snapshot.Dependencies
            );
            return await store.RecordAsync(
                fixture.SourceTurnId,
                fixture.ProjectId,
                fixture.ConversationId,
                fixture.ContextId,
                fixture.TaskId,
                fixture.AgentflowId,
                snapshot.RootRevision,
                snapshot,
                "user-id",
                isDurable: true,
                CreateCheckpoint("nested-revision"),
                new Dictionary<string, string> { ["checkpoint-node"] = "Saved" },
                cancellationToken
            );
        }

        public Task<AgentflowCheckpointSnapshot> ResumeDistributedAsync(
            AgentflowCheckpointStore store,
            Fixture fixture,
            Guid occurrenceId,
            Guid resumeTurnId,
            CancellationToken cancellationToken
        ) =>
            store.PrepareDistributedResumeAsync(
                occurrenceId,
                resumeTurnId,
                fixture.ProjectId,
                fixture.ContextId,
                fixture.AgentflowId,
                "user-id",
                cancellationToken
            );

        public async Task AssertResumedDependencySnapshotAsync(
            Guid resumeTurnId,
            AgentflowRevisionSnapshot snapshot,
            CancellationToken cancellationToken
        )
        {
            await using var verification = CreateContext();
            var execution = await verification.DurableExecutions.SingleAsync(
                item => item.Id == resumeTurnId,
                cancellationToken
            );
            var manifest = DurableExecutionJson.DeserializeRequired<DurableExecutionManifest>(
                execution.ManifestJson,
                "durable execution manifest"
            );
            var turn = await verification.ProjectConversationTurns.SingleAsync(
                item => item.Id == resumeTurnId,
                cancellationToken
            );

            Assert.Equal(snapshot.Dependencies, manifest.AgentflowDependencies);
            Assert.Equal(
                snapshot.Dependencies[0].TargetRevision,
                Assert.Single(turn.Extra.AgentflowDependencies!).TargetRevision
            );
            Assert.Equal(2, await verification.DurableExecutions.CountAsync(cancellationToken));
        }

        public async Task AppendHistoryAsync(Fixture fixture, long sequence, string text)
        {
            await using var context = CreateContext();
            context.ProjectConversationChatHistories.Add(
                new ProjectConversationChatHistory
                {
                    Id = Guid.CreateVersion7(),
                    ConversationId = fixture.ConversationId,
                    TaskId = fixture.TaskId,
                    Status = TaskExecutionStatus.Succeeded,
                    ConversationSequence = sequence,
                    ConversationPayload = text,
                    CreateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async Task PublishRevisionAsync(Guid agentflowId, int revision)
        {
            await using var context = CreateContext();
            var agentflow = await context.Agentflows.SingleAsync(
                item => item.Id == agentflowId,
                TestContext.Current.CancellationToken
            );
            var now = TimeProvider.System.GetUtcNow();
            context.AgentflowRevisions.Add(
                new AgentflowRevision
                {
                    AgentflowId = agentflowId,
                    Revision = revision,
                    CreateBy = "user-id",
                    CreateTime = now,
                }
            );
            context.AgentflowNodes.Add(
                new AgentflowNode
                {
                    AgentflowId = agentflowId,
                    AgentflowRevision = revision,
                    NodeId = "new-revision-node",
                    Kind = AgentflowNodeKind.Input,
                    ConfigJson = "{}",
                    CreateBy = "user-id",
                    CreateTime = now,
                }
            );
            agentflow.LatestRevision = revision;
            agentflow.PublishedRevision = revision;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async Task RejectCheckpointInsertsAsync(CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            await context.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER reject_agentflow_checkpoint_insert
                BEFORE INSERT ON agentflow_checkpoint
                BEGIN
                    SELECT RAISE(ABORT, 'checkpoint insert rejected');
                END;
                """,
                cancellationToken
            );
        }

        public async Task AddDurableExecutionAsync(
            Fixture fixture,
            Guid turnId,
            DurableExecutionStatus status,
            IReadOnlyList<Guid>? extensionIds = null,
            IReadOnlyList<AgentflowDependencyRevision>? dependencies = null
        )
        {
            var manifest = new DurableExecutionManifest
            {
                TurnId = turnId,
                UserId = "user-id",
                WorkspaceSnapshot = Agw.Shared.Utils.ProjectWorkspacePaths.CreateSnapshot(fixture.ProjectId, null),
                AgentId = fixture.AgentflowId,
                AgentType = AgentRuntimeType.Agentflow,
                AgentflowRevision = 1,
                AgentflowDependencies = dependencies ?? [],
                Input = new AgwUserInput { Contents = [] },
                Task = new DurableProjectTaskSnapshot
                {
                    TaskId = fixture.TaskId,
                    ProjectConversationId = fixture.ConversationId,
                    ProjectId = fixture.ProjectId,
                    ContextId = fixture.ContextId,
                },
                Settings = DurableExecutionMapper.FromSettings(
                    SettingCommandMapper.FromCommand(new SettingCommand(fixture.ProjectId, fixture.ConversationId))
                ),
                ExtensionIds = extensionIds ?? [],
            };
            await using var context = CreateContext();
            context.DurableExecutions.Add(
                new DurableExecutionRecord
                {
                    Id = turnId,
                    UserId = "user-id",
                    ProjectId = fixture.ProjectId,
                    ProjectConversationId = fixture.ConversationId,
                    ScopeBackfilled = true,
                    ManifestJson = DurableExecutionJson.Serialize(manifest),
                    Status = status,
                    StateChangedAt = TimeProvider.System.GetUtcNow(),
                    StateVersion = Guid.CreateVersion7(),
                    CreateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _serviceProvider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class CountingTransactionInterceptor : DbTransactionInterceptor
    {
        private int _transactionCount;

        public int TransactionCount => Volatile.Read(ref _transactionCount);

        public void Reset() => Volatile.Write(ref _transactionCount, 0);

        public override ValueTask<DbTransaction> TransactionStartedAsync(
            DbConnection connection,
            TransactionEndEventData eventData,
            DbTransaction result,
            CancellationToken cancellationToken = default
        )
        {
            Interlocked.Increment(ref _transactionCount);
            return ValueTask.FromResult(result);
        }
    }
}
