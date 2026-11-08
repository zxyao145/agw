using Agw.Extensions.Sdk.Database;
using Agw.Shared.Exceptions;

namespace Agw.Extensions.Sdk.EntityFrameworkCore;

public static class ExtensionDatabaseRegistrationExtensions
{
    public static void RegisterDbContext<TContext, TMigrationsAssemblyMarker>(this IExtensionRegistration registration)
        where TContext : ExtensionDbContext
        where TMigrationsAssemblyMarker : class
    {
        if (registration is null)
        {
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
        }
        registration.RegisterDatabase(new ExtensionDatabaseFactory<TContext, TMigrationsAssemblyMarker>());
    }
}
