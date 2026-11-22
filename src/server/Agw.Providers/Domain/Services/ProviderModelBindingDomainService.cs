using Agw.Agents.Contracts.Catalog;
using Agw.Providers.Domain.Behaviors;
using Agw.Providers.Domain.Repositories;
using Agw.Providers.Domain.ValueObjects;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Exceptions;

namespace Agw.Providers.Domain.Services;

/// <summary>
/// <para>供应商与模型的绑定规则：只能绑定所有者自己的供应商与模型，仍被 Agent 或 Agentflow 使用的绑定不能解除。</para>
/// <para>Provider-model binding rules: only the owner's providers and models can be bound, and a binding still used by an Agent or Agentflow cannot be removed.</para>
/// </summary>
public sealed class ProviderModelBindingDomainService
{
    private readonly IProviderModelRepository _providerModels;
    private readonly IAgentReferenceFacade _agentReferences;

    public ProviderModelBindingDomainService(
        IProviderModelRepository providerModels,
        IAgentReferenceFacade agentReferences
    )
    {
        _providerModels = providerModels;
        _agentReferences = agentReferences;
    }

    public async Task EnsureBindableAsync(Guid providerId, Guid modelId, CancellationToken cancellationToken = default)
    {
        var providerExists = await _providerModels
            .ProviderExistsAsync(providerId, cancellationToken)
            .ConfigureAwait(false);
        var modelExists = await _providerModels.ModelExistsAsync(modelId, cancellationToken).ConfigureAwait(false);
        if (!providerExists || !modelExists)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }
    }

    public async Task EnsureUnbindableAsync(
        IEnumerable<Guid> modelProviderIds,
        CancellationToken cancellationToken = default
    )
    {
        var ids = modelProviderIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        if (await _agentReferences.UsesAnyModelProviderAsync(ids, cancellationToken).ConfigureAwait(false))
        {
            throw new AgwException(ErrorCodes.ModelProviderInUse);
        }
    }

    /// <summary>
    /// <para>按名称更新模型绑定，创建新模型时使用已知的上下文窗口。</para>
    /// <para>Updates model bindings by name and uses reported context windows when creating new models.</para>
    /// </summary>
    public async Task<ProviderModelSelection> SelectModelsAsync(
        Guid providerId,
        IReadOnlyCollection<ModelProviderRelation> currentRelations,
        IReadOnlyList<string> modelNames,
        IReadOnlyDictionary<string, int>? initialContextWindows,
        CancellationToken cancellationToken = default
    )
    {
        var selectedNames = NormalizeModelNames(modelNames);
        var selectedNameSet = selectedNames.ToHashSet(StringComparer.Ordinal);
        var removedRelations = currentRelations
            .Where(relation => relation.Model == null || !selectedNameSet.Contains(relation.Model.Name))
            .ToList();
        await EnsureUnbindableAsync(removedRelations.Select(relation => relation.Id), cancellationToken)
            .ConfigureAwait(false);

        var existingModels =
            selectedNames.Count == 0
                ? []
                : await _providerModels.ListModelsByNameAsync(selectedNames, cancellationToken).ConfigureAwait(false);
        var modelByName = existingModels.ToDictionary(model => model.Name, StringComparer.Ordinal);
        var createdModels = selectedNames
            .Where(modelName => !modelByName.ContainsKey(modelName))
            .Select(name => CreateModel(name, initialContextWindows))
            .ToList();
        foreach (var model in createdModels)
        {
            modelByName.Add(model.Name, model);
        }

        return new ProviderModelSelection
        {
            RemovedRelations = removedRelations,
            CreatedModels = createdModels,
            AddedRelations = CreateMissingRelations(
                providerId,
                currentRelations.Except(removedRelations),
                selectedNames.Select(modelName => modelByName[modelName])
            ),
        };
    }

    private static List<string> NormalizeModelNames(IReadOnlyList<string> modelNames) =>
        modelNames
            .Select(modelName => modelName?.Trim())
            .Where(modelName => !string.IsNullOrEmpty(modelName))
            .Select(modelName => modelName!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static AgwAiModel CreateModel(string modelName, IReadOnlyDictionary<string, int>? initialContextWindows)
    {
        var contextWindow = AgwAiModel.DefaultMaxContextWindowTokens;
        var outputLimit = AgwAiModel.DefaultMaxOutputTokens;
        if (initialContextWindows != null && initialContextWindows.TryGetValue(modelName, out var allocatedWindow))
        {
            // 新模型将已分配窗口的四分之一用于输出，最多使用通用输出上限。
            // Reserve a quarter of the allocated window for output, up to the standard output limit.
            contextWindow = allocatedWindow;
            outputLimit = Math.Clamp(allocatedWindow / 4, 1, outputLimit);
        }

        var model = new AgwAiModel { Id = Guid.CreateVersion7() };
        new AgwAiModelBehavior(model).Define(modelName, null, contextWindow, outputLimit);
        return model;
    }

    private static List<ModelProviderRelation> CreateMissingRelations(
        Guid providerId,
        IEnumerable<ModelProviderRelation> keptRelations,
        IEnumerable<AgwAiModel> selectedModels
    )
    {
        var boundModelIds = keptRelations.Select(relation => relation.ModelId).ToHashSet();
        var addedRelations = new List<ModelProviderRelation>();
        foreach (var model in selectedModels)
        {
            if (!boundModelIds.Add(model.Id))
            {
                continue;
            }

            addedRelations.Add(
                new ModelProviderRelation
                {
                    Id = Guid.CreateVersion7(),
                    ProviderId = providerId,
                    ModelId = model.Id,
                }
            );
        }

        return addedRelations;
    }
}
