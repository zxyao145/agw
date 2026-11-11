using System.Runtime.Loader;
using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk;
using Agw.Shared.Exceptions;

namespace Agw.Extensions.Infrastructure.Runtime;

internal sealed class ExtensionHostCapabilities : IExtensionHostCapabilities
{
    private readonly IReadOnlySet<Type> _allowedCapabilities;
    private readonly AsyncLocal<IServiceProvider?> _services = new();

    public ExtensionHostCapabilities(IReadOnlySet<Type> allowedCapabilities)
    {
        _allowedCapabilities = allowedCapabilities;
    }

    public TCapability? Get<TCapability>()
        where TCapability : class
    {
        Type capabilityType = typeof(TCapability);
        ValidateCapabilityType(capabilityType, _allowedCapabilities);
        object? capability = _services.Value?.GetService(capabilityType);
        if (
            capability is not null
            && AssemblyLoadContext.GetLoadContext(capability.GetType().Assembly) != AssemblyLoadContext.Default
        )
            throw new AgwException(ErrorCodes.ExtensionUnavailable, "Extension host capability is unavailable.");
        return (TCapability?)capability;
    }

    public TCapability GetRequired<TCapability>()
        where TCapability : class =>
        Get<TCapability>()
        ?? throw new AgwException(
            ErrorCodes.ExtensionUnavailable,
            $"Host capability '{typeof(TCapability).Name}' is unavailable."
        );

    public IDisposable Push(IServiceProvider services)
    {
        IServiceProvider? previous = _services.Value;
        _services.Value = services;
        return new Restore(this, previous);
    }

    public void Clear() => _services.Value = null;

    internal static void ValidateCapabilityType(Type capabilityType)
    {
        if (!IsHostBusinessCapability(capabilityType))
            throw new AgwException(ErrorCodes.ExtensionUnavailable, "Extension host capability is unavailable.");
    }

    private static void ValidateCapabilityType(Type capabilityType, IReadOnlySet<Type> allowedCapabilities)
    {
        ValidateCapabilityType(capabilityType);
        if (!allowedCapabilities.Contains(capabilityType))
        {
            throw new AgwException(ErrorCodes.ExtensionUnavailable, "Extension host capability is unavailable.");
        }
    }

    private static bool IsHostBusinessCapability(Type capabilityType)
    {
        AssemblyLoadContext? loadContext = AssemblyLoadContext.GetLoadContext(capabilityType.Assembly);
        string? assemblyName = capabilityType.Assembly.GetName().Name;
        string? namespaceName = capabilityType.Namespace;
        return capabilityType.IsInterface
            && loadContext == AssemblyLoadContext.Default
            && (
                assemblyName is not ("Agw.Shared" or "Agw.Extensions.Contracts" or "Agw.Extensions.Sdk")
                || capabilityType == typeof(IExtensionCallerAccessor)
                || capabilityType == typeof(IExtensionServerAddress)
            )
            && namespaceName is not null
            && namespaceName.Contains(".Contracts", StringComparison.Ordinal);
    }

    private sealed class Restore : IDisposable
    {
        private ExtensionHostCapabilities? _owner;
        private readonly IServiceProvider? _previous;

        public Restore(ExtensionHostCapabilities owner, IServiceProvider? previous)
        {
            _owner = owner;
            _previous = previous;
        }

        public void Dispose()
        {
            ExtensionHostCapabilities? owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null)
            {
                owner._services.Value = _previous;
            }
        }
    }
}
