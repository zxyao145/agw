using System.Text.Json;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.HumanInteraction.Durable.Contracts;
using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Encryption;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Tests;

public sealed class DurableTurnUpgradeTests
{
    public static bool PostgresEnabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGW_TEST_POSTGRES_CONNECTION_STRING"));

    [Fact]
    public async Task UpgradeActiveExecutions_PreservesStateAndAllowsRecovery()
    {
        await using var kit = await TurnPersistenceTestKit.CreateAsync();
        await VerifyUpgradeAsync(kit);
    }

    [Fact(
        SkipUnless = nameof(PostgresEnabled),
        Skip = "Requires an isolated PostgreSQL server with CREATE DATABASE permission."
    )]
    public async Task Postgres_UpgradeActiveExecutions_PreservesStateAndAllowsRecovery()
    {
        await using var kit = await TurnPersistenceTestKit.CreatePostgresAsync(
            Environment.GetEnvironmentVariable("AGW_TEST_POSTGRES_CONNECTION_STRING")!
        );
        await VerifyUpgradeAsync(kit);
    }

    private static async Task VerifyUpgradeAsync(TurnPersistenceTestKit kit)
    {
        // 准备加密的活动执行与迁移生成的输入 Turn。
        // Arrange encrypted active executions and input turns created by the schema migration.
        var token = TestContext.Current.CancellationToken;
        using var owner = TurnPersistenceTestKit.EnterUser();
        var protector = new DataProtectionEncryptedDataProtector(new EphemeralDataProtectionProvider());
        var services = new ServiceCollection();
        services.AddScoped(_ => new AgwDbContext(kit.DbOptions, protector));
        await using var provider = services.BuildServiceProvider();
        var upgrade = new DurableTurnUpgrade(
            provider.GetRequiredService<IServiceScopeFactory>(),
            kit.Locks,
            TimeProvider.System
        );
        var ids = new List<(Guid Id, DurableExecutionStatus Status, Guid InputId)>();
        foreach (
            var status in new[]
            {
                DurableExecutionStatus.Queued,
                DurableExecutionStatus.Running,
                DurableExecutionStatus.Resuming,
                DurableExecutionStatus.WaitingForHuman,
            }
        )
        {
            var task = await kit.SeedConversationAsync(contextId: status.ToString());
            var id = Guid.CreateVersion7();
            var input = TurnPersistenceTestKit.CreateInput("Continue this task");
            var rowId = Guid.CreateVersion7();
            var now = TimeProvider.System.GetUtcNow();
            var pending = new InteractionRequest[]
            {
                new ToolApprovalInteraction
                {
                    InteractionId = "answered",
                    Prompt = "Read first file",
                    Source = new InteractionSource
                    {
                        ToolName = "read_file",
                        CallId = "call-1",
                        ProviderRequestId = "request-1",
                    },
                },
                new ToolApprovalInteraction
                {
                    InteractionId = "pending",
                    Prompt = "Read second file",
                    Source = new InteractionSource
                    {
                        ToolName = "read_file",
                        CallId = "call-2",
                        ProviderRequestId = "request-2",
                    },
                },
            };
            var responses = new List<InteractionResponse>
            {
                new ToolApprovalDecision { InteractionId = "answered", Approved = true },
            };
            if (status == DurableExecutionStatus.Resuming)
                responses.Add(new ToolApprovalDecision { InteractionId = "pending", Approved = true });
            var interactionState = JsonUtil.Serialize(new DurableInteractionState { Pending = pending });
            var manifest = new DurableExecutionManifest
            {
                ExecutionId = id,
                UserId = TurnPersistenceTestKit.UserId,
                AgentId = Guid.CreateVersion7(),
                AgentType = AgentRuntimeType.Agent,
                Input = input,
                Task = new DurableProjectTaskSnapshot
                {
                    TaskId = task.TaskId,
                    ProjectId = task.ProjectId,
                    ProjectConversationId = task.ProjectConversationId,
                    ContextId = task.ContextId,
                },
                Settings = new DurableExecutionSettings { EnvironmentVariables = [], Resume = true },
                WorkspaceSnapshot = ProjectWorkspacePaths.CreateSnapshot(task.ProjectId, AppContext.BaseDirectory, []),
            };
            await using var db = new AgwDbContext(kit.DbOptions, protector);
            db.DurableExecutions.Add(
                new DurableExecutionRecord
                {
                    Id = id,
                    UserId = TurnPersistenceTestKit.UserId,
                    CreateBy = TurnPersistenceTestKit.UserId,
                    ProjectId = task.ProjectId,
                    ProjectConversationId = task.ProjectConversationId,
                    ScopeBackfilled = true,
                    ManifestJson = JsonUtil.Serialize(manifest),
                    Status = status,
                    SegmentIndex = status == DurableExecutionStatus.Queued ? 0 : 2,
                    StateVersion = Guid.CreateVersion7(),
                    StateChangedAt = now,
                    CreateTime = now,
                    PendingInteractionsJson = interactionState,
                    ResponsesJson = JsonUtil.Serialize(responses),
                }
            );
            if (status != DurableExecutionStatus.Queued)
            {
                db.ProjectConversationChatHistories.Add(
                    new ProjectConversationChatHistory
                    {
                        Id = rowId,
                        ConversationId = task.ProjectConversationId,
                        TaskId = task.TaskId,
                        ConversationSequence = 0,
                        TurnId = rowId,
                        Purpose = ConversationMessagePurpose.Input,
                        ConversationPayload = JsonSerializer.Serialize(
                            new ChatMessage(ChatRole.User, "Continue this task") { MessageId = input.MessageId },
                            WebJsonOptions.Default
                        ),
                        CreateTime = now,
                    }
                );
                db.ProjectConversationTurns.Add(
                    new ProjectConversationTurn
                    {
                        Id = rowId,
                        ProjectConversationId = task.ProjectConversationId,
                        TargetId = manifest.AgentId,
                        InputMessageId = rowId,
                        FirstSequence = 0,
                        LastSequence = 0,
                        Status = ProjectConversationTurnStatus.Completed,
                        StartedAt = now,
                        FinishedAt = now,
                    }
                );
                db.ProjectConversationChatHistories.Add(
                    new ProjectConversationChatHistory
                    {
                        Id = Guid.CreateVersion7(),
                        ConversationId = task.ProjectConversationId,
                        TaskId = task.TaskId,
                        ConversationSequence = 1,
                        TurnId = rowId,
                        Purpose = ConversationMessagePurpose.Message,
                        ConversationPayload = JsonSerializer.Serialize(
                            new ChatMessage(ChatRole.Assistant, "Work in progress"),
                            WebJsonOptions.Default
                        ),
                        CreateTime = now,
                    }
                );
            }
            await db.SaveChangesAsync(token);
            ids.Add((id, status, status == DurableExecutionStatus.Queued ? Guid.Parse(input.MessageId) : rowId));
        }

        // 执行两次升级，验证重复运行保持相同结果。
        // Act twice to verify idempotency.
        await upgrade.UpgradeAsync(token);
        await upgrade.UpgradeAsync(token);

        // 检查身份、输入、等待状态与可领取状态。
        // Assert identities, inputs, waiting state and claimability.
        await using var actual = new AgwDbContext(kit.DbOptions, protector);
        Assert.Equal(4, await actual.ProjectConversationTurns.CountAsync(token));
        foreach (var (id, status, inputId) in ids)
        {
            var execution = await actual.DurableExecutions.SingleAsync(row => row.Id == id, token);
            var turn = await actual.ProjectConversationTurns.SingleAsync(row => row.Id == id, token);
            var input = await actual.ProjectConversationChatHistories.SingleAsync(row => row.Id == inputId, token);
            Assert.Equal(inputId, turn.InputMessageId);
            Assert.Equal(id, input.TurnId);
            Assert.Equal(
                inputId.ToString("D"),
                JsonUtil.Deserialize<DurableExecutionManifest>(execution.ManifestJson)!.Input.MessageId
            );
            Assert.Equal(
                status == DurableExecutionStatus.Running ? DurableExecutionStatus.Resuming : status,
                execution.Status
            );
            Assert.Equal(status == DurableExecutionStatus.Queued ? 0 : 2, execution.SegmentIndex);
            var responses = JsonUtil.Deserialize<InteractionResponse[]>(execution.ResponsesJson!)!;
            Assert.Equal(status == DurableExecutionStatus.Resuming ? 2 : 1, responses.Length);
            Assert.Equal("answered", responses[0].InteractionId);
            Assert.Equal(
                2,
                JsonUtil.Deserialize<DurableInteractionState>(execution.PendingInteractionsJson!)!.Pending.Count
            );
            var history = await actual
                .ProjectConversationChatHistories.Where(row => row.ConversationId == turn.ProjectConversationId)
                .ToArrayAsync(token);
            Assert.Equal(status == DurableExecutionStatus.Queued ? 1 : 2, history.Length);
            Assert.All(history, row => Assert.Equal(id, row.TurnId));
            if (status == DurableExecutionStatus.Queued)
            {
                Assert.Equal(id, input.Metadata!["producerScopeId"].GetGuid());
                Assert.Equal(0, input.Metadata["generation"].GetInt32());
            }
            var events = await actual
                .DurableExecutionEvents.Where(entry => entry.TurnId == id)
                .OrderBy(entry => entry.TurnSequence)
                .ToArrayAsync(token);
            Assert.Equal(status == DurableExecutionStatus.WaitingForHuman ? 2 : 1, events.Length);
            Assert.True(AgwMessageClassifier.IsTurnStart(JsonUtil.Deserialize<AgwMessage>(events[0].PayloadJson)!));
            if (status == DurableExecutionStatus.WaitingForHuman)
                Assert.Contains("pending", events[1].PayloadJson);
            var claimed = await kit.Leases.TryClaimAsync(id, "new-worker", TimeSpan.FromSeconds(30), token);
            Assert.Equal(status != DurableExecutionStatus.WaitingForHuman, claimed != null);
        }
    }

