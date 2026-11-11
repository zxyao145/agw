using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Events;

public sealed class EventDeliveryAttemptConfiguration : IEntityTypeConfiguration<EventDeliveryAttempt>
{
    public void Configure(EntityTypeBuilder<EventDeliveryAttempt> builder)
    {
        builder.ToTable("event_delivery_attempt");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.DeliveryId).IsRequired();
        builder.Property(entity => entity.Attempt).IsRequired();
        builder.Property(entity => entity.HostId).IsRequired();
        builder.Property(entity => entity.StartedAt).IsRequired();
        builder.Property(entity => entity.EndedAt).IsRequired();
        builder.Property(entity => entity.Result).HasConversion<int>().IsRequired();
    }
}
