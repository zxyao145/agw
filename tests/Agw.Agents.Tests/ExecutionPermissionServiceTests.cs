using System.Security.Claims;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Persistence;
using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

public sealed class ExecutionPermissionServiceTests
{
    [Theory]
    [InlineData(EngineKind.Codex)]
    [InlineData(EngineKind.Pi)]
    public void ExternalSdk_WithoutApprovalChannel_OnlyAllowsFullAccess(EngineKind kind)
    {
        var agent = new Agent { Type = AgentType.External, ExternalAgentKind = kind };
        Assert.False(ExecutionPermissionService.ForAgent(agent).SupportsNoTools);
        Assert.Equal(
            [AgwPermissionMode.FullAccess],
            ExecutionPermissionService.ForAgent(agent).SupportedPermissionModes
        );
        ExecutionPermissionService.Validate(agent, null);
        ExecutionPermissionService.Validate(agent, AgwPermissionMode.FullAccess);
        Assert.Throws<AgwException>(() => ExecutionPermissionService.Validate(agent, AgwPermissionMode.AlwaysAsk));
        Assert.Throws<AgwException>(() =>
            ExecutionPermissionService.Validate(agent, AgwPermissionMode.AllowSameArguments)
        );
    }

    [Fact]
    public async Task NestedFlow_IntersectsCapabilitiesAndRejectsForeignOrMissingTargets()
    {
        using var owner = UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "test"))
        );
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var db = new AgwDbContext(
            new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).Options
        );
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var codex = new Agent
        {
            Id = Guid.NewGuid(),
            Name = "codex",
            Type = AgentType.External,
            ExternalAgentKind = EngineKind.Codex,
            CreateBy = "owner",
        };
        var system = new Agent
        {
            Id = Guid.NewGuid(),
            Name = "system",
            Type = AgentType.System,
            CreateBy = "owner",
        };
        var foreign = new Agent
        {
            Id = Guid.NewGuid(),
            Name = "foreign",
            CreateBy = "another",
        };
        var inner = new Agentflow
        {
            Id = Guid.NewGuid(),
            Name = "inner",
            CreateBy = "owner",
            LatestRevision = 2,
            PublishedRevision = 1,
        };
        var outer = new Agentflow
        {
            Id = Guid.NewGuid(),
            Name = "outer",
            CreateBy = "owner",
            LatestRevision = 2,
            PublishedRevision = 1,
        };
        var unpublished = new Agentflow
        {
            Id = Guid.NewGuid(),
            Name = "unpublished",
            CreateBy = "owner",
            LatestRevision = 1,
        };
        var foreignFlow = new Agentflow
        {
            Id = Guid.NewGuid(),
            Name = "foreign-flow",
            CreateBy = "another",
            LatestRevision = 1,
        };
        db.Agents.AddRange(codex, foreign, system);
        db.Agentflows.AddRange(inner, outer, unpublished, foreignFlow);
        db.AgentflowRevisions.AddRange(
            new AgentflowRevision { AgentflowId = inner.Id, Revision = 1 },
            new AgentflowRevision { AgentflowId = inner.Id, Revision = 2 },
            new AgentflowRevision { AgentflowId = outer.Id, Revision = 1 },
            new AgentflowRevision { AgentflowId = outer.Id, Revision = 2 },
            new AgentflowRevision { AgentflowId = unpublished.Id, Revision = 1 },
            new AgentflowRevision { AgentflowId = foreignFlow.Id, Revision = 1 }
        );
        db.AgentflowNodes.AddRange(
            new AgentflowNode
            {
                AgentflowId = inner.Id,
                AgentflowRevision = 1,
                NodeId = "team",
                Kind = AgentflowNodeKind.ConcurrentBlock,
            },
            new AgentflowNode
            {
                AgentflowId = outer.Id,
                AgentflowRevision = 1,
                NodeId = "nested",
                Kind = AgentflowNodeKind.WorkflowAsAgent,
                RelateId = inner.Id,
            },
            new AgentflowNode
            {
                AgentflowId = outer.Id,
                AgentflowRevision = 2,
                NodeId = "system",
                Kind = AgentflowNodeKind.Agent,
                RelateId = system.Id,
            },
            new AgentflowNode
            {
                AgentflowId = unpublished.Id,
                AgentflowRevision = 1,
                NodeId = "system",
                Kind = AgentflowNodeKind.Agent,
                RelateId = system.Id,
            },
            new AgentflowNode
            {
                AgentflowId = foreignFlow.Id,
                AgentflowRevision = 1,
                NodeId = "system",
                Kind = AgentflowNodeKind.Agent,
                RelateId = system.Id,
            }
        );
        db.AgentflowBlockMembers.Add(
            new AgentflowBlockMember
            {
                AgentflowId = inner.Id,
                AgentflowRevision = 1,
                NodeId = "team",
                MemberId = "codex",
                Kind = AgentflowNodeKind.Agent,
                RelateId = codex.Id,
                Name = "Codex",
            }
        );
        var token = TestContext.Current.CancellationToken;
        await db.SaveChangesAsync(token);
        var snapshots = new AgentflowRevisionSnapshotReader(db);
        var snapshot = await snapshots.CreateRevisionSnapshotAsync(outer.Id, 0, token);
        var service = new ExecutionPermissionService(db, snapshots);
        var capabilities = await service.GetAsync(AgentRuntimeType.Agentflow, outer.Id, token);
        Assert.Equal([AgwPermissionMode.FullAccess], capabilities.SupportedPermissionModes);
        Assert.False(capabilities.SupportsNoTools);

        var editorSnapshot = await snapshots.CreateRevisionSnapshotAsync(outer.Id, 2, token);
        var editorCapabilities = await service.GetAgentflowAsync(outer.Id, editorSnapshot, token);
        Assert.Equal(Enum.GetValues<AgwPermissionMode>(), editorCapabilities.SupportedPermissionModes);
        Assert.True(editorCapabilities.SupportsNoTools);
        Assert.Equal(2, editorSnapshot.RootRevision);
        var editorQueryCapabilities = await service.GetAsync(AgentRuntimeType.Agentflow, outer.Id, 2, token);
        Assert.Equal(editorCapabilities.SupportedPermissionModes, editorQueryCapabilities.SupportedPermissionModes);
        Assert.Equal(editorCapabilities.SupportsNoTools, editorQueryCapabilities.SupportsNoTools);
        Assert.Equal(editorCapabilities.Reason, editorQueryCapabilities.Reason);
        var zeroRevisionError = await Assert.ThrowsAsync<AgwException>(() =>
            service.GetAsync(AgentRuntimeType.Agentflow, outer.Id, 0, token)
        );
        Assert.Equal(ErrorCodes.InvalidParam.Code, zeroRevisionError.Code);

        var unpublishedError = await Assert.ThrowsAsync<AgwException>(() =>
            service.GetAsync(AgentRuntimeType.Agentflow, unpublished.Id, token)
        );
        Assert.Equal(ErrorCodes.AgentflowNotPublished.Code, unpublishedError.Code);
        Assert.Equal(
            Enum.GetValues<AgwPermissionMode>(),
            (await service.GetAsync(AgentRuntimeType.Agentflow, unpublished.Id, 1, token)).SupportedPermissionModes
        );
        var foreignFlowError = await Assert.ThrowsAsync<AgwException>(() =>
            service.GetAsync(AgentRuntimeType.Agentflow, foreignFlow.Id, 1, token)
        );
        Assert.Equal(ErrorCodes.ResourceNotFound.Code, foreignFlowError.Code);
        var agentRevisionError = await Assert.ThrowsAsync<AgwException>(() =>
            service.GetAsync(AgentRuntimeType.Agent, codex.Id, 1, token)
        );
        Assert.Equal(ErrorCodes.InvalidParam.Code, agentRevisionError.Code);

        Assert.Throws<AgwException>(() =>
            ExecutionPermissionService.Validate(capabilities, AgwPermissionMode.AlwaysAsk)
        );
        inner.PublishedRevision = 2;
        outer.PublishedRevision = 2;
        await db.SaveChangesAsync(token);
        Assert.Contains(
            AgwPermissionMode.AlwaysAsk,
            (await service.GetAsync(AgentRuntimeType.Agentflow, outer.Id, token)).SupportedPermissionModes
        );
        Assert.Equal(
            [AgwPermissionMode.FullAccess],
            (await service.GetAgentflowAsync(outer.Id, snapshot, token)).SupportedPermissionModes
        );
        Assert.Equal(
            Enum.GetValues<AgwPermissionMode>(),
            (await service.GetAgentflowAsync(outer.Id, editorSnapshot, token)).SupportedPermissionModes
        );
        foreach (var id in new[] { foreign.Id, Guid.NewGuid() })
        {
            var error = await Assert.ThrowsAsync<AgwException>(() =>
                service.GetAsync(AgentRuntimeType.Agent, id, TestContext.Current.CancellationToken)
            );
            Assert.Equal(ErrorCodes.ResourceNotFound.Code, error.Code);
        }
    }

    [Fact]
    public void SystemAndClaude_SupportAllModes()
    {
        foreach (
            var agent in new[]
            {
                new Agent { Type = AgentType.System },
                new Agent { Type = AgentType.External, ExternalAgentKind = EngineKind.ClaudeCode },
            }
        )
        {
            Assert.Equal(
                Enum.GetValues<AgwPermissionMode>(),
                ExecutionPermissionService.ForAgent(agent).SupportedPermissionModes
            );
            Assert.True(ExecutionPermissionService.ForAgent(agent).SupportsNoTools);
        }
    }
}
