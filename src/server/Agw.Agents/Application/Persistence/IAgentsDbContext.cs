using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Executions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Application.Persistence;

public interface IAgentsDbContext : IModuleDbContext
{
    DbSet<Agent> Agents { get; }

    DbSet<AgentA2AEndpoint> AgentA2AEndpoints { get; }

    DbSet<AgentConnectionRelation> AgentConnectionRelations { get; }

    DbSet<AgentMcpServerRelation> AgentMcpToolServers { get; }

    DbSet<AgentSkillRelation> AgentSkillRelations { get; }

    DbSet<AgentSessionStateEntry> AgentSessionStates { get; }

    DbSet<BackgroundExecution> BackgroundExecutions { get; }

    DbSet<McpServer> McpToolServers { get; }

    DbSet<Agentflow> Agentflows { get; }

    DbSet<AgentflowRevision> AgentflowRevisions { get; }

    DbSet<AgentflowBlockMember> AgentflowBlockMembers { get; }

    DbSet<AgentflowNode> AgentflowNodes { get; }

    DbSet<AgentflowEdge> AgentflowEdges { get; }

    DbSet<AgentflowTrace> AgentflowNodeExecutionTraces { get; }

    DbSet<DurableExecutionRecord> DurableExecutions { get; }

    DbSet<DurableExecutionEventRecord> DurableExecutionEvents { get; }

    DbSet<DurableExecutionSteerRecord> DurableExecutionSteers { get; }

    DbSet<AgentflowCheckpointRecord> AgentflowCheckpoints { get; }
}
