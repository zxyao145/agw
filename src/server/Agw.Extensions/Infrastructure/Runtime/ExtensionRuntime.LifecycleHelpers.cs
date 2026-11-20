using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk;

namespace Agw.Extensions.Infrastructure.Runtime;

public sealed partial class ExtensionRuntime
{
    private void ReleaseGenerationLease(ExtensionGeneration generation)
    {
        generation.ReleaseLease();
        ActiveGenerationLeases.Add(-1, CreateMetricTags(generation));
    }

    private sealed class GenerationLease : IExtensionGenerationLease
    {
        private ExtensionRuntime? _owner;
        private ExtensionGeneration? _generation;

        public GenerationLease(
            ExtensionRuntime owner,
            ExtensionGeneration generation,
            ExtensionGenerationSnapshot snapshot,
            ExtensionExecutionPin pin
        )
        {
            _owner = owner;
            _generation = generation;
            Snapshot = snapshot;
            Pin = pin;
        }

        public ExtensionRuntime? Owner => Volatile.Read(ref _owner);

        public ExtensionGenerationSnapshot Snapshot { get; }

        public ExtensionExecutionPin Pin { get; }

        public bool TryGetGeneration(out ExtensionGeneration? generation)
        {
            generation = Volatile.Read(ref _generation);
            return generation is not null && Volatile.Read(ref _owner) is not null;
        }

        public ValueTask DisposeAsync()
        {
            ExtensionRuntime? owner = Interlocked.Exchange(ref _owner, null);
            ExtensionGeneration? generation = Interlocked.Exchange(ref _generation, null);
            if (owner is not null && generation is not null)
            {
                owner.ReleaseGenerationLease(generation);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ExtensionBackgroundScheduler : IExtensionBackgroundScheduler
    {
        private readonly ExtensionRuntime _runtime;
        private readonly ExtensionGenerationId _generationId;

        public ExtensionBackgroundScheduler(ExtensionRuntime runtime, ExtensionGenerationId generationId)
        {
            _runtime = runtime;
            _generationId = generationId;
        }

        public ValueTask ExecuteAsync(
            Func<IExtensionContext, CancellationToken, ValueTask> work,
            CancellationToken cancellationToken = default
        ) => _runtime.ExecuteBackgroundWorkAsync(_generationId, work, cancellationToken);

        public ValueTask<TResult> ExecuteAsync<TResult>(
            Func<IExtensionContext, CancellationToken, ValueTask<TResult>> work,
            CancellationToken cancellationToken = default
        ) => _runtime.ExecuteBackgroundWorkAsync(_generationId, work, cancellationToken);
    }
}
