using System.Security.Claims;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Contracts;
using Agw.Agents.Definitions.Domain.Behaviors;
using Agw.Agents.Definitions.Domain.Rules;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Agents.ExternalAgents;
using Agw.Auth.Application;
using Agw.Infrastructure.Agents;
using Agw.Integrations.Application.Facades;
using Agw.Providers.Application.Facades;
using Agw.Providers.Contracts;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Exceptions;
using Agw.Skills.Application.Facades;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests.Acp;

public sealed class AcpAgentDefinitionTests : IDisposable
{
    private const string UserId = "acp-owner";
    private const string CommandExtra = """{"command":"opencode","args":["acp"]}""";
    private readonly TestAgentDatabase _database = new();
    private readonly IDisposable _user = UserInfoUtil.Push(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId)], "test"))
    );
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreateAgent_AcpCommand_StoresExtra()
    {
        var agent = Assert.IsType<Agent>(await CreateService().CreateAgentAsync(NewAgent(), new(), Ct));
        _database.Context.ChangeTracker.Clear();

        Assert.Equal(CommandExtra, (await _database.Context.Agents.SingleAsync(x => x.Id == agent.Id, Ct)).Extra);
    }

    [Fact]
    public async Task CreateAgent_AcpEmptyExtra_ThrowsInvalidParam()
    {
        var error = await Assert.ThrowsAsync<AgwException>(() =>
            CreateService().CreateAgentAsync(NewAgent(null), new(), Ct)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
        Assert.Equal("ACP agents require either registry or command in Extra Settings.", error.Message);
    }

    [Fact]
    public void PrepareForCreate_AcpWithNoLaunchSource_ThrowsInvalidParam()
    {
        var behavior = new AgentBehavior(NewAgent(null));

        var error = Assert.Throws<AgwException>(() => behavior.PrepareForCreate());

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
        Assert.Equal("ACP agents require either registry or command in Extra Settings.", error.Message);
    }

    [Fact]
    public void EnsureUpdateAllowed_AcpWithEmptyExtra_ThrowsInvalidParam()
    {
        var behavior = new AgentBehavior(NewAgent());
        var update = new AgentUpdate
        {
            Extra = "{}",
            SpecifiedFields = new HashSet<AgentUpdateField> { AgentUpdateField.Extra },
        };

        var error = Assert.Throws<AgwException>(() => behavior.EnsureUpdateAllowed(update));

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
        Assert.Equal("ACP agents require either registry or command in Extra Settings.", error.Message);
    }

    [Fact]
    public void PrepareForCreate_AcpWithResponseSchema_ThrowsInvalidParam()
    {
        var agent = NewAgent();
        agent.ResponseSchema = """{"type":"object"}""";

        Assert.Equal(
            ErrorCodes.InvalidParam.Code,
            Assert.Throws<AgwException>(() => new AgentBehavior(agent).PrepareForCreate()).Code
        );
    }

    [Fact]
    public async Task CreateAgent_AcpWithModelProvider_ThrowsInvalidParam()
    {
        var model = new AgwAiModel
        {
            Id = Guid.CreateVersion7(),
            Name = "test-model",
            CreateBy = UserId,
        };
        var provider = new Provider
        {
            Id = Guid.CreateVersion7(),
            ProviderType = ProviderType.Anthropic,
            CreateBy = UserId,
        };
        var relation = new ModelProviderRelation
        {
            Id = Guid.CreateVersion7(),
            Model = model,
            Provider = provider,
            CreateBy = UserId,
        };
        _database.Context.ModelProviders.Add(relation);
        await _database.Context.SaveChangesAsync(Ct);
        var agent = NewAgent();
        agent.ModelProviderId = relation.Id;

        var error = await Assert.ThrowsAsync<AgwException>(() => CreateService().CreateAgentAsync(agent, new(), Ct));

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
        Assert.StartsWith("ACP does not support", error.Message);
    }

    [Fact]
    public async Task UpdateAgent_AcpInvalidExtra_ThrowsInvalidParam()
    {
        var service = CreateService();
        var agent = Assert.IsType<Agent>(await service.CreateAgentAsync(NewAgent(), new(), Ct));
        var request = new AgentUpdateRequest { Extra = "{}" };

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            service.UpdateAgentAsync(agent.Id, request.ToCommand(), Ct)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
        Assert.Equal(CommandExtra, agent.Extra);
    }

    [Fact]
    public void GetExternalOptions_IncludesAcp()
    {
        Assert.Contains(EngineKind.ACP, ExternalAgentRules.SupportedKinds);
        Assert.Equal("ACP", ExternalAgentDefaults.GetDisplayName(EngineKind.ACP));
        Assert.Equal("{}", ExternalAgentDefaults.GetDefaultExtra(EngineKind.ACP));
        Assert.Empty(ExternalAgentDefaults.GetSupportedProviderTypes(EngineKind.ACP));
    }

    public void Dispose()
    {
        _user.Dispose();
        _database.Dispose();
    }

    private AgentAppService CreateService()
    {
        var context = _database.Context;
        var user = new UserInfoService();
        return TestAgentAppService.Create(
            context,
            new ConnectionReferenceFacade(context, user),
            new ModelProviderReferenceFacade(context, user),
            new SkillReferenceFacade(context, user),
            user,
            new AgentDeletionCoordinator(
                context,
                InMemoryApplicationLock.Shared,
                TurnPersistenceTestKit.CreateRevisionUsageReader(context)
            )
        );
    }

    private static Agent NewAgent(string? extra = CommandExtra) =>
        new()
        {
            Name = "acp-test",
            DisplayName = "ACP test",
            Type = AgentType.External,
            ExternalAgentKind = EngineKind.ACP,
            Extra = extra,
            CreateBy = UserId,
        };
}
