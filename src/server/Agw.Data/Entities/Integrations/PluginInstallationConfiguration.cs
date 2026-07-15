using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Integrations;

public class PluginInstallationConfiguration : IEntityTypeConfiguration<PluginInstallation>
{
    public void Configure(EntityTypeBuilder<PluginInstallation> builder)
    {
        builder.ToTable(table => table.HasComment("Stores platform-wide plugin installation configuration."));
        builder.HasKey(entity => entity.Id);
        builder.HasIndex(entity => entity.PluginId).IsUnique();
        builder.Property(entity => entity.PluginId).IsRequired().HasMaxLength(128);
        builder.Property(entity => entity.ConfigurationJson).IsRequired().HasMaxLength(16000);
    }
}
