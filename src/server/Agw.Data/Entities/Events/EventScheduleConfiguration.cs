using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Events;

public sealed class EventScheduleConfiguration : IEntityTypeConfiguration<EventSchedule>
{
    public void Configure(EntityTypeBuilder<EventSchedule> builder)
    {
        builder.ToTable("event_schedule");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.CreateBy).IsRequired();
        builder.Property(entity => entity.CallerKey).IsRequired();
        builder.Property(entity => entity.BusinessKey).IsRequired();
        builder.Property(entity => entity.Kind).HasConversion<int?>();
        builder.Property(entity => entity.State).HasConversion<int>().IsRequired();
        builder.Property(entity => entity.DataIsBinary).IsRequired();
        builder.Property(entity => entity.RequestSeq).IsRequired();
        builder.Property(entity => entity.Revision).IsRequired();
        builder.Property(entity => entity.Occurrence).IsRequired();
        builder.Property(entity => entity.Hops).IsRequired();
        builder.Property(entity => entity.Version).IsConcurrencyToken();
        builder
            .HasIndex(entity => new
            {
                entity.CreateBy,
                entity.CallerKey,
                entity.BusinessKey,
            })
            .IsUnique();
        builder.HasIndex(entity => new { entity.State, entity.DueAt });
    }
}
