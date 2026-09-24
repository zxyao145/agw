using Agw.Agents.Execution.Context;
using Agw.Shared.Data.Entities.Agents;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

/// <summary>
/// 为执行上下文工厂提供真实的 Agent Definition 行。
/// Supplies real Agent definition rows to the execution context factory.
/// </summary>
internal sealed class TestExecutionAgents : IDisposable
{
    private readonly TestAgentDatabase _database = new();

    public ExecutionContextFactory ContextFactory => new(_database.Context);

    public Guid Add(Guid agentId, string ownerUserId = "user-id")
    {
        using (UserInfoUtil.PushSystemScope())
        {
            if (_database.Context.Agents.IgnoreQueryFilters().Any(agent => agent.Id == agentId))
                return agentId;
            _database.Context.Agents.Add(
                new Agent
                {
                    Id = agentId,
                    Name = $"agent-{agentId:N}",
                    CreateBy = ownerUserId,
                }
            );
            _database.Context.SaveChanges();
        }
        return agentId;
    }

    public void Dispose() => _database.Dispose();
}
