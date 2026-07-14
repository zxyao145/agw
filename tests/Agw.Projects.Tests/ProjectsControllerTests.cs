using System.Linq.Expressions;

using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data.Entities.Projects;
using Agw.Projects.Controllers;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Projects.Tests;

public class ProjectsControllerTests
{
    [Fact]
    public async Task ListAndGetAsync_ReturnProjectResponses()
    {
        var project = CreateProject();
        var service = new CapturingProjectAppService(project);
        var controller = new ProjectsController(service);

        var listResult = await controller.ListAsync();
        var getResult = await controller.GetAsync(project.Id);

        var listed = Assert.Single(Assert.IsAssignableFrom<IEnumerable<ProjectResponse>>(ReadApiResultData(listResult)));
        var fetched = Assert.IsType<ProjectResponse>(ReadApiResultData(getResult));
        Assert.Equal(project.Id, listed.Id);
        Assert.Equal(project.Id, fetched.Id);
    }

    [Fact]
    public async Task CreateAsync_ForwardsCapabilitiesAndReturnsProjectResponse()
    {
        var project = CreateProject();
        var service = new CapturingProjectAppService(project);
        var controller = new ProjectsController(service);
        var mcpToolServerId = Guid.NewGuid();
        var skillId = Guid.NewGuid();
        var appInstanceId = Guid.NewGuid();
        var request = new ProjectCreateRequest(
            "Project A",
            "Description",
            "~/project-a",
            true,
            "{}",
            "[\"read_file\"]",
            [mcpToolServerId],
            [skillId],
            [appInstanceId],
            new Dictionary<string, string> { ["API_KEY"] = "secret" });

        var result = await controller.CreateAsync(request);

        var response = Assert.IsType<ProjectResponse>(ReadApiResultData(result));
        Assert.Equal(project.Id, response.Id);
        Assert.Equal("[\"read_file\"]", service.CreatedProject!.Tools);
        Assert.Equal("secret", service.CreatedProject.EnvironmentVariables["API_KEY"]);
        Assert.Equal([mcpToolServerId], service.McpToolServerIds);
        Assert.Equal([skillId], service.SkillIds);
        Assert.Equal([appInstanceId], service.AppInstanceIds);
    }

    [Fact]
    public async Task UpdateAsync_ForwardsCapabilitiesAndReturnsProjectResponse()
    {
        var project = CreateProject();
        var service = new CapturingProjectAppService(project);
        var controller = new ProjectsController(service);
        var mcpToolServerId = Guid.NewGuid();
        var skillId = Guid.NewGuid();
        var appInstanceId = Guid.NewGuid();
        var request = new ProjectUpdateRequest(
            "Project A",
            "Updated",
            "~/project-a",
            true,
            "{}",
            "[\"write_file\"]",
            [mcpToolServerId],
            [skillId],
            [appInstanceId],
            new Dictionary<string, string> { ["MODE"] = "safe" });

        var result = await controller.UpdateAsync(project.Id, request);

        var response = Assert.IsType<ProjectResponse>(ReadApiResultData(result));
        Assert.Equal(project.Id, response.Id);
        Assert.Equal("[\"write_file\"]", project.Tools);
        Assert.Equal("safe", project.EnvironmentVariables["MODE"]);
        Assert.Equal([mcpToolServerId], service.McpToolServerIds);
        Assert.Equal([skillId], service.SkillIds);
        Assert.Equal([appInstanceId], service.AppInstanceIds);
    }

    [Fact]
    public async Task UpdateAsync_WhenCapabilitiesAreOmitted_PreservesExistingScalarCapabilities()
    {
        var project = CreateProject();
        project.Tools = "[\"read_file\"]";
        project.EnvironmentVariables = new Dictionary<string, string> { ["API_KEY"] = "secret" };
        var service = new CapturingProjectAppService(project);
        var controller = new ProjectsController(service);
        var request = new ProjectUpdateRequest("Project A", "Updated", "~/project-a", true, "{}");

        var result = await controller.UpdateAsync(project.Id, request);

        Assert.IsType<ProjectResponse>(ReadApiResultData(result));
        Assert.Equal("[\"read_file\"]", project.Tools);
        Assert.Equal("secret", project.EnvironmentVariables["API_KEY"]);
        Assert.Null(service.McpToolServerIds);
        Assert.Null(service.SkillIds);
        Assert.Null(service.AppInstanceIds);
    }

