using Microsoft.Extensions.Caching.Hybrid;

namespace Agw.Projects.Application;

/// <summary>
/// <para>在 HybridCache 中短期保存校验通过的项目所有权，文件路径校验命中缓存时不再查询数据库。</para>
/// <para>Keeps verified project ownership in HybridCache for a short time so file path validation skips the database on a hit.</para>
/// </summary>
/// <remarks>
/// <para>只缓存校验成功的结果；删除项目时按项目 tag 清除当前进程的缓存，其他进程中的缓存最多保留一个 <see cref="Lifetime"/>。</para>
/// <para>Only successful checks are cached; deleting a Project clears this process's entries by Project tag, and entries in other processes expire within one <see cref="Lifetime"/>.</para>
/// </remarks>
public sealed class ProjectOwnershipCache
{
    /// <summary>
    /// <para>覆盖一次目录列举或搜索中的连续校验，同时限制其他进程在项目删除后继续放行的时间。</para>
    /// <para>Covers the consecutive checks of one listing or search and bounds how long other processes keep admitting a deleted Project.</para>
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    private const string CacheKeyPrefix = "agw:projects:ownership:";

    private static readonly HybridCacheEntryOptions VerifiedEntry = new()
    {
        Expiration = Lifetime,
        LocalCacheExpiration = Lifetime,
    };

    private static readonly HybridCacheEntryOptions VerifiedRead = new()
    {
        Flags =
            HybridCacheEntryFlags.DisableUnderlyingData
            | HybridCacheEntryFlags.DisableLocalCacheWrite
            | HybridCacheEntryFlags.DisableDistributedCacheWrite,
    };

    private readonly HybridCache _cache;

    public ProjectOwnershipCache(HybridCache cache)
    {
        _cache = cache;
    }

    public ValueTask<bool> IsVerifiedAsync(string userId, Guid projectId, CancellationToken cancellationToken) =>
        _cache.GetOrCreateAsync(
            GetKey(userId, projectId),
            static _ => ValueTask.FromResult(false),
            VerifiedRead,
            cancellationToken: cancellationToken
        );

    public ValueTask MarkVerifiedAsync(string userId, Guid projectId, CancellationToken cancellationToken) =>
        _cache.SetAsync(GetKey(userId, projectId), true, VerifiedEntry, [GetProjectTag(projectId)], cancellationToken);

    public ValueTask InvalidateAsync(Guid projectId) =>
        _cache.RemoveByTagAsync(GetProjectTag(projectId), CancellationToken.None);

    private static string GetKey(string userId, Guid projectId) => $"{GetProjectTag(projectId)}:{userId}";

    private static string GetProjectTag(Guid projectId) => $"{CacheKeyPrefix}{projectId:N}";
}
