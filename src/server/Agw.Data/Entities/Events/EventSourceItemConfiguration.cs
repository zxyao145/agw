using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Events;

public sealed class EventSourceItemConfiguration : IEntityTypeConfiguration<EventSourceItem>
{
    public void Configure(EntityTypeBuilder<EventSourceItem> builder)
    {
        builder.ToTable("event_source_item");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.SourceId).IsRequired();
        builder.Property(entity => entity.ItemKey).IsRequired();
        builder.Property(entity => entity.Fingerprint).IsRequired();
        builder.Property(entity => entity.ChangeSeq).IsRequired();
        builder.Property(entity => entity.FirstSeenAt).IsRequired();
        builder.Property(entity => entity.LastSeenAt).IsRequired();
        builder.HasIndex(entity => new { entity.SourceId, entity.ItemKey }).IsUnique();
    }
}
