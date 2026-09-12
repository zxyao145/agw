using Agw.Auth.Contracts;
using Agw.Setup.Services;
using Microsoft.AspNetCore.Identity;

namespace Agw.Host;

public static class ServerCommand
{
    public static async Task<bool> TryRunAsync(string[] args, DatabaseInitializationStateStore stateStore)
    {
        if (
            args.Length != 2
            || !string.Equals(args[0], "auth", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(args[1], "reset-password", StringComparison.OrdinalIgnoreCase)
        )
        {
            return false;
        }

        if (!stateStore.IsInitialized)
        {
            Console.Error.WriteLine("The configured database has no initialized administrator.");
            Environment.ExitCode = 2;
            return true;
        }

        Console.Write("New administrator password: ");
        var password = Console.ReadLine() ?? string.Empty;
        if (password.Length is < 12 or > 256)
        {
            Console.Error.WriteLine("Password must be between 12 and 256 characters.");
            Environment.ExitCode = 2;
            return true;
        }

        var hasher = new PasswordHasher<object>();
        IAuthenticationStateStore authenticationStateStore = stateStore;
        await authenticationStateStore.UpdatePasswordAsync(hasher.HashPassword(new object(), password));
        Console.WriteLine("Administrator password reset. Existing web sessions were invalidated.");
        return true;
    }
}
