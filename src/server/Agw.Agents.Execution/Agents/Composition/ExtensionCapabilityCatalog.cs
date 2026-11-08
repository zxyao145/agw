using System.Text.Json;
using Agw.Extensions.Contracts;
using Agw.Shared.Exceptions;
using Agw.Tools.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Composition;

public interface IExtensionCapabilityCatalog
{
    ValueTask<IReadOnlyList<Guid>> ListEnabledSkillExtensionIdsAsync(CancellationToken cancellationToken);

    ValueTask<ExtensionCapabilitySnapshot> AcquireCurrentAsync(
        ExtensionCapabilitySelection selection,
        CancellationToken cancellationToken
    );

    ValueTask<ExtensionCapabilitySnapshot> AcquireCurrentAsync(
        IReadOnlyList<Guid> extensionIds,
        ExtensionCapabilitySelection selection,
        CancellationToken cancellationToken
    );

    ValueTask<ExtensionCapabilitySnapshot> AcquireForExecutionAsync(
        IReadOnlyList<Guid> extensionIds,
        ExtensionCapabilitySelection selection,
        CancellationToken cancellationToken
    );
}

internal sealed class ExtensionCapabilityCatalog : IExtensionCapabilityCatalog
{
    private readonly IExtensionGenerationCatalog _generations;
    private readonly IExtensionInvoker _invoker;
    private readonly IExtensionRuntimeAvailability _availability;

    public ExtensionCapabilityCatalog(
        IExtensionGenerationCatalog generations,
        IExtensionInvoker invoker,
        IExtensionRuntimeAvailability availability
    )
    {
        _generations = generations;
        _invoker = invoker;
        _availability = availability;
    }

