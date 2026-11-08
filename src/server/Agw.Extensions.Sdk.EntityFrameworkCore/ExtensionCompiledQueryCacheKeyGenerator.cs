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
        new CacheKey(
            provider.GenerateCacheKey(query, async),
            currentContext.Context is ExtensionDbContext { IsCheckingOwnership: true }
        );

    private sealed record CacheKey(object ProviderKey, bool IsCheckingOwnership);
}
