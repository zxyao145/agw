namespace Agw.Extensions.Application.Persistence;

public sealed record ExtensionEnablementState(bool Enabled, long Version);

public interface IExtensionEnablementStore
{
    Task<ExtensionEnablementState> GetAsync(Guid extensionId, CancellationToken cancellationToken = default);

    Task<ExtensionEnablementState> SetAsync(
        Guid extensionId,
        bool enabled,
        CancellationToken cancellationToken = default
    );
}
