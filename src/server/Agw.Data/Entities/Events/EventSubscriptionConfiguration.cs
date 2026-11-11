using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Events;

public sealed class EventSubscriptionConfiguration : IEntityTypeConfiguration<EventSubscription>
{
    public void Configure(EntityTypeBuilder<EventSubscription> builder)
    {
        builder.ToTable("event_subscription");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.CreateBy).IsRequired();
        builder.Property(entity => entity.Name).IsRequired();
        builder.Property(entity => entity.SourceIdsJson).IsRequired();
        builder.Property(entity => entity.EventTypesJson).IsRequired();
        builder.Property(entity => entity.TargetKind).HasConversion<int>().IsRequired();
        builder.Property(entity => entity.ManagedState).HasConversion<int?>();
        builder.Property(entity => entity.ManagedKey).HasColumnName("managed_key");
        builder.Property(entity => entity.Version).IsConcurrencyToken();
        builder.HasIndex(entity => new { entity.CreateBy, entity.Enabled });
        builder
            .HasIndex(entity => new
            {
                entity.CreateBy,
                entity.ManagedByExtensionId,
                entity.ManagedKey,
            })
            .IsUnique()
            .HasFilter("managed_key IS NOT NULL");
    }
}
