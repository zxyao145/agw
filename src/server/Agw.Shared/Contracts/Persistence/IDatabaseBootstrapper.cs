namespace Agw.Shared.Contracts.Persistence;

/// <summary>
/// <para>按当前生效的数据库配置初始化数据库（迁移与种子数据）。</para>
/// <para>Initializes the database (migrations and seed data) using the effective database configuration.</para>
/// </summary>
public interface IDatabaseBootstrapper
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
