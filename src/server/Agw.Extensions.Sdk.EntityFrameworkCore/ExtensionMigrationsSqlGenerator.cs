using Agw.Extensions.Sdk.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Migrations;

namespace Agw.Extensions.Sdk.EntityFrameworkCore;

internal sealed class ExtensionSqliteMigrationsSqlGenerator : SqliteMigrationsSqlGenerator
{
    private readonly ExtensionDatabaseSchemaMetadata _schemaMetadata;
    private readonly IRelationalTypeMappingSource _typeMappingSource;

    public ExtensionSqliteMigrationsSqlGenerator(
        MigrationsSqlGeneratorDependencies dependencies,
        IRelationalAnnotationProvider annotationProvider,
        IRelationalTypeMappingSource typeMappingSource,
        ExtensionDatabaseSchemaMetadata schemaMetadata
    )
        : base(dependencies, annotationProvider)
    {
        _schemaMetadata = schemaMetadata;
        _typeMappingSource = typeMappingSource;
    }

    public override IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default
    )
    {
        if (
            ExtensionMigrationSqlGenerationValidator.IsHistoryScript(
                operations,
                model,
                Dependencies.CurrentContext.Context
            )
        )
            return base.Generate(operations, model, options);
        ExtensionMigrationSqlGenerationValidator.Validate(operations, model, _schemaMetadata, _typeMappingSource);
        return base.Generate(operations, model, options);
    }

    protected override void Generate(
        CreateTableOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true
    )
    {
        // SQLite 重建表时在实际生成操作中移除物理外键，保留 EF 导航模型。
        // Removes physical foreign keys from SQLite rebuild operations while retaining EF navigation metadata.
        operation.ForeignKeys.Clear();
        base.Generate(operation, model, builder, terminate);
    }
}

#pragma warning disable EF1001 // Provider 扩展点使用固定版本的服务类型。Provider extension points use version-pinned service types.
internal sealed class ExtensionNpgsqlMigrationsSqlGenerator : NpgsqlMigrationsSqlGenerator
{
    private readonly ExtensionDatabaseSchemaMetadata _schemaMetadata;
    private readonly IRelationalTypeMappingSource _typeMappingSource;

    public ExtensionNpgsqlMigrationsSqlGenerator(
        MigrationsSqlGeneratorDependencies dependencies,
        INpgsqlSingletonOptions singletonOptions,
        IRelationalTypeMappingSource typeMappingSource,
        ExtensionDatabaseSchemaMetadata schemaMetadata
    )
        : base(dependencies, singletonOptions)
    {
        _schemaMetadata = schemaMetadata;
        _typeMappingSource = typeMappingSource;
    }

    public override IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default
    )
    {
        if (
            ExtensionMigrationSqlGenerationValidator.IsHistoryScript(
                operations,
                model,
                Dependencies.CurrentContext.Context
            )
        )
            return base.Generate(operations, model, options);
        ExtensionMigrationSqlGenerationValidator.Validate(operations, model, _schemaMetadata, _typeMappingSource);
        return base.Generate(operations, model, options);
    }
}

#pragma warning restore EF1001

internal static class ExtensionMigrationSqlGenerationValidator
{
    internal static bool IsHistoryScript(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model,
        DbContext context
    ) =>
        model is null
        && operations is [SqlOperation script]
        && script.Sql == context.GetService<IHistoryRepository>().GetCreateIfNotExistsScript();

    public static void Validate(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model,
        ExtensionDatabaseSchemaMetadata options,
        IRelationalTypeMappingSource typeMappingSource
    )
    {
        if (
            model?.FindEntityType(typeof(HistoryRow)) is not null
            && model
                .GetRelationalModel()
                .Tables.All(table =>
                    table.Name == $"{options.TablePrefix}__ef_migrations_history" && table.Schema is null
                )
        )
            return;

        if (model is not null)
        {
            ExtensionDatabaseModelValidator.Validate(model, options, typeMappingSource, requireOwnership: false);
        }
        ExtensionMigrationValidator.ValidateOperations(
            operations,
            options,
            model?.GetMaxIdentifierLength()
                ?? (options.Provider == ExtensionDatabaseProvider.PostgreSql ? 63 : int.MaxValue),
            typeMappingSource
        );
    }
}
