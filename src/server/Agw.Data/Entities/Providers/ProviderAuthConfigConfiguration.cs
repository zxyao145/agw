using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Providers;

public class ProviderAuthConfigConfiguration : IEntityTypeConfiguration<ProviderAuthConfig>
{
    public void Configure(EntityTypeBuilder<ProviderAuthConfig> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.AuthType).HasConversion<int>();
        builder.Property(e => e.ApiKey).HasMaxLength(2000);
        builder.Property(e => e.EnvName).HasMaxLength(200);

        builder
            .HasOne(e => e.Provider)
            .WithMany(p => p.AuthConfigs)
            .HasForeignKey(e => e.ProviderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
