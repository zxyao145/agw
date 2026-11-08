using Agw.Extensions.Application.Persistence;
using Agw.Extensions.Contracts;
using Agw.Extensions.Infrastructure.Runtime;
using Agw.Extensions.Sdk.Database;

namespace Agw.Extensions.Infrastructure.Database;

internal sealed class ExtensionEpochValidator : IExtensionEpochValidator, IExtensionSchemaEpochValidator
{
    private readonly IExtensionGenerationAccessor _generationAccessor;
    private readonly IExtensionEnablementStore _enablement;

    public ExtensionEpochValidator(
        IExtensionGenerationAccessor generationAccessor,
        IExtensionEnablementStore enablement
    )
    {
        _generationAccessor = generationAccessor;
        _enablement = enablement;
    }

    public ValueTask<bool> IsCurrentAsync(
        Guid extensionId,
        Guid generationId,
        long expectedEpoch,
        Guid? invocationId,
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (invocationId is not Guid activeInvocationId)
        {
            return ValueTask.FromResult(false);
        }

        return ValueTask.FromResult(
            _generationAccessor.IsInvocationActive(
                new ExtensionGenerationId(extensionId, generationId),
                expectedEpoch,
                activeInvocationId
            )
        );
    }

    public async ValueTask<bool> IsCurrentAsync(
        Guid extensionId,
        Guid generationId,
        long expectedEpoch,
        string releaseHash,
        bool hasPendingMigrations,
        System.Data.Common.DbConnection openConnection,
        CancellationToken cancellationToken = default
    )
    {
        ExtensionDatabaseGenerationState? generation = FindGeneration(extensionId, generationId);
        if (
            generation is null
            || generation.Epoch != expectedEpoch
            || generation.ReleaseHash != releaseHash
            || generation.State
                is not (
                    ExtensionGenerationState.Prepared
                    or ExtensionGenerationState.Ready
                    or ExtensionGenerationState.Active
                )
        )
        {
            return false;
        }

        ExtensionEnablementState enabled = await _enablement.GetAsync(extensionId, cancellationToken);
        return enabled.Version == expectedEpoch;
    }

    private ExtensionDatabaseGenerationState? FindGeneration(Guid extensionId, Guid generationId) =>
        _generationAccessor.GetDatabaseGenerationState(new ExtensionGenerationId(extensionId, generationId));
}
