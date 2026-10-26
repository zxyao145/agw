using System.Collections.Concurrent;
using System.Data.Common;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Configuration;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Turns;
using Agw.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Agw.Agents.Tests;

/// <summary>
/// Distributed 中断后的用量结算：中断时的结算时间与被中断租约的代次，以及持有该租约的 worker 退出后提前结算。
/// Usage settlement after a distributed interrupt: the settlement time and interrupted lease epoch written by the interrupt, and the earlier settlement once the worker holding that lease exits.
/// </summary>
public sealed partial class DurableExecutionStoreTests
{
    [Fact]
    public async Task InterruptAsync_HeldLease_SettlesUsageOneLeaseAfterFinish()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var id = await RegisterAsync();
        var lease = await ClaimAsync(id);

        // Act
        Assert.True(await _kit.Coordinator.InterruptAsync(id, UserId, reason: null, token));

        // Assert
        var turn = await _kit.ReadTurnAsync(id);
        Assert.Equal(_clock.GetUtcNow(), turn.FinishedAt);
        Assert.Equal(turn.FinishedAt + LeaseDuration, turn.UsageSettlesAt);
        Assert.Equal(lease.Epoch, (await _kit.ReadExecutionAsync(id)).InterruptedLeaseEpoch);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("waiting")]
    [InlineData("expired")]
    public async Task InterruptAsync_NoHeldLease_SettlesUsageAtFinish(string state)
    {
        // Arrange: no worker holds a valid lease when the interrupt commits.
        // 准备：中断提交时没有 worker 持有有效的租约。
        var token = TestContext.Current.CancellationToken;
        var id = await RegisterAsync();
        if (state == "waiting")
            await WaitForInteractionsAsync(id, [InteractionTestData.Input("request-1")]);
        if (state == "expired")
        {
            await ClaimAsync(id);
            _clock.Advance(LeaseDuration);
        }

        // Act
        Assert.True(await _kit.Coordinator.InterruptAsync(id, UserId, reason: null, token));

        // Assert
        var turn = await _kit.ReadTurnAsync(id);
        Assert.Equal(_clock.GetUtcNow(), turn.FinishedAt);
        Assert.Equal(turn.FinishedAt, turn.UsageSettlesAt);
        Assert.Null((await _kit.ReadExecutionAsync(id)).InterruptedLeaseEpoch);
    }

    [Fact]
    public async Task SettleInterruptedUsageAsync_InterruptedHolderExits_SettlesAtExitAndClearsEpoch()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var id = await RegisterAsync();
        var lease = await ClaimAsync(id);
        Assert.True(await _kit.Coordinator.InterruptAsync(id, UserId, reason: null, token));
        _clock.Advance(TimeSpan.FromSeconds(3));

        // Act
        await _kit.Coordinator.SettleInterruptedUsageAsync(lease, token);

        // Assert
        var turn = await _kit.ReadTurnAsync(id);
        Assert.Equal(turn.FinishedAt + TimeSpan.FromSeconds(3), turn.UsageSettlesAt);
        Assert.Null((await _kit.ReadExecutionAsync(id)).InterruptedLeaseEpoch);
    }

    [Fact]
    public async Task SettleInterruptedUsageAsync_TakenOverThenInterrupted_OnlyInterruptedHolderSettles()
    {
        // Arrange: worker A's lease expires, worker B takes it over and is interrupted.
        // 准备：worker A 的租约过期，worker B 接管后被中断。
        var token = TestContext.Current.CancellationToken;
        var task = await _kit.SeedConversationAsync();
        var id = (await AcceptAsync(task)).Request.TurnId;
        var oldLease = await ClaimAsync(id, "worker-a");
        _clock.Advance(LeaseDuration);
        var newLease = await ClaimAsync(id, "worker-b");
        await AddUsageAsync(task, id, 40);
        Assert.True(await _kit.Coordinator.InterruptAsync(id, UserId, reason: null, token));
        var finishedAt = (await _kit.ReadTurnAsync(id)).FinishedAt;

        // Act: A exits first, then B.
        // 执行：A 先退出，B 后退出。
        _clock.Advance(TimeSpan.FromSeconds(1));
        await _kit.Coordinator.SettleInterruptedUsageAsync(oldLease, token);
        var afterOldWorker = await _kit.ReadTurnAsync(id);
        var usageAfterOldWorker = await ReadConversationUsageAsync(task);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await _kit.Coordinator.SettleInterruptedUsageAsync(newLease, token);
        var usageAfterNewWorker = await ReadConversationUsageAsync(task);

        // Assert
        Assert.Equal(finishedAt + LeaseDuration, afterOldWorker.UsageSettlesAt);
        Assert.Equal([id], usageAfterOldWorker.UnsettledTurnIds);
        Assert.Equal(0, usageAfterOldWorker.TotalTokenCount);
        Assert.Equal(finishedAt + TimeSpan.FromSeconds(2), (await _kit.ReadTurnAsync(id)).UsageSettlesAt);
        Assert.Null((await _kit.ReadExecutionAsync(id)).InterruptedLeaseEpoch);
        Assert.Empty(usageAfterNewWorker.UnsettledTurnIds);
        Assert.Equal(40, usageAfterNewWorker.TotalTokenCount);
    }

    [Fact]
    public async Task InterruptAsync_HolderNeverExits_CountsUsageAfterLeaseDuration()
    {
        // Arrange: the instance holding the interrupted lease has stopped and never confirms settlement.
        // 准备：持有被中断租约的实例已经停止，不会确认结算。
        var token = TestContext.Current.CancellationToken;
        var task = await _kit.SeedConversationAsync();
        var id = (await AcceptAsync(task)).Request.TurnId;
        await ClaimAsync(id);
        await AddUsageAsync(task, id, 40);
        Assert.True(await _kit.Coordinator.InterruptAsync(id, UserId, reason: null, token));

        // Act
        _clock.Advance(LeaseDuration - TimeSpan.FromSeconds(1));
        var beforeDeadline = await ReadConversationUsageAsync(task);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var atDeadline = await ReadConversationUsageAsync(task);

        // Assert
        Assert.Equal([id], beforeDeadline.UnsettledTurnIds);
        Assert.Equal(0, beforeDeadline.TotalTokenCount);
        Assert.Empty(atDeadline.UnsettledTurnIds);
        Assert.Equal(40, atDeadline.TotalTokenCount);
    }

    [Fact]
    public async Task RunLeasedSegmentAsync_InterruptedSegmentExits_RecordsTurnUsageBeforeSettlement()
    {
        // Arrange: a real agent run inside a leased segment reports usage and keeps running.
        // 准备：持有租约的 Segment 中运行真实的 Agent，报告用量后继续运行。
        var token = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider();
        var client = new UsageReportingChatClient();
        var commands = new CommandLog();
        await using var kit = await TurnPersistenceTestKit.CreateAsync(
            clock,
            new ExecutionRuntimeOptions(),
            services => DurableUsageTestServices.Add(services, client),
            commands
        );
        await DurableUsageTestServices.SeedAgentAsync(kit, _agentId);
        var task = await kit.SeedConversationAsync();
        var id = (
            await kit.CreateAcceptance(
                    projectTasks: null,
                    new AgentExecutionFacadeTests.WorkspaceProjects(),
                    kit.Coordinator
                )
                .AcceptAsync(
                    new TurnAcceptanceRequest(
                        UserId,
                        null,
                        new ExecutionTarget(_agentId, AgentRuntimeType.Agent),
                        task.ProjectConversationId,
                        TurnPersistenceTestKit.CreateInput("hello"),
                        TurnPersistenceTestKit.CreateSettings(task.ProjectId, task.ProjectConversationId),
                        true
                    )
                    {
                        Task = task,
                    },
                    token
                )
        )
            .Request
            .TurnId;
        var lease = Assert.IsType<DurableLease>(await kit.Leases.TryClaimAsync(id, "worker-a", LeaseDuration, token));
        var running = kit.Coordinator.RunLeasedSegmentAsync(lease, CancellationToken.None);
        await client.UsageReported.WaitAsync(TimeSpan.FromSeconds(30), token);

        // Act: interrupt, then let the cancelled run exit three seconds later.
        // 执行：中断，再让被取消的运行在三秒后退出。
        Assert.True(await kit.Coordinator.InterruptAsync(id, UserId, reason: null, token));
        var finishedAt = (await kit.ReadTurnAsync(id)).FinishedAt;
        clock.Advance(TimeSpan.FromSeconds(3));
        client.ReleaseExit();
        await running.WaitAsync(TimeSpan.FromSeconds(30), token);

        // Assert
        await using var context = kit.CreateContext();
        var usage = await context.AgentUsages.IgnoreQueryFilters().AsNoTracking().SingleAsync(token);
        Assert.Equal(id, usage.TurnId);
        Assert.Equal(9, usage.TotalTokenCount);
        Assert.Equal(finishedAt + TimeSpan.FromSeconds(3), (await kit.ReadTurnAsync(id)).UsageSettlesAt);
        Assert.Null((await kit.ReadExecutionAsync(id)).InterruptedLeaseEpoch);
        var executed = commands.Texts.ToArray();
        var usageInsert = Array.FindIndex(executed, text => text.Contains("INSERT INTO \"agent_usage\""));
        var settlement = Array.FindLastIndex(
            executed,
            text => text.Contains("UPDATE \"project_conversation_turn\"") && text.Contains("\"usage_settles_at\"")
        );
        Assert.True(usageInsert >= 0, "The run recorded no usage.");
        Assert.True(settlement > usageInsert, "Settlement was written before the run's usage.");
    }

    private Task AddUsageAsync(AgentExecutionTask task, Guid turnId, long totalTokenCount) =>
        DurableUsageTestServices.AddUsageAsync(_kit, task, turnId, totalTokenCount);

    private Task<ProjectConversationUsage> ReadConversationUsageAsync(AgentExecutionTask task) =>
        DurableUsageTestServices.ReadConversationUsageAsync(_kit, task);

    /// <summary>
    /// 按执行顺序记录数据库命令的文本。
    /// Records the text of database commands in execution order.
    /// </summary>
    private sealed class CommandLog : DbCommandInterceptor
    {
        public ConcurrentQueue<string> Texts { get; } = new();

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default
        )
        {
            Texts.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default
        )
        {
            Texts.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
