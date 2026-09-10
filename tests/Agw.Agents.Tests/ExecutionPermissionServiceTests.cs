using System.Security.Claims;
using Agw.Agents.Definitions.Agents;
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
    [InlineData(ExternalAgentKind.Codex)]
    [InlineData(ExternalAgentKind.Pi)]
    public void ExternalSdk_WithoutApprovalChannel_OnlyAllowsFullAccess(ExternalAgentKind kind)
    {
        var agent = new Agent { Type = AgentType.External, ExternalAgentKind = kind };
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
            ExternalAgentKind = ExternalAgentKind.Codex,
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
        };
        inner.Nodes.Add(
            new AgentflowNode
            {
                NodeId = "codex",
                Kind = AgentflowNodeKind.Agent,
                RelateId = codex.Id,
            }
        );
        var outer = new Agentflow
        {
            Id = Guid.NewGuid(),
            Name = "outer",
            CreateBy = "owner",
        };
        outer.Nodes.Add(
            new AgentflowNode
            {
                NodeId = "nested",
                Kind = AgentflowNodeKind.WorkflowAsAgent,
                RelateId = inner.Id,
            }
        );
        db.Agents.AddRange(codex, foreign);
        db.Agentflows.AddRange(inner, outer);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = new ExecutionPermissionService(db);
        var capabilities = await service.GetAsync(
            AgentRuntimeType.Agentflow,
            outer.Id,
            TestContext.Current.CancellationToken
        );
        Assert.Equal([AgwPermissionMode.FullAccess], capabilities.SupportedPermissionModes);
        Assert.Throws<AgwException>(() =>
            ExecutionPermissionService.Validate(capabilities, AgwPermissionMode.AlwaysAsk)
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
                new Agent { Type = AgentType.External, ExternalAgentKind = ExternalAgentKind.ClaudeCode },
            }
        )
            Assert.Equal(
                Enum.GetValues<AgwPermissionMode>(),
                ExecutionPermissionService.ForAgent(agent).SupportedPermissionModes
            );
    }
}
