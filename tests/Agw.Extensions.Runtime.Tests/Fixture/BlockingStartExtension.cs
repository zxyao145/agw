using System.Text.Json;
using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Extensions.Runtime.Tests.Fixture;

public sealed class BlockingStartExtension : IServerExtension
{
    public void ConfigureServices(IServiceCollection services, IExtensionRegistration registration)
    {
        registration.RegisterEndpoint(
            new ExtensionEndpointDescriptor
            {
                Id = "lifecycle",
                Method = "GET",
                RelativePath = "lifecycle",
            },
            static (_, _, _, _) =>
                ValueTask.FromResult(
                    ExtensionEndpointResponse.FromJson(JsonSerializer.SerializeToElement(new { active = true }))
                )
        );
    }

    public async ValueTask StartAsync(IExtensionContext context, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(
            Path.Combine(context.DataDirectory, "start-waiting.marker"),
            "started",
            cancellationToken
        );
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    public ValueTask StopAsync(IExtensionContext context, CancellationToken cancellationToken)
    {
        File.WriteAllText(Path.Combine(context.DataDirectory, "stopped.marker"), "stopped");
        return ValueTask.CompletedTask;
    }
}
