using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Runtime;

/// <summary>Applies declared Agw metadata to tools produced dynamically by ToolBlock context providers.</summary>
public sealed class AgwToolMetadataContextProvider : AIContextProvider
{
    private readonly IReadOnlyDictionary<string, AgwToolMetadata> _metadata;
    private readonly IReadOnlyList<AIContextProvider>? _sourceProviders;

    public AgwToolMetadataContextProvider(IReadOnlyDictionary<string, AgwToolMetadata> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        _metadata = new Dictionary<string, AgwToolMetadata>(metadata, StringComparer.OrdinalIgnoreCase);
    }

    public AgwToolMetadataContextProvider(
        IReadOnlyList<AIContextProvider> sourceProviders,
        IReadOnlyDictionary<string, AgwToolMetadata> metadata
    )
    {
        ArgumentNullException.ThrowIfNull(sourceProviders);
        ArgumentNullException.ThrowIfNull(metadata);
        _sourceProviders = sourceProviders.ToArray();
        _metadata = new Dictionary<string, AgwToolMetadata>(metadata, StringComparer.OrdinalIgnoreCase);
    }

    public override IReadOnlyList<string> StateKeys =>
        _sourceProviders?.SelectMany(static provider => provider.StateKeys).ToArray() ?? [];

    protected override async ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default
    )
    {
        var initialTools = context.AIContext.Tools?.ToHashSet(ReferenceEqualityComparer.Instance) ?? [];
        var aiContext = context.AIContext;
        if (_sourceProviders != null)
        {
            foreach (var provider in _sourceProviders)
            {
                aiContext = await provider
                    .InvokingAsync(new InvokingContext(context.Agent, context.Session, aiContext), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (aiContext.Tools is not { } tools)
        {
            ValidateProducedTools([]);
            return aiContext;
        }

        var producedTools =
            _sourceProviders == null ? tools.ToArray() : tools.Where(tool => !initialTools.Contains(tool)).ToArray();
        ValidateProducedTools(producedTools);
        var producedToolSet = producedTools.ToHashSet(ReferenceEqualityComparer.Instance);
        aiContext.Tools = tools
            .Select(tool =>
                producedToolSet.Contains(tool) && _metadata.TryGetValue(tool.Name, out var metadata)
                    ? AgwToolMetadataBinding.Bind(tool, metadata)
                    : tool
            )
            .ToArray();
        return aiContext;
    }

    protected override async ValueTask InvokedCoreAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default
    )
    {
        if (_sourceProviders == null)
        {
            return;
        }

        foreach (var provider in _sourceProviders)
        {
            await provider.InvokedAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        var service = base.GetService(serviceType, serviceKey);
        if (service != null || _sourceProviders == null)
        {
            return service;
        }

        foreach (var provider in _sourceProviders)
        {
            service = provider.GetService(serviceType, serviceKey);
            if (service != null)
            {
                return service;
            }
        }

        return null;
    }

    private void ValidateProducedTools(IReadOnlyList<AITool> producedTools)
    {
        var counts = producedTools
            .GroupBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var duplicate = counts.FirstOrDefault(pair => pair.Value > 1);
        if (!string.IsNullOrEmpty(duplicate.Key))
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"ToolBlock member '{duplicate.Key}' was produced more than once."
            );
        }

        var unexpected = counts
            .Keys.Except(_metadata.Keys, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (unexpected.Length > 0)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"ToolBlock produced undeclared members: {string.Join(", ", unexpected)}."
            );
        }

        var missing = _metadata
            .Keys.Except(counts.Keys, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"ToolBlock did not produce its declared members: {string.Join(", ", missing)}."
            );
        }
    }
}
