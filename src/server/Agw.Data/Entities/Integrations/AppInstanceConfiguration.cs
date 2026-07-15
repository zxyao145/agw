using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Integrations;

public class AppInstanceConfiguration : IEntityTypeConfiguration<AppInstance>
{
    public void Configure(EntityTypeBuilder<AppInstance> builder)
    {
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.AppName).IsUnique(false);
        builder.HasIndex(e => e.ClientId).IsUnique();
        builder.Property(e => e.AppName).IsRequired().HasMaxLength(128);
        builder.Property(e => e.ClientId).IsRequired().HasMaxLength(200);
        builder.Property(e => e.ClientSecret).IsRequired().HasMaxLength(2000);

        builder.HasOne(e => e.AuthorizationToken)
            .WithOne()
            .HasForeignKey<OAuthAuthorizationToken>(e => e.AppInstanceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
