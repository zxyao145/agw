using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Executions;

/// <summary>
/// 配置 durable_execution 单行状态机的主键、并发版本、索引和字段约束。
/// </summary>
public sealed class DurableExecutionRecordConfiguration : IEntityTypeConfiguration<DurableExecutionRecord>
{
    /// <summary>
    /// 应用 durable execution 登记实体的 EF Core 映射。
    /// </summary>
    public void Configure(EntityTypeBuilder<DurableExecutionRecord> builder)
    {
        builder.HasKey(item => item.Id);
        builder.Property(item => item.UserId).HasMaxLength(128).IsRequired();
        builder.Property(item => item.ManifestJson).IsRequired();
        builder.Property(item => item.Status).IsRequired();
        builder.Property(item => item.StateChangedAt).IsRequired();
        builder.Property(item => item.StateVersion).IsConcurrencyToken().IsRequired();
        builder.HasIndex(item => new { item.Status, item.StateChangedAt });
    }
}
