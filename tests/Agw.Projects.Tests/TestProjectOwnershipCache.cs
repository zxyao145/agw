using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Internal;

namespace Agw.Projects.Tests;

/// <summary>
/// <para>基于 HybridCache 默认实现创建 ProjectOwnershipCache，本地缓存过期和 tag 失效读取同一个时钟。</para>
/// <para>Creates ProjectOwnershipCache over the default HybridCache, with local expiration and tag invalidation reading the same clock.</para>
/// </summary>
internal static class TestProjectOwnershipCache
{
    public static ProjectOwnershipCache Create(TimeProvider timeProvider)
    {
        var services = new ServiceCollection();
        services.AddSingleton(timeProvider);
        services.AddMemoryCache(options => options.Clock = new TimeProviderClock(timeProvider));
        services.AddHybridCache();
        return new ProjectOwnershipCache(services.BuildServiceProvider().GetRequiredService<HybridCache>());
    }

    private sealed class TimeProviderClock : ISystemClock
    {
        private readonly TimeProvider _timeProvider;

        public TimeProviderClock(TimeProvider timeProvider)
        {
            _timeProvider = timeProvider;
        }

        public DateTimeOffset UtcNow => _timeProvider.GetUtcNow();
    }
}
