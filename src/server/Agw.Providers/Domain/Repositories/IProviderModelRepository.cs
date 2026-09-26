using Agw.Shared.Data.Entities.Providers;

namespace Agw.Providers.Domain.Repositories;

/// <summary>
/// <para>读取当前所有者的供应商与模型事实，供供应商与模型的绑定规则使用。</para>
/// <para>Reads the current owner's provider and model facts for the provider-model binding rules.</para>
/// </summary>
public interface IProviderModelRepository
{
    Task<bool> ProviderExistsAsync(Guid providerId);

    Task<bool> ModelExistsAsync(Guid modelId);

    Task<IReadOnlyList<AgwAiModel>> ListModelsByNameAsync(IReadOnlyCollection<string> modelNames);
}
