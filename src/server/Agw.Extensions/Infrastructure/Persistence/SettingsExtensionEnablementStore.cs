using Agw.Extensions.Application.Persistence;
using Agw.Settings.Contracts;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Extensions.Infrastructure.Persistence;

public sealed class SettingsExtensionEnablementStore : IExtensionEnablementStore
{
    private readonly IServiceScopeFactory _scopeFactory;

    public SettingsExtensionEnablementStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<ExtensionEnablementState> GetAsync(
        Guid extensionId,
        CancellationToken cancellationToken = default
    )
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        ISettingsStore settings = scope.ServiceProvider.GetRequiredService<ISettingsStore>();
        SettingSnapshot<ExtensionEnablementSetting>? snapshot =
            await settings.GetGlobalAsync<ExtensionEnablementSetting>(GetKey(extensionId), cancellationToken);
        return snapshot is null
            ? new ExtensionEnablementState(false, 0)
            : new ExtensionEnablementState(snapshot.Value.Enabled, snapshot.Version);
    }

    public async Task<ExtensionEnablementState> SetAsync(
        Guid extensionId,
        bool enabled,
        CancellationToken cancellationToken = default
    )
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        ISettingsStore settings = scope.ServiceProvider.GetRequiredService<ISettingsStore>();
        string key = GetKey(extensionId);
        SettingSnapshot<ExtensionEnablementSetting>? snapshot =
            await settings.GetGlobalAsync<ExtensionEnablementSetting>(key, cancellationToken);
        if (snapshot is null)
        {
            if (!enabled)
            {
                return new ExtensionEnablementState(false, 0);
            }

            if (
                await settings.TryCreateGlobalAsync(
                    key,
                    new ExtensionEnablementSetting { Enabled = true },
                    cancellationToken
                )
            )
            {
                return new ExtensionEnablementState(true, 1);
            }

            snapshot = await settings.GetGlobalAsync<ExtensionEnablementSetting>(key, cancellationToken);
            if (snapshot is null || !snapshot.Value.Enabled)
            {
                throw new AgwException(ErrorCodes.ExtensionOperationConflict);
            }

            return new ExtensionEnablementState(true, snapshot.Version);
        }

        if (snapshot.Value.Enabled == enabled)
        {
            return new ExtensionEnablementState(enabled, snapshot.Version);
        }

        if (
            !await settings.TryUpdateGlobalAsync(
                key,
                new ExtensionEnablementSetting { Enabled = enabled },
                snapshot.Version,
                cancellationToken
            )
        )
        {
            var current = await settings.GetGlobalAsync<ExtensionEnablementSetting>(key, cancellationToken);
            if (current?.Value.Enabled == enabled)
                return new ExtensionEnablementState(enabled, current.Version);
            throw new AgwException(ErrorCodes.ExtensionOperationConflict);
        }

        return new ExtensionEnablementState(enabled, snapshot.Version + 1);
    }

    private static string GetKey(Guid extensionId) => $"extensions.enabled.{extensionId:D}";

    private sealed class ExtensionEnablementSetting
    {
        public ExtensionEnablementSetting() { }

        public bool Enabled { get; init; }
    }
}
