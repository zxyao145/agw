using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Auth;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Entities.Tools;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Data;

/// <summary>
/// Names for the row-level user scope filter. Internal maintenance queries may
/// disable only this filter while retaining soft-delete filtering. A missing
/// user context is fail-closed; trusted infrastructure uses a restricted
/// system scope for cross-owner scans.
/// </summary>
public static class UserScopeQueryFilterNames
{
    public const string UserScope = "UserScopeFilter";
}

internal static class UserScopeModelBuilderExtensions
{
    public static void ApplyUserScopeQueryFilters(this ModelBuilder modelBuilder, AgwDbContext context)
    {
        modelBuilder
            .Entity<Provider>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                provider =>
                    context.UserScopeBypass || context.UserScopeIsActive && provider.CreateBy == context.CurrentUserId
            );
        modelBuilder
            .Entity<AgwAiModel>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                model => context.UserScopeBypass || context.UserScopeIsActive && model.CreateBy == context.CurrentUserId
            );
        modelBuilder
            .Entity<ModelProviderRelation>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                relation =>
                    context.UserScopeBypass
                    || context.UserScopeIsActive
                        && relation.CreateBy == context.CurrentUserId
                        && relation.Model!.CreateBy == context.CurrentUserId
                        && relation.Provider!.CreateBy == context.CurrentUserId
            );
        modelBuilder
            .Entity<Agent>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                agent => context.UserScopeBypass || context.UserScopeIsActive && agent.CreateBy == context.CurrentUserId
            );
        modelBuilder
            .Entity<Agentflow>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                agentflow =>
                    context.UserScopeBypass || context.UserScopeIsActive && agentflow.CreateBy == context.CurrentUserId
            );
        modelBuilder
            .Entity<McpServer>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                server =>
                    context.UserScopeBypass || context.UserScopeIsActive && server.CreateBy == context.CurrentUserId
            );
        modelBuilder
            .Entity<Skill>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                skill =>
                    context.UserScopeBypass
                    || context.UserScopeIsActive
                        && context.CurrentUserId != null
                        && (skill.Kind == SkillKind.BuiltIn || skill.CreateBy == context.CurrentUserId)
            );
        modelBuilder
            .Entity<Project>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                project =>
                    context.UserScopeBypass || context.UserScopeIsActive && project.CreateBy == context.CurrentUserId
            );
        modelBuilder
            .Entity<ProjectConversation>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                conversation =>
                    context.UserScopeBypass
                    || context.UserScopeIsActive
                        && conversation.CreateBy == context.CurrentUserId
                        && conversation.Project!.CreateBy == context.CurrentUserId
            );
        modelBuilder
            .Entity<Job>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                job => context.UserScopeBypass || context.UserScopeIsActive && job.CreateBy == context.CurrentUserId
            );
        modelBuilder
            .Entity<PluginInstallation>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                installation =>
                    context.UserScopeBypass
                    || context.UserScopeIsActive && installation.CreateBy == context.CurrentUserId
            );
        modelBuilder
            .Entity<Connection>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                connection =>
                    context.UserScopeBypass || context.UserScopeIsActive && connection.CreateBy == context.CurrentUserId
            );
        modelBuilder
            .Entity<ApiToken>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                token => context.UserScopeBypass || context.UserScopeIsActive && token.CreateBy == context.CurrentUserId
            );
        modelBuilder
            .Entity<UserMemory>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                memory => context.UserScopeBypass || context.UserScopeIsActive && memory.UserId == context.CurrentUserId
            );
        modelBuilder
            .Entity<AgentUsage>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                usage => context.UserScopeBypass || context.UserScopeIsActive && usage.UserId == context.CurrentUserId
            );
        modelBuilder
            .Entity<DurableExecutionRecord>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                execution =>
                    context.UserScopeBypass || context.UserScopeIsActive && execution.UserId == context.CurrentUserId
            );
        modelBuilder
            .Entity<AgentflowCheckpointRecord>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                checkpoint =>
                    context.UserScopeBypass || context.UserScopeIsActive && checkpoint.UserId == context.CurrentUserId
            );

        // Child rows do not duplicate the owner. Correlate each child with its
        // consistency root so direct DbSet queries remain fail-closed too.
        modelBuilder
            .Entity<ProviderAuthConfig>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                config =>
                    context.UserScopeBypass
                    || context.UserScopeIsActive && config.Provider!.CreateBy == context.CurrentUserId
            );
        modelBuilder
            .Entity<RemoteSkillCache>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                cache =>
                    context.UserScopeBypass
                    || context.UserScopeIsActive
                        && context
                            .Set<Skill>()
                            .Any(skill =>
                                skill.Id == cache.SkillId
                                && (skill.Kind == SkillKind.BuiltIn || skill.CreateBy == context.CurrentUserId)
                            )
            );
        modelBuilder
            .Entity<AgentflowNode>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                node =>
                    context.UserScopeBypass
                    || context.UserScopeIsActive
                        && context
                            .Set<Agentflow>()
                            .Any(agentflow =>
                                agentflow.Id == node.AgentflowId && agentflow.CreateBy == context.CurrentUserId
                            )
            );
        modelBuilder
            .Entity<AgentflowEdge>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                edge =>
                    context.UserScopeBypass
                    || context.UserScopeIsActive
                        && context
                            .Set<Agentflow>()
                            .Any(agentflow =>
                                agentflow.Id == edge.AgentflowId && agentflow.CreateBy == context.CurrentUserId
                            )
            );
        modelBuilder
            .Entity<AgentflowTrace>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                trace =>
                    context.UserScopeBypass
                    || context.UserScopeIsActive
                        && context
                            .Set<Project>()
                            .Any(project => project.Id == trace.ProjectId && project.CreateBy == context.CurrentUserId)
            );
        modelBuilder
            .Entity<ProjectMemoryEntry>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                memory =>
                    context.UserScopeBypass
                    || context.UserScopeIsActive
                        && context
                            .Set<Project>()
                            .Any(project => project.Id == memory.ProjectId && project.CreateBy == context.CurrentUserId)
            );
        modelBuilder
            .Entity<ProjectConversationChatHistory>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                history =>
                    context.UserScopeBypass
                    || context.UserScopeIsActive
                        && context
                            .Set<ProjectConversation>()
                            .Any(conversation =>
                                conversation.Id == history.ConversationId
                                && conversation.CreateBy == context.CurrentUserId
                                && conversation.Project!.CreateBy == context.CurrentUserId
                            )
            );
        modelBuilder
            .Entity<ProjectConversationBinding>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                binding =>
                    context.UserScopeBypass
                    || context.UserScopeIsActive
                        && context
                            .Set<ProjectConversation>()
                            .Any(conversation =>
                                conversation.Id == binding.ProjectConversationId
                                && conversation.CreateBy == context.CurrentUserId
                                && conversation.Project!.CreateBy == context.CurrentUserId
                            )
            );
        modelBuilder
            .Entity<JobLog>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                log =>
                    context.UserScopeBypass
                    || context.UserScopeIsActive
                        && context.Set<Job>().Any(job => job.Id == log.JobId && job.CreateBy == context.CurrentUserId)
            );
        modelBuilder
            .Entity<PluginInstallationCredential>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                credential =>
                    context.UserScopeBypass
                    || context.UserScopeIsActive && credential.PluginInstallation!.CreateBy == context.CurrentUserId
            );
        modelBuilder
            .Entity<ConnectionCredential>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                credential =>
                    context.UserScopeBypass
                    || context.UserScopeIsActive && credential.Connection!.CreateBy == context.CurrentUserId
            );
        modelBuilder
            .Entity<AgentSessionStateEntry>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                entry =>
                    context.UserScopeBypass
                    || context.UserScopeIsActive
                        && entry.Agent!.CreateBy == context.CurrentUserId
                        && entry.ProjectConversation!.CreateBy == context.CurrentUserId
                        && entry.ProjectConversation.Project!.CreateBy == context.CurrentUserId
            );
        modelBuilder
            .Entity<AgentSkillRelation>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                relation =>
                    context.UserScopeBypass
                    || context.UserScopeIsActive
                        && relation.Agent!.CreateBy == context.CurrentUserId
                        && context
                            .Set<Skill>()
                            .Any(skill =>
                                skill.Id == relation.SkillId
                                && (skill.Kind == SkillKind.BuiltIn || skill.CreateBy == context.CurrentUserId)
                            )
            );
        modelBuilder
            .Entity<AgentMcpServerRelation>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                relation =>
                    context.UserScopeBypass
                    || context.UserScopeIsActive
                        && relation.Agent!.CreateBy == context.CurrentUserId
                        && relation.McpToolServer!.CreateBy == context.CurrentUserId
            );
        modelBuilder
            .Entity<AgentConnectionRelation>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                relation =>
                    context.UserScopeBypass
                    || context.UserScopeIsActive
                        && relation.Agent!.CreateBy == context.CurrentUserId
                        && relation.Connection!.CreateBy == context.CurrentUserId
            );
        modelBuilder
            .Entity<ProjectSkillRelation>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                relation =>
                    context.UserScopeBypass
                    || context.UserScopeIsActive
                        && relation.Project!.CreateBy == context.CurrentUserId
                        && (
                            relation.Skill!.Kind == SkillKind.BuiltIn
                            || relation.Skill.CreateBy == context.CurrentUserId
                        )
            );
        modelBuilder
            .Entity<ProjectMcpServerRelation>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                relation =>
                    context.UserScopeBypass
                    || context.UserScopeIsActive
                        && relation.Project!.CreateBy == context.CurrentUserId
                        && relation.McpToolServer!.CreateBy == context.CurrentUserId
            );
        modelBuilder
            .Entity<ProjectConnectionRelation>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                relation =>
                    context.UserScopeBypass
                    || context.UserScopeIsActive
                        && relation.Project!.CreateBy == context.CurrentUserId
                        && relation.Connection!.CreateBy == context.CurrentUserId
            );
        modelBuilder
            .Entity<DurableExecutionEventRecord>()
            .HasQueryFilter(
                UserScopeQueryFilterNames.UserScope,
                entry =>
                    context.UserScopeBypass
                    || context.UserScopeIsActive
                        && context
                            .Set<DurableExecutionRecord>()
                            .Any(execution =>
                                execution.Id == entry.ExecutionId && execution.UserId == context.CurrentUserId
                            )
            );
    }

    public static IQueryable<TEntity> IgnoreUserScope<TEntity>(this IQueryable<TEntity> query)
        where TEntity : class
    {
        return query.IgnoreQueryFilters([UserScopeQueryFilterNames.UserScope]);
    }
}
