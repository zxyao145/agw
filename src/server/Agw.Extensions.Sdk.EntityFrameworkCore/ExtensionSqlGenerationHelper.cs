using System.Text;
using Microsoft.EntityFrameworkCore.Storage;

namespace Agw.Extensions.Sdk.EntityFrameworkCore;

internal sealed class ExtensionSqlGenerationHelper : RelationalSqlGenerationHelper
{
    private const string TemporaryTableMarker = "ef_temp_";
    private readonly string _tablePrefix;
    private readonly ISqlGenerationHelper _providerHelper;

    public ExtensionSqlGenerationHelper(
        RelationalSqlGenerationHelperDependencies dependencies,
        string tablePrefix,
        ISqlGenerationHelper providerHelper
    )
        : base(dependencies)
    {
        _tablePrefix = tablePrefix;
        _providerHelper = providerHelper;
    }

    public override string StartTransactionStatement => _providerHelper.StartTransactionStatement;

    public override string DelimitIdentifier(string identifier) =>
        base.DelimitIdentifier(EnsureTemporaryPrefix(identifier));

    public override void DelimitIdentifier(StringBuilder builder, string identifier) =>
        base.DelimitIdentifier(builder, EnsureTemporaryPrefix(identifier));

    public override string DelimitIdentifier(string name, string? schema) =>
        _providerHelper.DelimitIdentifier(EnsureTemporaryPrefix(name), schema);

    public override void DelimitIdentifier(StringBuilder builder, string name, string? schema) =>
        _providerHelper.DelimitIdentifier(builder, EnsureTemporaryPrefix(name), schema);

    private string EnsureTemporaryPrefix(string identifier)
    {
        if (!identifier.StartsWith(TemporaryTableMarker, StringComparison.Ordinal))
        {
            return identifier;
        }

        string tableName = identifier[TemporaryTableMarker.Length..];
        if (tableName.StartsWith(_tablePrefix, StringComparison.Ordinal))
        {
            tableName = tableName[_tablePrefix.Length..];
        }

        return $"{_tablePrefix}{TemporaryTableMarker}{tableName}";
    }
}
