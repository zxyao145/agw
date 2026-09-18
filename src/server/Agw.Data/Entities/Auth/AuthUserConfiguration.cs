using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Auth;

public sealed class AuthUserConfiguration : IEntityTypeConfiguration<AuthUser>
{
    public void Configure(EntityTypeBuilder<AuthUser> builder)
    {
        builder.HasKey(user => user.Id);
        builder.Property(user => user.Id).ValueGeneratedNever();
        builder.Property(user => user.DisplayName).IsRequired().HasMaxLength(256);
        builder.Property(user => user.Email).HasMaxLength(320);
        builder.Property(user => user.SessionVersion).HasDefaultValue(1);
        builder.Property(user => user.CreateBy).IsRequired().HasMaxLength(128);
        builder.HasData(
            new AuthUser
            {
                Id = 1001,
                DisplayName = "admin",
                SessionVersion = 1,
                CreateBy = "1001",
                CreateTime = new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero),
            }
        );
    }
}

public sealed class AuthExternalIdentityConfiguration : IEntityTypeConfiguration<AuthExternalIdentity>
{
    public void Configure(EntityTypeBuilder<AuthExternalIdentity> builder)
    {
        builder.HasKey(identity => identity.UserId);
        builder.Property(identity => identity.UserId).ValueGeneratedNever();
        builder.Property(identity => identity.ProviderId).IsRequired().HasMaxLength(64);
        builder.Property(identity => identity.Issuer).IsRequired().HasMaxLength(512);
        builder.Property(identity => identity.Subject).IsRequired().HasMaxLength(255);
        builder.Property(identity => identity.CreateBy).IsRequired().HasMaxLength(128);
        builder.HasIndex(identity => new { identity.Issuer, identity.Subject }).IsUnique();
    }
}

public sealed class AuthDesktopLoginGrantConfiguration : IEntityTypeConfiguration<AuthDesktopLoginGrant>
{
    public void Configure(EntityTypeBuilder<AuthDesktopLoginGrant> builder)
    {
        builder.HasKey(grant => grant.CodeHash);
        builder.Property(grant => grant.CodeHash).HasMaxLength(64);
        builder.Property(grant => grant.ProviderId).IsRequired().HasMaxLength(64);
        builder.Property(grant => grant.CodeChallenge).IsRequired().HasMaxLength(43);
        builder.Property(grant => grant.CreateBy).IsRequired().HasMaxLength(128);
        builder
            .Property(grant => grant.ExpiresAt)
            .HasColumnName("expires_at_ms")
            .HasConversion(
                value => value.ToUnixTimeMilliseconds(),
                value => DateTimeOffset.FromUnixTimeMilliseconds(value)
            );
        builder.HasIndex(grant => grant.ExpiresAt);
    }
}

public sealed class AuthUserIdSequenceConfiguration : IEntityTypeConfiguration<AuthUserIdSequence>
{
    public void Configure(EntityTypeBuilder<AuthUserIdSequence> builder)
    {
        builder.ToTable(
            "auth_user_id_sequence",
            table =>
            {
                table.HasCheckConstraint("ck_auth_user_id_sequence_singleton", "id = 1");
                table.HasCheckConstraint("ck_auth_user_id_sequence_start", "next_id >= 10000");
            }
        );
        builder.HasKey(sequence => sequence.Id);
        builder.Property(sequence => sequence.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(sequence => sequence.NextId).HasColumnName("next_id");
        builder.HasData(new AuthUserIdSequence { Id = 1, NextId = 10000 });
    }
}
