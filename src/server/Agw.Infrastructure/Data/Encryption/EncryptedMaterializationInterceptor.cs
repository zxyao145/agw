using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Agw.Infrastructure.Data.Encryption;

internal sealed class EncryptedMaterializationInterceptor : IMaterializationInterceptor
{
    public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
    {
        if (materializationData.Context is AgwDbContext context)
        {
            context.DecryptMaterializedEntity(entity);
        }

        return entity;
    }
}
