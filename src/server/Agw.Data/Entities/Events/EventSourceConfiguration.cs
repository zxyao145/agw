using Agw.Shared.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Events;

public sealed class EventSourceConfiguration : IEntityTypeConfiguration<EventSource>
{
    public void Configure(EntityTypeBuilder<EventSource> builder)
    {
        builder.ToTable("event_source");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.CreateBy).IsRequired();
        builder.Property(entity => entity.Name).IsRequired();
        builder.Property(entity => entity.PublisherKey).HasColumnName("publisher_key");
        builder
            .Property(entity => entity.ManagedBy)
            .HasColumnName("managed_by")
            .IsRequired()
            .HasMaxLength(64)
            .HasDefaultValue(EventManagedBy.User);
        builder.Property(entity => entity.ManagedKey).HasColumnName("managed_key");
        builder.Property(entity => entity.Kind).HasConversion<int>().IsRequired();
        builder.Property(entity => entity.CeSource).IsRequired();
        builder.Property(entity => entity.ConfigJson).IsRequired();
        builder.Property(entity => entity.DeletedAt).IsRequired(false);
        builder.Property(entity => entity.Version).IsConcurrencyToken();
        builder
            .HasIndex(entity => new { entity.CreateBy, entity.PublisherKey })
            .IsUnique()
            .HasFilter("publisher_key IS NOT NULL");
        builder
            .HasIndex(entity => new
            {
                entity.CreateBy,
                entity.ManagedBy,
                entity.ManagedKey,
            })
            .IsUnique()
            .HasFilter("managed_key IS NOT NULL");
        builder.HasIndex(entity => new { entity.Enabled, entity.NextRunAt });
    }
}
