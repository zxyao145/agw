using Microsoft.Extensions.DependencyInjection;

namespace Agw.Extensions.Sdk;

public interface IServerExtension
{
    void ConfigureServices(IServiceCollection services, IExtensionRegistration registration);

    ValueTask StartAsync(IExtensionContext context, CancellationToken cancellationToken);

    ValueTask StopAsync(IExtensionContext context, CancellationToken cancellationToken);
}
