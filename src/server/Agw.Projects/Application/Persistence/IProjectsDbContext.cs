using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Application.Persistence;

public interface IProjectsDbContext : IModuleDbContext
{
    Task<int> SaveConversationChangesAsync(
        Guid conversationId,
        int expectedGeneration,
        CancellationToken cancellationToken = default
    );

    DbSet<Project> Projects { get; }

    DbSet<ProjectSkillRelation> ProjectSkillRelations { get; }

    DbSet<ProjectMcpServerRelation> ProjectMcpToolServers { get; }

    DbSet<ProjectConnectionRelation> ProjectConnectionRelations { get; }

    DbSet<ProjectConversation> ProjectConversations { get; }

    DbSet<ProjectConversationChatHistory> ProjectConversationChatHistories { get; }

    DbSet<ProjectConversationBinding> ProjectConversationBindings { get; }

    DbSet<AgentUsage> AgentUsages { get; }
}
