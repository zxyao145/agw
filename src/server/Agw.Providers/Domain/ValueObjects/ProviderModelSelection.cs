using Agw.Shared.Data.Entities.Providers;

namespace Agw.Providers.Domain.ValueObjects;

/// <summary>
/// <para>按模型名称选择供应商模型后需要持久化的变化。</para>
/// <para>The changes to persist after selecting a provider's models by name.</para>
/// </summary>
public sealed record ProviderModelSelection
{
    public required IReadOnlyList<ModelProviderRelation> RemovedRelations { get; init; }

    public required IReadOnlyList<AgwAiModel> CreatedModels { get; init; }

    public required IReadOnlyList<ModelProviderRelation> AddedRelations { get; init; }
}
