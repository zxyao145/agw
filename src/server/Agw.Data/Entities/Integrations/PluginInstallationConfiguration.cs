using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Integrations;

public class PluginInstallationConfiguration : IEntityTypeConfiguration<PluginInstallation>
{
    public void Configure(EntityTypeBuilder<PluginInstallation> builder)
    {
        builder.ToTable(table => table.HasComment("Stores per-user plugin installation setup."));
        builder.HasKey(entity => entity.Id);
        builder.HasIndex(entity => new { entity.CreateBy, entity.PluginId }).IsUnique();
        // CreateBy is a stable owner identifier. Keep the provider-native text
        // type so historical owner values are preserved during backfill.
        builder.Property(entity => entity.CreateBy).IsRequired();
        builder.Property(entity => entity.PluginId).IsRequired().HasMaxLength(128);
        builder.Property(entity => entity.ConfigurationJson).IsRequired().HasMaxLength(16000);
    }
}
