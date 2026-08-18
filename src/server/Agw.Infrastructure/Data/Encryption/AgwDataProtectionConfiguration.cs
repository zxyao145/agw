using Microsoft.AspNetCore.DataProtection;

namespace Agw.Infrastructure.Data.Encryption;

public static class AgwDataProtectionConfiguration
{
    public const string ApplicationName = "Agw";

    public static IDataProtectionBuilder ConfigureAgwApplication(this IDataProtectionBuilder builder)
    {
        return builder.SetApplicationName(ApplicationName);
    }

    public static IDataProtectionProvider CreatePersistedProvider(DirectoryInfo keysDirectory)
    {
        return DataProtectionProvider.Create(keysDirectory, builder => builder.ConfigureAgwApplication());
    }
}
