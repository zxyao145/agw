using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Providers;

public class ModelProviderRelationConfiguration : IEntityTypeConfiguration<ModelProviderRelation>
{
    public void Configure(EntityTypeBuilder<ModelProviderRelation> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.InputPrice).HasColumnType("decimal(18,4)");
        builder.Property(e => e.OutputPrice).HasColumnType("decimal(18,4)");
        builder.Property(e => e.CacheRead).HasColumnType("decimal(18,4)");
        builder.Property(e => e.CacheWrite).HasColumnType("decimal(18,4)");

        builder.HasOne(e => e.Model)
            .WithMany(m => m.Providers)
            .HasForeignKey(e => e.ModelId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.Provider)
            .WithMany(p => p.Models)
            .HasForeignKey(e => e.ProviderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
