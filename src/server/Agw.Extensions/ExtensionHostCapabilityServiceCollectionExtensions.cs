using Agw.Extensions.Infrastructure.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Extensions;

public static class ExtensionHostCapabilityServiceCollectionExtensions
{
    public static IServiceCollection AddExtensionHostCapability<TCapability>(this IServiceCollection services)
        where TCapability : class
    {
        ArgumentNullException.ThrowIfNull(services);
        Type capabilityType = typeof(TCapability);
        ExtensionHostCapabilities.ValidateCapabilityType(capabilityType);
        services.AddSingleton(new ExtensionHostCapabilityRegistration(capabilityType));
        return services;
    }
}

internal sealed record ExtensionHostCapabilityRegistration(Type CapabilityType);
