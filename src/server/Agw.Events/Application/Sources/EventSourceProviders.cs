using Agw.Extensions.Contracts;

namespace Agw.Events.Application.Sources;

internal static class EventSourceProviders
{
    public static IEnumerable<ExtensionGenerationSnapshot> GetActiveGenerations(
        this IExtensionGenerationCatalog catalog
    ) => catalog.GetSnapshots().Where(snapshot => snapshot.State == ExtensionGenerationState.Active);

    public static ExtensionEventSourceProviderDescriptor? FindSourceProvider(
        this ExtensionGenerationSnapshot? snapshot,
        string? providerId
    ) => snapshot?.EventSourceProviders.SingleOrDefault(provider => provider.Id == providerId);
}
