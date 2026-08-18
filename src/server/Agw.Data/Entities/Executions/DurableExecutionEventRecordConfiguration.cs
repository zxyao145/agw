using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Executions;

/// <summary>
/// 配置 execution_stream_entry 的主键、消息字段和确定性位置索引。
/// </summary>
public sealed class DurableExecutionEventRecordConfiguration : IEntityTypeConfiguration<DurableExecutionEventRecord>
{
    /// <summary>
    /// 应用 PostgreSQL execution 消息记录的 EF Core 映射。
    /// </summary>
    public void Configure(EntityTypeBuilder<DurableExecutionEventRecord> builder)
    {
        builder.HasKey(item => item.Id);
        builder.Property(item => item.ExecutionId).IsRequired();
        builder.Property(item => item.SegmentIndex).IsRequired();
        builder.Property(item => item.Sequence).IsRequired();
        builder.Property(item => item.PayloadJson).IsRequired();
        builder
            .HasIndex(item => new
            {
                item.ExecutionId,
                item.SegmentIndex,
                item.Sequence,
            })
            .IsUnique();
    }
}