    public async ValueTask<ExtensionCapabilitySnapshot> AcquireCurrentAsync(
        ExtensionCapabilitySelection selection,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(selection);
        var explicitToolExtensionIds = selection.Tools.Select(tool => tool.ExtensionId).ToHashSet();
        var optionalSkillExtensionIds = selection
            .SkillExtensionIds.Where(extensionId => !explicitToolExtensionIds.Contains(extensionId))
            .ToHashSet();
        return await AcquireAsync(
                selection.ExtensionIds,
                selection,
                cancellationToken,
                _generations.AcquireCurrentAsync,
                optionalSkillExtensionIds
            )
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<Guid>> ListEnabledSkillExtensionIdsAsync(CancellationToken cancellationToken)
    {
        var extensionIds = new HashSet<Guid>();
        foreach (
            var snapshot in _generations
                .GetSnapshots()
                .Where(snapshot => snapshot.State == ExtensionGenerationState.Active && snapshot.Skills.Count > 0)
                .OrderBy(snapshot => snapshot.GenerationId.ExtensionId)
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pin = new ExtensionExecutionPin(
                snapshot.GenerationId.ExtensionId,
                snapshot.ReleaseHash,
                snapshot.Epoch
            );
            if (await _availability.CanAcceptNewWorkAsync(pin, cancellationToken).ConfigureAwait(false))
            {
                extensionIds.Add(snapshot.GenerationId.ExtensionId);
            }
        }

        return extensionIds.Order().ToArray();
    }

    public ValueTask<ExtensionCapabilitySnapshot> AcquireCurrentAsync(
        IReadOnlyList<Guid> extensionIds,
        ExtensionCapabilitySelection selection,
        CancellationToken cancellationToken
    ) => AcquireAsync(extensionIds, selection, cancellationToken, _generations.AcquireCurrentAsync);

    public ValueTask<ExtensionCapabilitySnapshot> AcquireForExecutionAsync(
        IReadOnlyList<Guid> extensionIds,
        ExtensionCapabilitySelection selection,
        CancellationToken cancellationToken
    ) => AcquireAsync(extensionIds, selection, cancellationToken, _generations.AcquireForExecutionAsync);

    private async ValueTask<ExtensionCapabilitySnapshot> AcquireAsync(
        IReadOnlyList<Guid> extensionIds,
        ExtensionCapabilitySelection selection,
        CancellationToken cancellationToken,
        Func<Guid, CancellationToken, ValueTask<IExtensionGenerationLease>> acquireGeneration,
        IReadOnlySet<Guid>? optionalSkillExtensionIds = null
    )
    {
        ArgumentNullException.ThrowIfNull(extensionIds);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(acquireGeneration);
        ValidateSelection(selection);
        if (extensionIds.Any(id => id == Guid.Empty) || extensionIds.Distinct().Count() != extensionIds.Count)
        {
            throw new AgwException(ErrorCodes.DurableExecutionConflict, "The execution extension ids are invalid.");
        }

        var requestedExtensionIds = extensionIds.ToHashSet();
        if (!selection.ExtensionIds.ToHashSet().IsSubsetOf(requestedExtensionIds))
        {
            throw new AgwException(ErrorCodes.ExtensionVersionMismatch);
        }

        if (extensionIds.Count == 0)
        {
            return CreateSnapshot([], selection);
        }

        var leases = new List<IExtensionGenerationLease>(extensionIds.Count);
        var skippedSkillExtensionIds = new HashSet<Guid>();
        try
        {
            foreach (var extensionId in extensionIds.Order())
            {
                IExtensionGenerationLease lease;
                try
                {
                    lease = await acquireGeneration(extensionId, cancellationToken).ConfigureAwait(false);
                }
                catch (AgwException exception)
                    when (optionalSkillExtensionIds?.Contains(extensionId) == true
                        && exception.Code == ErrorCodes.ExtensionUnavailable.Code
                    )
                {
                    skippedSkillExtensionIds.Add(extensionId);
                    continue;
                }

                leases.Add(lease);
                EnsureLeaseMatchesPin(lease, lease.Pin);
            }

            var effectiveSelection =
                skippedSkillExtensionIds.Count == 0
                    ? selection
                    : new ExtensionCapabilitySelection(
                        selection.Tools,
                        selection.SkillExtensionIds.Where(id => !skippedSkillExtensionIds.Contains(id)).ToArray()
                    );
            return CreateSnapshot(leases, effectiveSelection);
        }
        catch
        {
            await DisposeLeasesAsync(leases).ConfigureAwait(false);
            throw;
        }
    }

    private ExtensionCapabilitySnapshot CreateSnapshot(
        IReadOnlyList<IExtensionGenerationLease> leases,
        ExtensionCapabilitySelection selection
    )
    {
        var tools = new List<AITool>();
        var selectedTools = new Dictionary<ExtensionToolSelection, AITool>();
        var skills = new List<AgentSkill>();
        var leaseSource = new ExtensionCapabilityLeaseSource(leases);
        foreach (var lease in leases)
        {
            EnsureLeaseMatchesPin(lease, lease.Pin);
            foreach (var toolSelection in selection.Tools.Where(tool => tool.ExtensionId == lease.Pin.ExtensionId))
            {
                var descriptor =
                    lease.Snapshot.Tools.SingleOrDefault(tool =>
                        string.Equals(tool.Id, toolSelection.ToolId, StringComparison.Ordinal)
                    )
                    ?? throw new AgwException(
                        ErrorCodes.ExtensionUnavailable,
                        "A selected extension Tool is unavailable."
                    );
                ValidateTool(descriptor);
                var function = new ExtensionToolFunction(leaseSource, lease.Pin.ExtensionId, descriptor, _invoker);
                var bound = AgwToolMetadataBinding.Bind(
                    function,
                    new AgwToolMetadata(
                        $"extension:{lease.Snapshot.Name}",
                        descriptor.RequiredPermission,
                        descriptor.AllowInPlanMode
                    )
                );
                tools.Add(bound);
                selectedTools.Add(toolSelection, bound);
            }

            foreach (var descriptor in lease.Snapshot.Skills)
            {
                ValidateSkill(descriptor);
                var extensionSkill = new ExtensionAgentSkill(leaseSource, lease.Pin.ExtensionId, descriptor, _invoker);
                skills.Add(extensionSkill);
            }
        }

        if (selectedTools.Count != selection.Tools.Distinct().Count())
        {
            throw new AgwException(ErrorCodes.ExtensionVersionMismatch);
        }

        return new ExtensionCapabilitySnapshot(
            leaseSource,
            leases.Select(lease => lease.Pin).ToArray(),
            tools,
            skills,
            selectedTools
        );
    }

    private static void ValidateSelection(ExtensionCapabilitySelection selection)
    {
        if (
            selection.Tools.Any(tool => tool.ExtensionId == Guid.Empty || string.IsNullOrWhiteSpace(tool.ToolId))
            || selection.SkillExtensionIds.Any(extensionId => extensionId == Guid.Empty)
            || selection.SkillExtensionIds.Distinct().Count() != selection.SkillExtensionIds.Count
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, "The extension capability selection is invalid.");
        }
    }

    private static void EnsureLeaseMatchesPin(IExtensionGenerationLease lease, ExtensionExecutionPin pin)
    {
        var snapshot = lease.Snapshot;
        if (
            lease.Pin != pin
            || snapshot.GenerationId.ExtensionId != pin.ExtensionId
            || !string.Equals(snapshot.ReleaseHash, pin.ReleaseHash, StringComparison.Ordinal)
            || snapshot.Epoch != pin.Epoch
        )
        {
            throw new AgwException(ErrorCodes.ExtensionVersionMismatch);
        }
    }

