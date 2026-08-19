using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;

namespace Agw.Infrastructure.Data;

/// <summary>
/// 对迁移操作进行自定义处理，忽略外键的添加和删除操作
/// </summary>
[SuppressMessage("Usage", "EF1001:Internal EF Core API usage.", Justification = "<挂起>")]
public class NoForeignKeyModelDiffer : MigrationsModelDiffer
{
    public NoForeignKeyModelDiffer(
        IRelationalTypeMappingSource typeMappingSource,
        IMigrationsAnnotationProvider migrationsAnnotationProvider,
        IRelationalAnnotationProvider relationalAnnotationProvider,
        IRowIdentityMapFactory rowIdentityMapFactory,
        CommandBatchPreparerDependencies commandBatchPreparerDependencies
    )
        : base(
            typeMappingSource,
            migrationsAnnotationProvider,
            relationalAnnotationProvider,
            rowIdentityMapFactory,
            commandBatchPreparerDependencies
        ) { }

    public override IReadOnlyList<MigrationOperation> GetDifferences(IRelationalModel? source, IRelationalModel? target)
    {
        // 过滤掉所有外键相关操作
        var operations = base.GetDifferences(source, target)
            .Where(op => op is not AddForeignKeyOperation)
            .Where(op => op is not DropForeignKeyOperation)
            .ToList();

        foreach (var operation in operations.OfType<CreateTableOperation>())
            operation.ForeignKeys?.Clear();

        return operations;
    }
}
