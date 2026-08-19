using Agw.Auth.Application;
using Agw.Setup.Services;
using Agw.Shared.Runtime;
using Microsoft.AspNetCore.Identity;

namespace Agw.Host;

public static class ServerCommand
{
    public static async Task<bool> TryRunAsync(string[] args, AgwDataPaths paths)
    {
        if (
            args.Length != 2
            || !string.Equals(args[0], "auth", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(args[1], "reset-password", StringComparison.OrdinalIgnoreCase)
        )
        {
            return false;
        }

        if (!File.Exists(paths.StateFile))
        {
            Console.Error.WriteLine($"Server state was not found at {paths.StateFile}.");
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
        IAuthenticationStateStore authenticationStateStore = new JsonInitializationStateStore(paths);
        await authenticationStateStore.UpdatePasswordAsync(hasher.HashPassword(new object(), password));
        Console.WriteLine("Administrator password reset. Existing web sessions were invalidated.");
        return true;
    }
}
