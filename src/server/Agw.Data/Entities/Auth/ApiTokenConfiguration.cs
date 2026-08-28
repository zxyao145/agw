using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Auth;

public class ApiTokenConfiguration : IEntityTypeConfiguration<ApiToken>
{
    public void Configure(EntityTypeBuilder<ApiToken> builder)
    {
        builder.ToTable(table => table.HasComment("Stores hashed API tokens used by external Agw clients."));
        builder.HasKey(entity => entity.Id);
        builder.HasIndex(entity => new { entity.CreateBy, entity.NormalizedName }).IsUnique();
        builder.HasIndex(entity => entity.Prefix);
        builder.Property(entity => entity.Name).IsRequired().HasMaxLength(64);
        builder.Property(entity => entity.NormalizedName).IsRequired().HasMaxLength(64);
        builder.Property(entity => entity.Prefix).IsRequired().HasMaxLength(12);
        builder.Property(entity => entity.SecretHash).IsRequired().HasMaxLength(64);
        builder.Property(entity => entity.CreateBy).IsRequired().HasMaxLength(128);
    }
}
