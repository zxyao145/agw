using Agw.Agents.Application.A2A;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Domain.Services;
using Agw.Agents.Definitions.Persistence;
using Agw.Integrations.Contracts.References;
using Agw.Providers.Contracts.References;
using Agw.Shared.Coordination;
using Agw.Skills.Contracts.References;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Tests;

internal static class TestAgentAppService
{
    private static readonly ServiceProvider CardServices = A2A.A2AAgentCardClientTests.CreateServices();
    internal static IA2AAgentCardClient CardClient => CardServices.GetRequiredService<IA2AAgentCardClient>();

    public static AgentAppService Create(
        IAgentsDbContext dbContext,
        IConnectionReferenceFacade connectionReferences,
        IModelProviderReferenceFacade modelProviderReferences,
        ISkillReferenceFacade skillReferences,
        IUserInfoService userInfoService,
        IAgentDeletionCoordinator deletionCoordinator,
        IA2AAgentCardClient? cardClient = null,
        TimeProvider? timeProvider = null
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
            deletionCoordinator,
            InMemoryApplicationLock.Shared,
            cardClient ?? CardClient,
            timeProvider ?? TimeProvider.System
        );
    }
}
