using Agw.Extensions.Sdk.Database;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Agw.Extensions.Sdk.EntityFrameworkCore;

public sealed class ExtensionModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
    {
        if (context is not ExtensionDbContext extensionContext)
        {
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
        }

        var identity = extensionContext.ExtensionDatabaseIdentity;
        return new ModelCacheKey(context.GetType(), identity.ExtensionId, identity.GenerationId, designTime);
    }

    private sealed record ModelCacheKey(Type ContextType, Guid ExtensionId, Guid GenerationId, bool DesignTime);
}
