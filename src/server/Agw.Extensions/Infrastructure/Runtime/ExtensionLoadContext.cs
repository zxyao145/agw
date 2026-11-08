using System.Reflection;
using System.Runtime.Loader;
using Agw.Shared.Exceptions;

namespace Agw.Extensions.Infrastructure.Runtime;

internal sealed class ExtensionLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public ExtensionLoadContext(string entryAssemblyPath)
        : base($"Agw.Extension.{Guid.CreateVersion7():N}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsSharedAssembly(assemblyName.Name))
            return Default.LoadFromAssemblyName(assemblyName);

        string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (IsPrivateDatabaseAssembly(assemblyName.Name) && assemblyPath is null)
            throw new AgwException(
                ErrorCodes.ExtensionPackageInvalid,
                $"Extension database dependency '{assemblyName.Name}' is missing."
            );
        return assemblyPath is null ? null : LoadFromAssemblyPath(assemblyPath);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return libraryPath is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(libraryPath);
    }

    private static bool IsSharedAssembly(string? name) =>
        name
            is "Agw.Extensions.Sdk"
                or "Agw.Extensions.Contracts"
                or "Agw.Projects.Contracts"
                or "Agw.Skills.Contracts"
                or "Agw.Shared"
                or "Agw.Tools.Abstractions"
                or "Microsoft.Agents.AI"
                or "Microsoft.Extensions.AI"
                or "Microsoft.Extensions.AI.Abstractions"
                or "Microsoft.Extensions.DependencyInjection.Abstractions"
                or "Microsoft.Extensions.Logging.Abstractions"
                or "Microsoft.Data.Sqlite"
        || name?.StartsWith("SQLitePCLRaw.", StringComparison.Ordinal) == true;

    private static bool IsPrivateDatabaseAssembly(string? name) =>
        name == "Agw.Extensions.Sdk.EntityFrameworkCore"
        || name?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) == true
        || name?.StartsWith("Npgsql", StringComparison.Ordinal) == true;
}
