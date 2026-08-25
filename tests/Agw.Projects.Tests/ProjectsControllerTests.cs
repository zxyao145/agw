using System.Linq.Expressions;
using System.Security.Claims;
using Agw.Projects.Controllers;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Tooling;
using Microsoft.AspNetCore.Http;
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

        var listed = Assert.Single(
            Assert.IsAssignableFrom<IEnumerable<ProjectResponse>>(ReadApiResultData(listResult))
        );
        var fetched = Assert.IsType<ProjectResponse>(ReadApiResultData(getResult));
        Assert.Equal(project.Id, listed.Id);
        Assert.Equal(project.Id, fetched.Id);
    }

    [Fact]
    public async Task CreateAsync_ForwardsCapabilitiesAndReturnsProjectResponse()
    {
        var project = CreateProject();
        var service = new CapturingProjectAppService(project);
        var controller = new ProjectsController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity(
                            [new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.NameIdentifier, "user-42")],
                            "test"
                        )
                    ),
                },
            },
        };
        var mcpToolServerId = Guid.CreateVersion7();
        var skillId = Guid.CreateVersion7();
        var appInstanceId = Guid.CreateVersion7();
        var request = new ProjectCreateRequest(
            "Project A",
            "Description",
            "~/project-a",
            "{}",
            [new ToolValue { Definition = new WebSearchToolDefinition() }],
            [mcpToolServerId],
            [skillId],
            [appInstanceId],
            new Dictionary<string, string> { ["API_KEY"] = "secret" }
        );

        var result = await controller.CreateAsync(request);

        var response = Assert.IsType<ProjectResponse>(ReadApiResultData(result));
        Assert.Equal(project.Id, response.Id);
        Assert.IsType<WebSearchToolDefinition>(
            Assert.IsType<ToolValue>(Assert.Single(service.CreatedProject!.Tools)).Definition
        );
        Assert.Equal("secret", service.CreatedProject.EnvironmentVariables["API_KEY"]);
        Assert.Equal([mcpToolServerId], service.McpToolServerIds);
        Assert.Equal([skillId], service.SkillIds);
        Assert.Equal([appInstanceId], service.ConnectionIds);
    }

    [Fact]
    public async Task UpdateAsync_ForwardsCapabilitiesAndReturnsProjectResponse()
    {
        var project = CreateProject();
        var service = new CapturingProjectAppService(project);
        var controller = new ProjectsController(service);
        var mcpToolServerId = Guid.CreateVersion7();
        var skillId = Guid.CreateVersion7();
        var appInstanceId = Guid.CreateVersion7();
        var request = new ProjectUpdateRequest(
            "Project A",
            "Updated",
            "~/project-a",
            "{}",
            [new ToolValue { Definition = new WebFetchToolDefinition() }],
            [mcpToolServerId],
            [skillId],
            [appInstanceId],
            new Dictionary<string, string> { ["MODE"] = "safe" }
        );

        var result = await controller.UpdateAsync(project.Id, request);

        var response = Assert.IsType<ProjectResponse>(ReadApiResultData(result));
        Assert.Equal(project.Id, response.Id);
        Assert.IsType<WebFetchToolDefinition>(Assert.IsType<ToolValue>(Assert.Single(project.Tools)).Definition);
        Assert.Equal("safe", project.EnvironmentVariables["MODE"]);
        Assert.Equal([mcpToolServerId], service.McpToolServerIds);
        Assert.Equal([skillId], service.SkillIds);
        Assert.Equal([appInstanceId], service.ConnectionIds);
    }

    [Fact]
    public async Task UpdateAsync_WhenCapabilitiesAreOmitted_PreservesExistingScalarCapabilities()
    {
        var project = CreateProject();
        project.Tools = [new ToolValue { Definition = new WebSearchToolDefinition() }];
        project.EnvironmentVariables = new Dictionary<string, string> { ["API_KEY"] = "secret" };
        var service = new CapturingProjectAppService(project);
        var controller = new ProjectsController(service);
        var request = new ProjectUpdateRequest("Project A", "Updated", "~/project-a", "{}");

        var result = await controller.UpdateAsync(project.Id, request);

        Assert.IsType<ProjectResponse>(ReadApiResultData(result));
        Assert.IsType<WebSearchToolDefinition>(Assert.IsType<ToolValue>(Assert.Single(project.Tools)).Definition);
        Assert.Equal("secret", project.EnvironmentVariables["API_KEY"]);
        Assert.Null(service.McpToolServerIds);
        Assert.Null(service.SkillIds);
        Assert.Null(service.ConnectionIds);
    }

    [Fact]
    public async Task UpdateAsync_WhenCapabilitiesAreExplicitlyEmpty_ClearsScalarCapabilitiesAndForwardsEmptyRelations()
    {
        var project = CreateProject();
        project.Tools = [new ToolValue { Definition = new WebSearchToolDefinition() }];
        project.EnvironmentVariables = new Dictionary<string, string> { ["API_KEY"] = "secret" };
        var service = new CapturingProjectAppService(project);
        var controller = new ProjectsController(service);
        var request = new ProjectUpdateRequest(
            "Project A",
            "Updated",
            "~/project-a",
            "{}",
            [],
            [],
            [],
            [],
            new Dictionary<string, string>()
        );

        var result = await controller.UpdateAsync(project.Id, request);

        Assert.IsType<ProjectResponse>(ReadApiResultData(result));
        Assert.Empty(project.Tools);
        Assert.Empty(project.EnvironmentVariables);
        Assert.Empty(service.McpToolServerIds!);
        Assert.Empty(service.SkillIds!);
        Assert.Empty(service.ConnectionIds!);
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

    private static Project CreateProject() =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Name = "Project A",
            Type = ProjectType.UserDefined,
            Workspace = "~/project-a",
            EnvironmentVariables = new Dictionary<string, string>(),
            CreateTime = new DateTimeOffset(2026, 7, 13, 1, 0, 0, TimeSpan.Zero),
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
        public IReadOnlyList<Guid>? ConnectionIds { get; private set; }

        public Task<IReadOnlyList<Project>> ListAsync(Expression<Func<Project, bool>>? predicate = null) =>
            Task.FromResult<IReadOnlyList<Project>>([_project]);

        public Task<IReadOnlyList<Project>> ListForCurrentUserAsync() => ListAsync();

        public Task<string?> GetProjectExtraSettingAsync(Guid? projectId) => throw new NotSupportedException();

        public Task<Guid?> ResolveProjectIdAsync(Guid? projectId) => throw new NotSupportedException();

        public Task<Project?> CreateAsync(Project project)
        {
            CreatedProject = project;
            return Task.FromResult<Project?>(_project);
        }

        public Task<Project?> CreateAsync(
            Project project,
            IEnumerable<Guid>? mcpToolServerIds,
            IEnumerable<Guid>? skillIds,
            IEnumerable<Guid>? connectionIds
        )
        {
            CreatedProject = project;
            CaptureRelationIds(mcpToolServerIds, skillIds, connectionIds);
            return Task.FromResult<Project?>(_project);
        }

        public Task<bool> DeleteAsync(Guid id) => throw new NotSupportedException();

        public Task<Project?> GetAsync(Guid id) => Task.FromResult<Project?>(_project);

        public Task<Project?> GetForCurrentUserAsync(Guid id) => GetAsync(id);

        public Task<Project?> UpdateAsync(Guid id, Action<Project> updateAction)
        {
            updateAction(_project);
            return Task.FromResult<Project?>(_project);
        }

        public Task<Project?> UpdateAsync(
            Guid id,
            Action<Project> updateAction,
            IEnumerable<Guid>? mcpToolServerIds,
            IEnumerable<Guid>? skillIds,
            IEnumerable<Guid>? connectionIds
        )
        {
            updateAction(_project);
            CaptureRelationIds(mcpToolServerIds, skillIds, connectionIds);
            return Task.FromResult<Project?>(_project);
        }

        private void CaptureRelationIds(
            IEnumerable<Guid>? mcpToolServerIds,
            IEnumerable<Guid>? skillIds,
            IEnumerable<Guid>? connectionIds
        )
        {
            McpToolServerIds = mcpToolServerIds?.ToList();
            SkillIds = skillIds?.ToList();
            ConnectionIds = connectionIds?.ToList();
        }
    }
}
