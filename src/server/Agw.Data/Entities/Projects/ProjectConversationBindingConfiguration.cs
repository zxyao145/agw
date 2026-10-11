using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Projects;

public class ProjectConversationBindingConfiguration : IEntityTypeConfiguration<ProjectConversationBinding>
{
    public void Configure(EntityTypeBuilder<ProjectConversationBinding> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.ExternalAgentName).IsRequired().HasMaxLength(200);
        builder.Property(e => e.ProviderSessionId).IsRequired().HasMaxLength(200);
        // 默认值只用于升级已有记录；EF 写入时总是发送实体上的值。列名固定，供生效记录索引的过滤条件引用。
        // The default only fills existing rows during upgrade; EF always sends the entity value on insert. The column name is fixed so the active-record index filter can reference it.
        builder
            .Property(e => e.IsActive)
            .IsRequired()
            .HasColumnName("is_active")
            .HasDefaultValue(true)
            .ValueGeneratedNever();

        builder
            .HasIndex(e => new
            {
                e.ProjectConversationId,
                e.AgentId,
                e.ExternalAgentName,
                e.ProviderSessionId,
            })
            .IsUnique()
            .HasDatabaseName("ux_project_conversation_binding_session");
        builder
            .HasIndex(e => new
            {
                e.ProjectConversationId,
                e.AgentId,
                e.ExternalAgentName,
            })
            .IsUnique()
            .HasFilter("is_active = TRUE")
            .HasDatabaseName("ux_project_conversation_binding_active");
        builder.HasIndex(e => new { e.ExternalAgentName, e.ProviderSessionId });

        builder
            .HasOne(e => e.ProjectConversation)
            .WithMany(conversation => conversation.Bindings)
            .HasForeignKey(e => e.ProjectConversationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
