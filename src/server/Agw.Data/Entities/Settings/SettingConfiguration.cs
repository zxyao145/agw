using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Settings;

public sealed class SettingConfiguration : IEntityTypeConfiguration<Setting>
{
    public void Configure(EntityTypeBuilder<Setting> builder)
    {
        builder.HasKey(setting => setting.Id);
        builder.Property(setting => setting.Key).IsRequired().HasMaxLength(128);
        builder.Property(setting => setting.UserId).HasMaxLength(128).HasColumnName("user_id");
        builder.Property(setting => setting.ValueJson).IsRequired().HasColumnType("text");
        builder.Property(setting => setting.Version).IsConcurrencyToken();
        builder.HasIndex(setting => setting.Key).IsUnique().HasFilter("user_id IS NULL");
        builder.HasIndex(setting => new { setting.UserId, setting.Key }).IsUnique().HasFilter("user_id IS NOT NULL");
    }
}
