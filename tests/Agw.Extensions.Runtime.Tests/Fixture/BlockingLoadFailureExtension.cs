using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Extensions.Runtime.Tests.Fixture;

public sealed class BlockingLoadFailureExtension : IServerExtension
{
    private const string WaitingMarker = "extension-load-failure-waiting.marker";
    private const string ReleaseMarker = "extension-load-failure-release.marker";

    public void ConfigureServices(IServiceCollection services, IExtensionRegistration registration)
    {
        string waitingPath = Path.Combine(AppContext.BaseDirectory, WaitingMarker);
        string releasePath = Path.Combine(AppContext.BaseDirectory, ReleaseMarker);
        File.WriteAllText(waitingPath, "waiting");
        while (!File.Exists(releasePath))
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(5));
        }

        registration.RegisterSkill(
            new ExtensionSkillDescriptor("invalid-skill", "", "Invalid skill metadata."),
            static (_, _, _) => ValueTask.FromResult("content")
        );
    }

    public ValueTask StartAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
