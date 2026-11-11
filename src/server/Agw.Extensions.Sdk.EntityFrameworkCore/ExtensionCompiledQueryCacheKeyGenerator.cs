using System.Linq.Expressions;
using Agw.Extensions.Sdk.Database;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;

namespace Agw.Extensions.Sdk.EntityFrameworkCore;

internal sealed class ExtensionCompiledQueryCacheKeyGenerator(
    ICompiledQueryCacheKeyGenerator provider,
    ICurrentDbContext currentContext
) : ICompiledQueryCacheKeyGenerator
{
    public object GenerateCacheKey(Expression query, bool async) =>
        new CacheKey
        {
            ProviderKey = provider.GenerateCacheKey(query, async),
            IsCheckingOwnership = currentContext.Context is ExtensionDbContext { IsCheckingOwnership: true },
            IsScanningOutbox = currentContext.Context is ExtensionDbContext { IsScanningOutbox: true },
            IsReadingOwnedRecords = currentContext.Context is ExtensionDbContext { IsReadingOwnedRecords: true },
        };

    private sealed record CacheKey
    {
        public required object ProviderKey { get; init; }
        public required bool IsCheckingOwnership { get; init; }
        public required bool IsScanningOutbox { get; init; }
        public required bool IsReadingOwnedRecords { get; init; }
    }
}
