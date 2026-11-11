using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Events;

public sealed class EventOutboxConfiguration : IEntityTypeConfiguration<EventOutbox>
{
    public void Configure(EntityTypeBuilder<EventOutbox> builder)
    {
        builder.ToTable("event_outbox");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Topic).IsRequired();
        builder.Property(entity => entity.EntityId).IsRequired();
        builder.Property(entity => entity.State).HasConversion<int>().IsRequired();
        builder.HasIndex(entity => new { entity.State, entity.PublishedAt });
        builder.HasIndex(entity => new { entity.Topic, entity.EntityId });
    }
}
