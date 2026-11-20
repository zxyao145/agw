using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;

namespace Agw.Extensions.Sdk.EntityFrameworkCore;

[SuppressMessage(
    "Usage",
    "EF1001:Internal EF Core API usage.",
    Justification = "The optional SDK pins EF Core within each extension generation."
)]
internal sealed class ExtensionNoForeignKeyModelDiffer : MigrationsModelDiffer
{
    public ExtensionNoForeignKeyModelDiffer(
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
        var operations = base.GetDifferences(source, target)
            .Where(operation => operation is not AddForeignKeyOperation and not DropForeignKeyOperation)
            .ToList();

        foreach (var createTable in operations.OfType<CreateTableOperation>())
        {
            createTable.ForeignKeys.Clear();
        }

        return operations;
    }
}