    [Fact]
    public async Task UpgradeAsync_InvalidManifest_FailsWithoutCreatingTurn()
    {
        // Arrange
        using var owner = TurnPersistenceTestKit.EnterUser();
        await using var kit = await TurnPersistenceTestKit.CreateAsync();
        var task = await kit.SeedConversationAsync();
        await using (var db = kit.CreateContext())
        {
            db.DurableExecutions.Add(
                new DurableExecutionRecord
                {
                    Id = Guid.CreateVersion7(),
                    UserId = TurnPersistenceTestKit.UserId,
                    ProjectId = task.ProjectId,
                    ProjectConversationId = task.ProjectConversationId,
                    ScopeBackfilled = true,
                    ManifestJson = "{}",
                    Status = DurableExecutionStatus.Running,
                }
            );
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var upgrade = new DurableTurnUpgrade(
            kit.Services.GetRequiredService<IServiceScopeFactory>(),
            kit.Locks,
            TimeProvider.System
        );
        // Act
        await Assert.ThrowsAsync<AgwException>(() => upgrade.UpgradeAsync(TestContext.Current.CancellationToken));
        // Assert
        await using var actual = kit.CreateContext();
        Assert.Empty(await actual.ProjectConversationTurns.ToArrayAsync(TestContext.Current.CancellationToken));
    }
}
