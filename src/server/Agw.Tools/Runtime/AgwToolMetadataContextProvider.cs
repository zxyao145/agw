using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Runtime;

/// <summary>Applies declared Agw metadata to tools produced dynamically by ToolBlock context providers.</summary>
public sealed class AgwToolMetadataContextProvider : AIContextProvider
{
    private readonly IReadOnlyDictionary<string, AgwToolMetadata> _metadata;
    private readonly IReadOnlyList<AIContextProvider>? _sourceProviders;
    private readonly bool _requireAllMembers = true;

    public AgwToolMetadataContextProvider(IReadOnlyDictionary<string, AgwToolMetadata> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        _metadata = new Dictionary<string, AgwToolMetadata>(metadata, StringComparer.OrdinalIgnoreCase);
    }

    internal AgwToolMetadataContextProvider(
        IReadOnlyList<AIContextProvider> sourceProviders,
        IReadOnlyDictionary<string, AgwToolMetadata> metadata,
        bool requireAllMembers
    )
    {
        ArgumentNullException.ThrowIfNull(sourceProviders);
        ArgumentNullException.ThrowIfNull(metadata);
        _sourceProviders = sourceProviders.ToArray();
        _metadata = new Dictionary<string, AgwToolMetadata>(metadata, StringComparer.OrdinalIgnoreCase);
        _requireAllMembers = requireAllMembers;
    }

    public override IReadOnlyList<string> StateKeys =>
        _sourceProviders?.SelectMany(static provider => provider.StateKeys).ToArray() ?? [];

    protected override async ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default
    )
    {
        var initialTools = _sourceProviders is not null
            ? context.AIContext.Tools?.ToHashSet(ReferenceEqualityComparer.Instance) ?? []
            : [];
        var aiContext = context.AIContext;
        if (_sourceProviders is not null)
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

        // 最后一个阶段一并校验此 Block 在前一阶段已绑定的成员。
        // The final phase also validates this block's members bound in the preceding phase.
        ValidateProducedTools(tools.Where(tool => !initialTools.Contains(tool) || IsBoundMember(tool)));
        aiContext.Tools = tools
            .Select(tool =>
                !initialTools.Contains(tool) && _metadata.TryGetValue(tool.Name, out var metadata)
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

    private bool IsBoundMember(AITool tool) =>
        _metadata.TryGetValue(tool.Name, out var metadata) && AgwToolMetadataBinding.GetMetadata(tool) == metadata;

    private void ValidateProducedTools(IEnumerable<AITool> producedTools)
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

        if (_requireAllMembers)
        {
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
}
