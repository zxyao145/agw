using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk.Database;

namespace Agw.Extensions.Infrastructure.Runtime;

internal interface IExtensionGenerationAccessor
{
    IExtensionDatabaseGeneration? GetDatabaseGeneration(ExtensionGenerationId generationId);

    ExtensionDatabaseGenerationState? GetDatabaseGenerationState(ExtensionGenerationId generationId);

    bool IsInvocationActive(ExtensionGenerationId generationId, long epoch, Guid invocationId);
}

internal sealed record ExtensionDatabaseGenerationState(
    Guid ExtensionId,
    long Epoch,
    string ReleaseHash,
    ExtensionGenerationState State
);
