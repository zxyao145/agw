using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Executions;

/// <summary>
/// 配置 execution_stream_entry 的主键、消息字段和 Turn 内唯一序号。
/// Configures the key, message fields and unique in-turn sequence of execution_stream_entry.
/// </summary>
public sealed class DurableExecutionEventRecordConfiguration : IEntityTypeConfiguration<DurableExecutionEventRecord>
{
    public void Configure(EntityTypeBuilder<DurableExecutionEventRecord> builder)
    {
        builder.HasKey(item => item.Id);
        builder.Property(item => item.TurnId).IsRequired();
        builder.Property(item => item.TurnSequence).IsRequired();
        builder.Property(item => item.LeaseEpoch).IsRequired();
        builder.Property(item => item.SegmentIndex).IsRequired();
        builder.Property(item => item.PayloadJson).IsRequired();
        builder.HasIndex(item => new { item.TurnId, item.TurnSequence }).IsUnique();
    }
}
