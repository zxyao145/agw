using Agw.Integrations.Contracts.References;
using Agw.Providers.Contracts.References;
using Agw.Shared.Contracts;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Repositories;
using Agw.Skills.Contracts.References;

namespace Agw.Agents.Tests;

internal sealed class TestConnectionReferenceFacade : IConnectionReferenceFacade
{
    private readonly IRepository<Connection> _connections;
    private readonly ICurrentUser _currentUser;

    public TestConnectionReferenceFacade(IRepository<Connection> connections, ICurrentUser currentUser)
    {
        _connections = connections;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlySet<Guid>> FilterOwnedConnectionIdsAsync(
        IReadOnlyCollection<Guid> connectionIds,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ids = connectionIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new HashSet<Guid>();
        }

        var connections = await _connections.ListAsync(connection =>
            ids.Contains(connection.Id) && connection.CreateBy == _currentUser.RequiredUserId
        );
        return connections.Select(connection => connection.Id).ToHashSet();
    }
}

internal sealed class TestModelProviderReferenceFacade : IModelProviderReferenceFacade
{
    private readonly IRepository<ModelProviderRelation> _modelProviders;
    private readonly IRepository<AgwAiModel> _models;
    private readonly IRepository<Provider> _providers;
    private readonly ICurrentUser _currentUser;

    public TestModelProviderReferenceFacade(
        IRepository<ModelProviderRelation> modelProviders,
        IRepository<AgwAiModel> models,
        IRepository<Provider> providers,
        ICurrentUser currentUser
    )
    {
        _modelProviders = modelProviders;
        _models = models;
        _providers = providers;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlySet<Guid>> FilterVisibleModelProviderIdsAsync(
        IReadOnlyCollection<Guid> modelProviderIds,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ids = modelProviderIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new HashSet<Guid>();
        }

        var modelProviders = await _modelProviders.ListAsync(modelProvider =>
            ids.Contains(modelProvider.Id) && modelProvider.CreateBy == _currentUser.RequiredUserId
        );
        return modelProviders.Select(modelProvider => modelProvider.Id).ToHashSet();
    }

    public async Task<ModelProviderRuntimeSnapshot?> GetRuntimeSnapshotAsync(
        Guid modelProviderId,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var modelProvider = await _modelProviders.SingleOrDefaultAsync(
            relation => relation.Id == modelProviderId && relation.CreateBy == _currentUser.RequiredUserId,
            cancellationToken
        );
        if (modelProvider == null)
        {
            return null;
        }

        var model = await _models.SingleOrDefaultAsync(
            item => item.Id == modelProvider.ModelId && item.CreateBy == _currentUser.RequiredUserId,
            cancellationToken
        );
        var providers = await _providers.ListAsync(
            provider => provider.Id == modelProvider.ProviderId && provider.CreateBy == _currentUser.RequiredUserId,
            null,
            provider => provider.AuthConfigs
        );
        var provider = providers.SingleOrDefault();
        if (model == null || provider == null)
        {
            return null;
        }

        return new ModelProviderRuntimeSnapshot(
            modelProvider.Id,
            new ModelProviderModelSnapshot(model.Id, model.Name, model.MaxContextWindowTokens, model.MaxOutputTokens),
            new ModelProviderProviderSnapshot(
                provider.Id,
                provider.Name,
                provider.ProviderType,
                provider.Endpoint,
                provider
                    .AuthConfigs.Select(authConfig => new ProviderAuthConfigSnapshot(
                        authConfig.Enable,
                        authConfig.ApiKey
                    ))
                    .ToArray()
            )
        );
    }
}

internal sealed class TestSkillReferenceFacade : ISkillReferenceFacade
{
    private readonly IRepository<Skill> _skills;
    private readonly ICurrentUser _currentUser;

    public TestSkillReferenceFacade(IRepository<Skill> skills, ICurrentUser currentUser)
    {
        _skills = skills;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlySet<Guid>> FilterVisibleSkillIdsAsync(
        IReadOnlyCollection<Guid> skillIds,
        CancellationToken cancellationToken = default
    )
    {
        var skills = await ListVisibleAsync(skillIds, cancellationToken);
        return skills.Select(skill => skill.Id).ToHashSet();
    }

    public async Task<IReadOnlyList<SkillReferenceSnapshot>> ResolveVisibleSkillsAsync(
        IReadOnlyCollection<Guid> skillIds,
        CancellationToken cancellationToken = default
    )
    {
        var skills = await ListVisibleAsync(skillIds, cancellationToken);
        return skills
            .Select(skill => new SkillReferenceSnapshot(
                skill.Id,
                skill.Name,
                skill.Description,
                skill.Kind,
                skill.ContentPath
            ))
            .ToArray();
    }

    public async Task<IReadOnlyList<SkillDescriptor>> DescribeVisibleSkillsAsync(
        IReadOnlyCollection<Guid> skillIds,
        CancellationToken cancellationToken = default
    )
    {
        var skills = await ListVisibleAsync(skillIds, cancellationToken);
        return skills.Select(skill => new SkillDescriptor(skill.Id, skill.Name, skill.Description)).ToArray();
    }

    private async Task<IReadOnlyList<Skill>> ListVisibleAsync(
        IReadOnlyCollection<Guid> skillIds,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ids = skillIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        return await _skills.ListAsync(skill =>
            ids.Contains(skill.Id) && (skill.Kind == SkillKind.BuiltIn || skill.CreateBy == _currentUser.RequiredUserId)
        );
    }
}
