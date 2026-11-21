using Agw.Extensions.Contracts;

namespace Agw.Extensions.Infrastructure.Runtime;

internal static class EventSourceProviderDescriptorCloner
{
    public static ExtensionEventSourceProviderDescriptor Clone(ExtensionEventSourceProviderDescriptor descriptor) =>
        descriptor with
        {
            EventTypes = descriptor.EventTypes.ToArray(),
            Configurations = descriptor
                .Configurations.Select(configuration => configuration with { Config = configuration.Config.Clone() })
                .ToArray(),
        };
}
