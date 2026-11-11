using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Events;

public sealed class EventRecordConfiguration : IEntityTypeConfiguration<EventRecord>
{
    public void Configure(EntityTypeBuilder<EventRecord> builder)
    {
        builder.ToTable("event");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.CreateBy).IsRequired();
        builder.Property(entity => entity.CeSpecVersion).IsRequired();
        builder.Property(entity => entity.CeSource).IsRequired();
        builder.Property(entity => entity.CeId).IsRequired();
        builder.Property(entity => entity.Type).IsRequired();
        builder.Property(entity => entity.DispatchState).HasConversion<int>().IsRequired();
        builder.Property(entity => entity.ReceivedAt).IsRequired();
        builder.Property(entity => entity.ChainId).IsRequired();
        builder.Property(entity => entity.Hops).IsRequired();
        builder.Property(entity => entity.MatchedCount).IsRequired();
        builder.Property(entity => entity.RecoverAfter).IsRequired();
        builder
            .HasIndex(entity => new
            {
                entity.SourceId,
                entity.CeSource,
                entity.CeId,
            })
            .IsUnique();
        builder.HasIndex(entity => new { entity.DispatchState, entity.RecoverAfter });
        builder.HasIndex(entity => new { entity.CreateBy, entity.ReceivedAt });
        builder.HasIndex(entity => new
        {
            entity.CreateBy,
            entity.Type,
            entity.ReceivedAt,
        });
        builder.HasIndex(entity => entity.ChainId);
    }
}
