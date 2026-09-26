using Agw.Agents.Application.Persistence;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Domain.Services;
using Agw.Agents.Definitions.Persistence;
using Agw.Integrations.Contracts.References;
using Agw.Providers.Contracts.References;
using Agw.Skills.Contracts.References;

namespace Agw.Agents.Tests;

internal static class TestAgentAppService
{
    public static AgentAppService Create(
        IAgentsDbContext dbContext,
        IConnectionReferenceFacade connectionReferences,
        IModelProviderReferenceFacade modelProviderReferences,
        ISkillReferenceFacade skillReferences,
        IUserInfoService userInfoService,
        IAgentDeletionCoordinator deletionCoordinator
    )
    {
        var definitions = new AgentDefinitionRepository(dbContext, userInfoService);
        return new AgentAppService(
            dbContext,
            new AgentDefinitionDomainService(definitions, modelProviderReferences),
            new AgentResourceBindingDomainService(definitions, skillReferences, connectionReferences),
            modelProviderReferences,
            skillReferences,
            userInfoService,
            deletionCoordinator
        );
    }
}