    private static void ValidateTool(ExtensionToolDescriptor descriptor)
    {
        if (
            string.IsNullOrWhiteSpace(descriptor.Id)
            || string.IsNullOrWhiteSpace(descriptor.Name)
            || string.IsNullOrWhiteSpace(descriptor.Description)
            || descriptor.InputSchema.ValueKind != JsonValueKind.Object
            || !Enum.IsDefined(descriptor.RequiredPermission)
        )
        {
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "An extension Tool descriptor is invalid.");
        }
    }

    private static void ValidateSkill(ExtensionSkillDescriptor descriptor)
    {
        if (
            string.IsNullOrWhiteSpace(descriptor.Id)
            || string.IsNullOrWhiteSpace(descriptor.Name)
            || string.IsNullOrWhiteSpace(descriptor.Description)
        )
        {
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "An extension Skill descriptor is invalid.");
        }
    }

    internal static async ValueTask DisposeLeasesAsync(IReadOnlyList<IExtensionGenerationLease> leases)
    {
        for (var index = leases.Count - 1; index >= 0; index--)
        {
            await leases[index].DisposeAsync().ConfigureAwait(false);
        }
    }
}

public sealed class ExtensionCapabilitySnapshot : IAsyncDisposable
{
    private readonly ExtensionCapabilityLeaseSource _leaseSource;
    private readonly IReadOnlyDictionary<ExtensionToolSelection, AITool> _selectedTools;

    internal ExtensionCapabilitySnapshot(
        ExtensionCapabilityLeaseSource leaseSource,
        IReadOnlyList<ExtensionExecutionPin> pins,
        IReadOnlyList<AITool> tools,
        IReadOnlyList<AgentSkill> skills,
        IReadOnlyDictionary<ExtensionToolSelection, AITool> selectedTools
    )
    {
        _leaseSource = leaseSource;
        Pins = pins.ToArray();
        Tools = tools.ToArray();
        Skills = skills.ToArray();
        _selectedTools = selectedTools;
    }

    public IReadOnlyList<ExtensionExecutionPin> Pins { get; }

    public IReadOnlyList<AITool> Tools { get; }

    public IReadOnlyList<AgentSkill> Skills { get; }

    internal IReadOnlyList<ExtensionToolSelection> SelectedToolSelections =>
        _selectedTools
            .Keys.OrderBy(tool => tool.ExtensionId)
            .ThenBy(tool => tool.ToolId, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<AITool> SelectTools(ExtensionCapabilitySelection selection) =>
        selection
            .Tools.Select(tool =>
                _selectedTools.TryGetValue(tool, out var selectedTool)
                    ? selectedTool
                    : throw new AgwException(ErrorCodes.ExtensionVersionMismatch)
            )
            .ToArray();

    internal ValueTask BindTurnAsync(Guid turnId) => _leaseSource.BindTurnAsync(turnId);

    internal IReadOnlyList<IExtensionGenerationLease> TakeLeasesForRebind() => _leaseSource.DetachLeases();

    internal ValueTask RebindTurnAsync(Guid turnId, ExtensionCapabilitySnapshot? next)
    {
        if (
            next != null
            && (!Pins.SequenceEqual(next.Pins) || !SelectedToolSelections.SequenceEqual(next.SelectedToolSelections))
        )
        {
            throw new AgwException(ErrorCodes.ExtensionVersionMismatch);
        }

        return _leaseSource.RebindTurnAsync(turnId, Pins, next);
    }

    internal ValueTask ReleaseTurnAsync(Guid turnId) => _leaseSource.ReleaseTurnAsync(turnId);

    public ValueTask DisposeAsync() => _leaseSource.DisposeAsync();
}

internal sealed class ExtensionCapabilityLeaseSource
{
    private readonly object _sync = new();
    private IReadOnlyList<IExtensionGenerationLease> _leases;
    private Guid? _activeTurnId;
    private bool _disposed;

    public ExtensionCapabilityLeaseSource(IReadOnlyList<IExtensionGenerationLease> leases)
    {
        _leases = leases.ToArray();
    }

    public IExtensionGenerationLease GetRequiredLease(Guid extensionId)
    {
        lock (_sync)
        {
            if (_disposed)
                throw new AgwException(ErrorCodes.ExtensionUnavailable);

            return _leases.SingleOrDefault(lease => lease.Pin.ExtensionId == extensionId)
                ?? throw new AgwException(ErrorCodes.ExtensionUnavailable);
        }
    }

    public ValueTask BindTurnAsync(Guid turnId)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeTurnId = turnId;
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask RebindTurnAsync(
        Guid turnId,
        IReadOnlyList<ExtensionExecutionPin> pins,
        ExtensionCapabilitySnapshot? next
    )
    {
        if (next != null && !pins.SequenceEqual(next.Pins))
            throw new AgwException(ErrorCodes.ExtensionVersionMismatch);

        var newLeases = next?.TakeLeasesForRebind() ?? [];
        IReadOnlyList<IExtensionGenerationLease> oldLeases;
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                oldLeases = _leases;
                _leases = newLeases;
                _activeTurnId = turnId;
            }
        }
        catch
        {
            await ExtensionCapabilityCatalog.DisposeLeasesAsync(newLeases).ConfigureAwait(false);
            throw;
        }

