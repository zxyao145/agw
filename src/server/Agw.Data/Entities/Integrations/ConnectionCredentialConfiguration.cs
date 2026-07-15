using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Integrations;

public class ConnectionCredentialConfiguration : IEntityTypeConfiguration<ConnectionCredential>
{
    public void Configure(EntityTypeBuilder<ConnectionCredential> builder)
    {
        builder.ToTable(table => table.HasComment("Stores protected credentials owned by an integration connection."));
        builder.HasKey(entity => entity.Id);
        builder.HasIndex(entity => new { entity.ConnectionId, entity.Slot }).IsUnique();
        builder.HasIndex(entity => entity.ExpiresAtUtc);
        builder.Property(entity => entity.Slot).IsRequired().HasMaxLength(512);
        builder.Property(entity => entity.ProtectedValue).IsRequired().HasMaxLength(16000);
        builder.Property(entity => entity.MetadataJson).HasMaxLength(8000);

        builder.HasOne(entity => entity.Connection)
            .WithMany(connection => connection.Credentials)
            .HasForeignKey(entity => entity.ConnectionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
