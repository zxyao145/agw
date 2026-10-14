namespace Agw.Auth.Contracts;

public interface IServerAuthStatePersistence
{
    /// <summary>
    /// Returns whether the database and the table holding the server authentication state exist.
    /// Both are missing until the first Setup applies the database migrations.
    /// 返回数据库和保存服务端认证状态的表是否存在；首次 Setup 执行数据库迁移之前两者都可能不存在。
    /// </summary>
    Task<bool> StorageExistsAsync(CancellationToken cancellationToken = default);
    Task<AuthenticationSnapshot?> ReadAsync(CancellationToken cancellationToken = default);
    Task InitializeAsync(string passwordHash, CancellationToken cancellationToken = default);
    Task UpdatePasswordAsync(string passwordHash, CancellationToken cancellationToken = default);
}
