using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Executions;

/// <summary>
/// 配置 execution_steer 的主键、字段与按 Turn 读取等待输入的索引。
/// Configures the key, fields and the index for reading a turn's waiting inputs of execution_steer.
/// </summary>
public sealed class DurableExecutionSteerRecordConfiguration : IEntityTypeConfiguration<DurableExecutionSteerRecord>
{
    public void Configure(EntityTypeBuilder<DurableExecutionSteerRecord> builder)
    {
        builder.HasKey(item => item.Id);
        builder.Property(item => item.TurnId).IsRequired();
        builder.Property(item => item.Status).IsRequired();
        builder.Property(item => item.InputJson).IsRequired();
        builder.Property(item => item.ReturnReason).HasMaxLength(64);
        builder.HasIndex(item => new { item.TurnId, item.Status });
    }
}
