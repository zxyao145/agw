namespace Agw.Extensions.Sdk;

public interface IExtensionHostCapabilities
{
    TCapability? Get<TCapability>()
        where TCapability : class;

    TCapability GetRequired<TCapability>()
        where TCapability : class;
}