    [Fact]
    public async Task UpdateAsync_WhenCapabilitiesAreExplicitlyEmpty_ClearsScalarCapabilitiesAndForwardsEmptyRelations()
    {
        var project = CreateProject();
        project.Tools = "[\"read_file\"]";
        project.EnvironmentVariables = new Dictionary<string, string> { ["API_KEY"] = "secret" };
        var service = new CapturingProjectAppService(project);
        var controller = new ProjectsController(service);
        var request = new ProjectUpdateRequest(
            "Project A",
            "Updated",
            "~/project-a",
            true,
            "{}",
            "[]",
            [],
            [],
            [],
            new Dictionary<string, string>());

        var result = await controller.UpdateAsync(project.Id, request);

        Assert.IsType<ProjectResponse>(ReadApiResultData(result));
        Assert.Equal("[]", project.Tools);
        Assert.Empty(project.EnvironmentVariables);
        Assert.Empty(service.McpToolServerIds!);
        Assert.Empty(service.SkillIds!);
        Assert.Empty(service.AppInstanceIds!);
    }

    private static object ReadApiResultData(IActionResult result)
    {
        Assert.StartsWith("Bens.Results.ApiResult", result.GetType().FullName);
        var property = result.GetType().GetProperty("Data");
        Assert.NotNull(property);
        var data = property.GetValue(result);
        Assert.NotNull(data);
        return data;
    }

    private static Project CreateProject() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Project A",
        Type = ProjectType.UserDefined,
        Workspace = "~/project-a",
        Enable = true,
        EnvironmentVariables = new Dictionary<string, string>(),
        CreateTime = new DateTimeOffset(2026, 7, 13, 1, 0, 0, TimeSpan.Zero)
    };

    private sealed class CapturingProjectAppService : IProjectAppService
    {
        private readonly Project _project;

        public CapturingProjectAppService(Project project)
        {
            _project = project;
        }

        public Project? CreatedProject { get; private set; }
        public IReadOnlyList<Guid>? McpToolServerIds { get; private set; }
        public IReadOnlyList<Guid>? SkillIds { get; private set; }
        public IReadOnlyList<Guid>? AppInstanceIds { get; private set; }

        public Task<IReadOnlyList<Project>> ListAsync(Expression<Func<Project, bool>>? predicate = null) =>
            Task.FromResult<IReadOnlyList<Project>>([_project]);

        public Task<string?> GetProjectExtraSettingAsync(Guid? projectId) =>
            throw new NotSupportedException();

        public Task<Guid?> ResolveProjectIdAsync(Guid? projectId) =>
            throw new NotSupportedException();

        public Task<Project?> CreateAsync(Project project, string user)
        {
            CreatedProject = project;
            return Task.FromResult<Project?>(_project);
        }

        public Task<Project?> CreateAsync(
            Project project,
            IEnumerable<Guid>? mcpToolServerIds,
            IEnumerable<Guid>? skillIds,
            IEnumerable<Guid>? appInstanceIds,
            string user)
        {
            CreatedProject = project;
            CaptureRelationIds(mcpToolServerIds, skillIds, appInstanceIds);
            return Task.FromResult<Project?>(_project);
        }

        public Task<bool> DeleteAsync(Guid id) => throw new NotSupportedException();

        public Task<Project?> GetAsync(Guid id) => Task.FromResult<Project?>(_project);

        public Task<Project?> UpdateAsync(Guid id, Action<Project> updateAction, string user)
        {
            updateAction(_project);
            return Task.FromResult<Project?>(_project);
        }

        public Task<Project?> UpdateAsync(
            Guid id,
            Action<Project> updateAction,
            IEnumerable<Guid>? mcpToolServerIds,
            IEnumerable<Guid>? skillIds,
            IEnumerable<Guid>? appInstanceIds,
            string user)
        {
            updateAction(_project);
            CaptureRelationIds(mcpToolServerIds, skillIds, appInstanceIds);
            return Task.FromResult<Project?>(_project);
        }

        private void CaptureRelationIds(
            IEnumerable<Guid>? mcpToolServerIds,
            IEnumerable<Guid>? skillIds,
            IEnumerable<Guid>? appInstanceIds)
        {
            McpToolServerIds = mcpToolServerIds?.ToList();
            SkillIds = skillIds?.ToList();
            AppInstanceIds = appInstanceIds?.ToList();
        }
    }
}
