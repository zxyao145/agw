using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Executions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Application.Persistence;

public interface IAgentsDbContext : IModuleDbContext
{
    DbSet<Agent> Agents { get; }

    DbSet<AgentConnectionRelation> AgentConnectionRelations { get; }

    DbSet<AgentMcpServerRelation> AgentMcpToolServers { get; }

    DbSet<AgentSkillRelation> AgentSkillRelations { get; }

    DbSet<AgentSessionStateEntry> AgentSessionStates { get; }

    DbSet<McpServer> McpToolServers { get; }

    DbSet<Agentflow> Agentflows { get; }

    DbSet<AgentflowNode> AgentflowNodes { get; }

    DbSet<AgentflowEdge> AgentflowEdges { get; }

    DbSet<AgentflowTrace> AgentflowNodeExecutionTraces { get; }

    DbSet<DurableExecutionRecord> DurableExecutions { get; }

    DbSet<DurableExecutionEventRecord> DurableExecutionEvents { get; }

    DbSet<AgentflowCheckpointRecord> AgentflowCheckpoints { get; }
}
