using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Agw.Extensions.Sdk.EntityFrameworkCore;

public sealed class ExtensionUpdateAllRowsOperation : MigrationOperation
{
    public required string Table { get; init; }

    public required IReadOnlyList<KeyValuePair<string, object?>> Assignments { get; init; }
}
