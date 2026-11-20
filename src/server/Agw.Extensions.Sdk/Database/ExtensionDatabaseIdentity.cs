namespace Agw.Extensions.Sdk.Database;

public sealed record ExtensionDatabaseIdentity(Guid ExtensionId, Guid GenerationId, long Epoch)
    : IExtensionDatabaseIdentity
{
    public string TablePrefix => $"zext_{ExtensionId:N}_";
}
