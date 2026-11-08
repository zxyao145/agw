namespace Agw.Extensions.Sdk.Database;

public interface IExtensionOwnedEntity
{
    string? CreateBy { get; set; }

    DateTimeOffset CreateTime { get; set; }

    string? UpdateBy { get; set; }

    DateTimeOffset? UpdateTime { get; set; }
}

public interface IExtensionOwnedChild<TRoot, TRootKey> : IExtensionOwnedEntity
    where TRoot : class, IExtensionOwnedEntity
{
    TRootKey RootId { get; set; }
}
