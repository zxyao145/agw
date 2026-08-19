using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Integrations;

public class PluginInstallationCredentialConfiguration : IEntityTypeConfiguration<PluginInstallationCredential>
{
    public void Configure(EntityTypeBuilder<PluginInstallationCredential> builder)
    {
        builder.ToTable(table => table.HasComment("Stores protected credentials owned by a plugin installation."));
        builder.HasKey(entity => entity.Id);
        builder.HasIndex(entity => new { entity.PluginInstallationId, entity.Slot }).IsUnique();
        builder.Property(entity => entity.Slot).IsRequired().HasMaxLength(512);
        builder.Property(entity => entity.Value).HasColumnName("protected_value").IsRequired().HasMaxLength(16000);

        builder
            .HasOne(entity => entity.PluginInstallation)
            .WithMany(installation => installation.Credentials)
            .HasForeignKey(entity => entity.PluginInstallationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
