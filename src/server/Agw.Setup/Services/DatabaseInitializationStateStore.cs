using Agw.Auth.Contracts;
using Agw.Shared.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Setup.Services;

public sealed class DatabaseInitializationStateStore
    : IInitializationStateStore,
        IAuthenticationStateStore,
        IServerInitializationState
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile AuthenticationSnapshot? _snapshot;

    public DatabaseInitializationStateStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public bool IsInitialized => _snapshot is { PasswordHash: not null, SessionVersion: > 0 };

    public AuthenticationSnapshot GetAuthenticationSnapshot() => _snapshot ?? new(null, 0);

    public Task RefreshAsync(CancellationToken cancellationToken = default) => ExecuteAsync(null, cancellationToken);

    public Task PersistAsync(string passwordHash, CancellationToken cancellationToken = default) =>
        ExecuteAsync(store => store.InitializeAsync(passwordHash, cancellationToken), cancellationToken);

    public Task UpdatePasswordAsync(string passwordHash, CancellationToken cancellationToken = default) =>
        ExecuteAsync(store => store.UpdatePasswordAsync(passwordHash, cancellationToken), cancellationToken);

    private async Task ExecuteAsync(Func<IServerAuthStatePersistence, Task>? write, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IServerAuthStatePersistence>();
            if (write != null)
                await write(store);
            // 还没有读到管理员状态时，首次 Setup 之前的数据库或表可能不存在；读到之后存储一定存在，不再重复检查。
            // Before administrator state has been read, the database or table may not exist until the first Setup;
            // once it has been read the storage exists, so the check is skipped.
            else if (_snapshot == null && !await store.StorageExistsAsync(cancellationToken))
                return;
            _snapshot = await store.ReadAsync(cancellationToken);
        }
        catch
        {
            // Do not keep accepting stale administrator sessions when the database cannot be read.
            _snapshot = null;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }
}
