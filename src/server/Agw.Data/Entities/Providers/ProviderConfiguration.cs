using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Providers;

public class ProviderConfiguration : IEntityTypeConfiguration<Provider>
{
    public void Configure(EntityTypeBuilder<Provider> builder)
    {
        builder.HasKey(e => e.Id);
        builder
            .HasIndex(e => new
            {
                e.CreateBy,
                e.Name,
                e.ProviderType,
            })
            .IsUnique();
        builder.Property(e => e.Name).IsRequired().HasMaxLength(64);
        builder.Property(e => e.Endpoint).IsRequired().HasMaxLength(500);
        builder.Property(e => e.Description).HasMaxLength(1000);
    }
}
