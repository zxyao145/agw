using Agw.Extensions.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Extensions.Runtime.Tests.Fixture;

public sealed class BlockingStopExtension : IServerExtension
{
    public void ConfigureServices(IServiceCollection services, IExtensionRegistration registration) { }

    public ValueTask StartAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public async ValueTask StopAsync(IExtensionContext context, CancellationToken cancellationToken)
    {
        string waitingPath = Path.Combine(context.DataDirectory, "stop-waiting.marker");
        string releasePath = Path.Combine(context.DataDirectory, "stop-release.marker");
        await File.WriteAllTextAsync(waitingPath, "waiting", cancellationToken);
        while (!File.Exists(releasePath))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }
}
