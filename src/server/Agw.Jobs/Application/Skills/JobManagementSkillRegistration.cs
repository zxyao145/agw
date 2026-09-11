using Agw.Jobs.Application.Tools;
using Agw.Skills.Contracts.Registration;
using Agw.Tools.Abstractions.Generated;
using Microsoft.Agents.AI;

namespace Agw.Jobs.Application.Skills;

#pragma warning disable MAAI001

public sealed partial class JobManagementSkillRegistration
    : IAgentSkillRegistration,
        IAgwToolSet<JobManagementToolExecutor>
{
    public const string SkillName = "agw-job";
    public static readonly Guid SkillId = Guid.Parse("11111111-1111-1111-8888-000000000002");

    public Guid Id => SkillId;

    public string Name => SkillName;

    public string Description =>
        "Manage scheduled jobs in the current project, including listing, inspecting, creating, updating, and deleting jobs.";

    public AgentSkill Create(Guid projectId) => new JobManagementSkill();
}

#pragma warning restore MAAI001
