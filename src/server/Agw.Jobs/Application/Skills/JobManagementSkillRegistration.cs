using Agw.Skills.Contracts.Registration;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Jobs.Application.Skills;

#pragma warning disable MAAI001

public sealed class JobManagementSkillRegistration : IAgentSkillRegistration
{
    public const string SkillName = "agw-job";
    public static readonly Guid SkillId = Guid.Parse("11111111-1111-1111-8888-000000000002");

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ICurrentAgentTurn _turnContextAccessor;

    public JobManagementSkillRegistration(
        IServiceScopeFactory serviceScopeFactory,
        ICurrentAgentTurn turnContextAccessor
    )
    {
        _serviceScopeFactory = serviceScopeFactory;
        _turnContextAccessor = turnContextAccessor;
    }

    public Guid Id => SkillId;

    public string Name => SkillName;

    public string Description =>
        "Manage scheduled jobs in the current project, including listing, inspecting, creating, updating, and deleting jobs.";

    public AgentSkill Create(Guid projectId) =>
        new JobManagementSkill(projectId, _serviceScopeFactory, _turnContextAccessor);
}

#pragma warning restore MAAI001
