using Agw.Extensions.Sdk.Database;

namespace Agw.Extensions.Sdk.EntityFrameworkCore;

internal sealed record ExtensionDatabaseSchemaMetadata(ExtensionDatabaseProvider Provider, string TablePrefix);
