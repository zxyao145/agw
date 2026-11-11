namespace Agw.Extensions.Sdk.Database;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ExtensionMigrationProviderAttribute : Attribute
{
    public ExtensionMigrationProviderAttribute(ExtensionDatabaseProvider provider)
    {
        Provider = provider;
    }

    public ExtensionDatabaseProvider Provider { get; }
}
