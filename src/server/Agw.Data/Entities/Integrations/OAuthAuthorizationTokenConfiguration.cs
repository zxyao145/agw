using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Integrations;

public class OAuthAuthorizationTokenConfiguration : IEntityTypeConfiguration<OAuthAuthorizationToken>
{
    public void Configure(EntityTypeBuilder<OAuthAuthorizationToken> builder)
    {
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.AppInstanceId).IsUnique();
        builder.HasIndex(e => e.ExpiresAtUtc);
        builder.Property(e => e.AppInstanceId).IsRequired();
        builder.Property(e => e.Subject).IsRequired().HasMaxLength(200);
        builder.Property(e => e.AccessToken).IsRequired().HasMaxLength(4000);
        builder.Property(e => e.RefreshToken).HasMaxLength(4000);
        builder.Property(e => e.TokenType).IsRequired().HasMaxLength(50);
    }
}
