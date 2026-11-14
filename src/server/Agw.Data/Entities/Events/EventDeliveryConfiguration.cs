using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Events;

public sealed class EventDeliveryConfiguration : IEntityTypeConfiguration<EventDelivery>
{
    public void Configure(EntityTypeBuilder<EventDelivery> builder)
    {
        builder.ToTable("event_delivery");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.CreateBy).IsRequired();
        builder.Property(entity => entity.State).HasConversion<int>().IsRequired();
        builder.Property(entity => entity.Attempt).IsRequired();
        builder.Property(entity => entity.RecoverAfter).IsRequired();
        builder.Property(entity => entity.Version).IsConcurrencyToken();
        builder.HasIndex(entity => new { entity.EventId, entity.SubscriptionId }).IsUnique();
        builder.HasIndex(entity => new { entity.SubscriptionId, entity.Id });
        builder.HasIndex(entity => new { entity.State, entity.NextAttemptAt });
        builder.HasIndex(entity => new { entity.State, entity.RecoverAfter });
        builder.HasIndex(entity => entity.TurnId);
    }
}
