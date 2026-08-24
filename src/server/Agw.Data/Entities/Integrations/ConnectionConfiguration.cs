using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Integrations;

public class ConnectionConfiguration : IEntityTypeConfiguration<Connection>
{
    public void Configure(EntityTypeBuilder<Connection> builder)
    {
        builder.ToTable(table =>
            table.HasComment("Represents an external account or service endpoint available to agents.")
        );
        builder.HasKey(entity => entity.Id);
        builder.HasIndex(entity => new { entity.CreateBy, entity.Alias }).IsUnique();
        builder.HasIndex(entity => entity.PluginId);
        builder.HasIndex(entity => entity.Status);
        builder.Property(entity => entity.CreateBy).IsRequired();
        builder.Property(entity => entity.PluginId).IsRequired().HasMaxLength(128);
        builder.Property(entity => entity.ConnectorId).IsRequired().HasMaxLength(128);
        builder.Property(entity => entity.AuthSchemeId).IsRequired().HasMaxLength(128);
        builder.Property(entity => entity.DisplayName).IsRequired().HasMaxLength(200);
        builder.Property(entity => entity.Alias).IsRequired().HasMaxLength(128);
        builder.Property(entity => entity.ConfigurationJson).IsRequired().HasMaxLength(16000);
        builder.Property(entity => entity.Status).HasConversion<string>().IsRequired().HasMaxLength(64);
        builder.Property(entity => entity.Subject).HasMaxLength(500);
        builder.Property(entity => entity.LastValidationErrorCode).HasMaxLength(128);
        builder.Property(entity => entity.ValidationMetadataJson).HasMaxLength(8000);
    }
}
