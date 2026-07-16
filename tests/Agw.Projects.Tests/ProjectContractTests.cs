using Agw.Projects.Application;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data.Entities.Projects;

namespace Agw.Projects.Tests;

public class ProjectContractTests
{
    [Fact]
    public void ProjectContracts_DoNotExposeEnable()
    {
        Assert.Null(typeof(Project).GetProperty("Enable"));
        Assert.Null(typeof(ProjectCreateRequest).GetProperty("Enable"));
        Assert.Null(typeof(ProjectUpdateRequest).GetProperty("Enable"));
        Assert.Null(typeof(ProjectResponse).GetProperty("Enable"));
    }

    [Fact]
    public void RequestContracts_LegacyConstructorValues_LeaveCapabilitiesUnset()
    {
        var create = new ProjectCreateRequest("Project A", "Description", "~/project-a", "{}");
        var update = new ProjectUpdateRequest("Project A", "Description", "~/project-a", "{}");

        Assert.Null(create.Tools);
        Assert.Null(create.McpToolServerIds);
        Assert.Null(create.SkillIds);
        Assert.Null(create.ConnectionIds);
        Assert.Null(create.EnvironmentVariables);
        Assert.Null(update.Tools);
        Assert.Null(update.McpToolServerIds);
        Assert.Null(update.SkillIds);
        Assert.Null(update.ConnectionIds);
        Assert.Null(update.EnvironmentVariables);
    }

    [Fact]
    public void ProjectAppService_ExposesCapabilityAwareCreateAndUpdateOverloads()
    {
        var createParameterTypes = new[]
        {
            typeof(Project),
            typeof(IEnumerable<Guid>),
            typeof(IEnumerable<Guid>),
            typeof(IEnumerable<Guid>),
            typeof(string)
        };
        var updateParameterTypes = new[]
        {
            typeof(Guid),
            typeof(Action<Project>),
            typeof(IEnumerable<Guid>),
            typeof(IEnumerable<Guid>),
            typeof(IEnumerable<Guid>),
            typeof(string)
        };

        Assert.NotNull(typeof(ProjectAppService).GetMethod("CreateAsync", createParameterTypes));
        Assert.NotNull(typeof(ProjectAppService).GetMethod("UpdateAsync", updateParameterTypes));
        Assert.NotNull(typeof(IProjectAppService).GetMethod("CreateAsync", createParameterTypes));
        Assert.NotNull(typeof(IProjectAppService).GetMethod("UpdateAsync", updateParameterTypes));
    }

    [Fact]
    public void ProjectResponse_FromDomain_MapsProjectAndRelationFields()
    {
        var projectId = Guid.NewGuid();
        var mcpToolServerId = Guid.NewGuid();
        var skillId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var project = new Project
        {
            Id = projectId,
            Name = "Project A",
            Type = ProjectType.UserDefined,
            Description = "Description",
            Workspace = "~/project-a",
            ExtraSetting = "{}",
            Tools = "[\"read_file\"]",
            EnvironmentVariables = new Dictionary<string, string> { ["API_KEY"] = "secret" },
            CreateBy = "tester",
            CreateTime = new DateTimeOffset(2026, 7, 13, 1, 0, 0, TimeSpan.Zero),
            UpdateBy = "updater",
            UpdateTime = new DateTimeOffset(2026, 7, 13, 2, 0, 0, TimeSpan.Zero),
            ProjectMcpToolServers =
            [
                new ProjectMcpServerRelation { ProjectId = projectId, McpToolServerId = mcpToolServerId }
            ],
            ProjectSkillRelations =
            [
                new ProjectSkillRelation { ProjectId = projectId, SkillId = skillId }
            ],
            ProjectConnectionRelations =
            [
                new ProjectConnectionRelation { ProjectId = projectId, ConnectionId = connectionId }
            ]
        };

        var response = ProjectResponse.FromDomain(project);

        Assert.Equal(project.Id, response.Id);
        Assert.Equal(project.Name, response.Name);
        Assert.Equal(project.Type, response.Type);
        Assert.Equal(project.Description, response.Description);
        Assert.Equal(project.Workspace, response.Workspace);
        Assert.Equal(project.ExtraSetting, response.ExtraSetting);
        Assert.Equal(project.Tools, response.Tools);
        Assert.Equal("secret", response.EnvironmentVariables["API_KEY"]);
        Assert.Equal(mcpToolServerId, Assert.Single(response.ProjectMcpToolServers).McpToolServerId);
        Assert.Equal(skillId, Assert.Single(response.ProjectSkillRelations).SkillId);
        Assert.Equal(connectionId, Assert.Single(response.ProjectConnectionRelations).ConnectionId);
        Assert.Equal(project.CreateTime, response.CreateTime);
        Assert.Equal(project.CreateBy, response.CreateBy);
        Assert.Equal(project.UpdateTime, response.UpdateTime);
        Assert.Equal(project.UpdateBy, response.UpdateBy);
    }
}
