using Agw.Agents.Definitions.Domain.Repositories;
using Agw.Agents.ExternalAgents;
using Agw.Providers.Contracts.References;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Definitions.Domain.Services;

/// <summary>
/// <para>Agent 定义中需要其他聚合事实的规则：名称在所有者的 Agent 中唯一，引用的模型供应商必须可见，External Agent 的模型供应商必须受其类型支持。</para>
/// <para>Agent definition rules that need facts from other aggregates: names are unique among the owner's agents, referenced model providers must be visible, and an External agent's model provider must be supported by its kind.</para>
/// </summary>
public sealed class AgentDefinitionDomainService
{
    private readonly IAgentDefinitionRepository _definitions;
    private readonly IModelProviderReferenceFacade _modelProviderReferences;

    public AgentDefinitionDomainService(
        IAgentDefinitionRepository definitions,
        IModelProviderReferenceFacade modelProviderReferences
    )
    {
        _definitions = definitions;
        _modelProviderReferences = modelProviderReferences;
    }

    public async Task EnsureNameAvailableAsync(Agent agent)
    {
        if (await _definitions.AgentNameExistsAsync(agent.Name).ConfigureAwait(false))
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "An agent with this name already exists. Choose a different name."
            );
        }
    }

    public async Task<bool> AreModelProvidersVisibleAsync(Agent agent) =>
        await IsModelProviderVisibleAsync(agent.ModelProviderId).ConfigureAwait(false)
        && await IsModelProviderVisibleAsync(agent.SummaryModelProviderId).ConfigureAwait(false);

    public async Task<bool> IsModelProviderVisibleAsync(Guid? modelProviderId)
    {
        if (!modelProviderId.HasValue)
        {
            return true;
        }

        var visibleIds = await _modelProviderReferences
            .FilterVisibleModelProviderIdsAsync([modelProviderId.Value])
            .ConfigureAwait(false);
        return visibleIds.Contains(modelProviderId.Value);
    }

    /// <summary>
    /// <para>External Agent 的模型供应商必须存在，且其协议受该 External Agent 类型支持。</para>
    /// <para>An External agent's model provider must exist and use a protocol its kind supports.</para>
    /// </summary>
    public async Task EnsureExternalModelProviderSupportedAsync(EngineKind kind, Guid? modelProviderId)
    {
        if (!modelProviderId.HasValue)
        {
            return;
        }

        var snapshot =
            await _modelProviderReferences.GetRuntimeSnapshotAsync(modelProviderId.Value).ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound, "Model provider is unavailable.");
        ExternalAgentDefaults.ValidateProviderType(kind, snapshot.Provider.ProviderType);
    }
}