        await ExtensionCapabilityCatalog.DisposeLeasesAsync(oldLeases).ConfigureAwait(false);
    }

    public async ValueTask ReleaseTurnAsync(Guid turnId)
    {
        IReadOnlyList<IExtensionGenerationLease> leases;
        lock (_sync)
        {
            if (_disposed || _activeTurnId != turnId)
                return;

            leases = _leases;
            _leases = [];
            _activeTurnId = null;
        }

        await ExtensionCapabilityCatalog.DisposeLeasesAsync(leases).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        IReadOnlyList<IExtensionGenerationLease> leases;
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            _activeTurnId = null;
            leases = _leases;
            _leases = [];
        }

        await ExtensionCapabilityCatalog.DisposeLeasesAsync(leases).ConfigureAwait(false);
    }

    internal IReadOnlyList<IExtensionGenerationLease> DetachLeases()
    {
        lock (_sync)
        {
            if (_disposed)
                return [];

            var leases = _leases;
            _leases = [];
            _activeTurnId = null;
            return leases;
        }
    }
}

internal sealed class ExtensionToolFunction : AIFunction
{
    private readonly ExtensionCapabilityLeaseSource _leaseSource;
    private readonly Guid _extensionId;
    private readonly ExtensionToolDescriptor _descriptor;
    private readonly IExtensionInvoker _invoker;
    private readonly JsonElement _inputSchema;

    public ExtensionToolFunction(
        ExtensionCapabilityLeaseSource leaseSource,
        Guid extensionId,
        ExtensionToolDescriptor descriptor,
        IExtensionInvoker invoker
    )
    {
        _leaseSource = leaseSource;
        _extensionId = extensionId;
        _descriptor = descriptor;
        _invoker = invoker;
        _inputSchema = descriptor.InputSchema.Clone();
    }

    public override string Name => _descriptor.Name;

    public override string Description => _descriptor.Description;

    public override JsonElement JsonSchema => _inputSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken
    )
    {
        var generationLease = _leaseSource.GetRequiredLease(_extensionId);
        var argumentJson = JsonSerializer.SerializeToElement(
            arguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        );
        await using var response = await _invoker
            .InvokeToolAsync(
                generationLease,
                new ExtensionToolInvocation
                {
                    ReleaseHash = generationLease.Pin.ReleaseHash,
                    Epoch = generationLease.Pin.Epoch,
                    ToolId = _descriptor.Id,
                    Arguments = argumentJson,
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        if (response.StatusCode is < 200 or >= 300)
        {
            throw new AgwException(ErrorCodes.ExtensionUnavailable);
        }

        return response.JsonBody?.Clone();
    }
}

internal sealed class ExtensionAgentSkill : AgentSkill
{
    private readonly ExtensionCapabilityLeaseSource _leaseSource;
    private readonly Guid _extensionId;
    private readonly ExtensionSkillDescriptor _descriptor;
    private readonly IExtensionInvoker _invoker;

    public ExtensionAgentSkill(
        ExtensionCapabilityLeaseSource leaseSource,
        Guid extensionId,
        ExtensionSkillDescriptor descriptor,
        IExtensionInvoker invoker
    )
    {
        _leaseSource = leaseSource;
        _extensionId = extensionId;
        _descriptor = descriptor;
        _invoker = invoker;
        Frontmatter = new AgentSkillFrontmatter(descriptor.Name, descriptor.Description);
    }

    public override AgentSkillFrontmatter Frontmatter { get; }

    public override async ValueTask<string> GetContentAsync(CancellationToken cancellationToken = default)
    {
        var generationLease = _leaseSource.GetRequiredLease(_extensionId);
        await using var response = await _invoker
            .InvokeSkillContentAsync(
                generationLease,
                new ExtensionSkillInvocation
                {
                    ReleaseHash = generationLease.Pin.ReleaseHash,
                    Epoch = generationLease.Pin.Epoch,
                    SkillId = _descriptor.Id,
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        if (response.StatusCode is < 200 or >= 300)
        {
            throw new AgwException(ErrorCodes.ExtensionUnavailable);
        }

        var body = response.JsonBody ?? throw new AgwException(ErrorCodes.ExtensionUnavailable);
        if (body.ValueKind != JsonValueKind.String)
        {
            throw new AgwException(ErrorCodes.ExtensionUnavailable);
        }

        return body.GetString() ?? string.Empty;
    }
}
