using System.Reflection;
using System.Text.Json;

using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Tasks.Domain.Services;

namespace Agw.Tasks.Tests;

public class ProjectTaskDomainServiceTests
{
    private readonly ProjectTaskDomainService _service = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ProjectTask_RemovesLegacyTargetAndDescriptionProperties()
    {
        Assert.Null(typeof(ProjectTask).GetProperty("AgentType"));
        Assert.Null(typeof(ProjectTask).GetProperty("AgentId"));
        Assert.Null(typeof(ProjectTask).GetProperty("Description"));
    }

    [Fact]
    public void TryPrepareForCreate_MissingRequiredFields_ReturnsFalse()
    {
        var task = new ProjectTask
        {
            ContextId = " ",
        };

        var result = _service.TryPrepareTaskForCreate(task, "tester");

        Assert.False(result);
        Assert.Equal(Guid.Empty, task.Id);
    }

    [Fact]
    public void TryPrepareForCreate_ValidTask_InitializesTaskWithoutDescriptionOrAgent()
    {
        var before = DateTime.UtcNow;
        var task = new ProjectTask
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Title = "  New task  ",
            ContextId = "context-1",
        };
        var result = _service.TryPrepareTaskForCreate(task, "tester");

        Assert.True(result);
        Assert.Equal("New task", task.Title);
        Assert.Equal(ProjectTaskStatus.Pending, task.Status);
        Assert.InRange(task.CreateTime, before, DateTime.UtcNow);
    }

    [Fact]
    public void TryUpdateTitle_BlankTitle_ReturnsFalse()
    {
        var task = new ProjectTask { Title = "Original" };

        var result = _service.TryUpdateTitle(task, "   ", "tester");

        Assert.False(result);
        Assert.Equal("Original", task.Title);
    }

    [Fact]
    public void TryUpdateTitle_ValidTitle_TrimsAndSetsMetadata()
    {
        var before = DateTime.UtcNow;
        var task = new ProjectTask { Title = "Original" };

        var result = _service.TryUpdateTitle(task, "  Updated title  ", "tester");

        Assert.True(result);
        Assert.Equal("Updated title", task.Title);
        Assert.Equal("tester", task.UpdateBy);
        Assert.InRange(task.UpdateTime!.Value, before, DateTime.UtcNow);
    }

    [Fact]
    public void ProjectTaskDomainService_ExposesOnlyCurrentPublicHelpers()
    {
        var methodNames = typeof(ProjectTaskDomainService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(
            [
                "TryMarkFailed",
                "TryMarkSucceeded",
                "TryPrepareForCreate",
                "TryUpdateTitle"
            ],
            methodNames);
    }

    [Fact]
    public void TryMarkSucceeded_RunningTask_SetsSuccessState()
    {
        var before = DateTime.UtcNow;
        var task = new ProjectTask
        {
            Status = ProjectTaskStatus.Running,
            ErrorMessage = "old error",
        };

        var result = _service.TryMarkSucceeded(task, "tester");

        Assert.True(result);
        Assert.Equal(ProjectTaskStatus.Succeeded, task.Status);
        Assert.Null(task.ErrorMessage);
        Assert.Equal("tester", task.UpdateBy);
        Assert.InRange(task.UpdateTime!.Value, before, DateTime.UtcNow);
        Assert.Equal(task.UpdateTime, task.FinishedTime);
    }

    [Fact]
    public void TryMarkSucceeded_NonRunningTask_ReturnsFalse()
    {
        var task = new ProjectTask { Status = ProjectTaskStatus.Pending };

        var result = _service.TryMarkSucceeded(task, "tester");

        Assert.False(result);
    }

    [Fact]
    public void TryMarkFailed_RunningTask_SetsFailureState()
    {
        var before = DateTime.UtcNow;
        var task = new ProjectTask { Status = ProjectTaskStatus.Running };

        var result = _service.TryMarkFailed(task, "boom", "tester");

        Assert.True(result);
        Assert.Equal(ProjectTaskStatus.Failed, task.Status);
        Assert.Equal("boom", task.ErrorMessage);
        Assert.Equal("tester", task.UpdateBy);
        Assert.InRange(task.UpdateTime!.Value, before, DateTime.UtcNow);
        Assert.Equal(task.UpdateTime, task.FinishedTime);
    }

    [Fact]
    public void TryMarkFailed_NonRunningTask_ReturnsFalse()
    {
        var task = new ProjectTask { Status = ProjectTaskStatus.Pending };

        var result = _service.TryMarkFailed(task, "boom", "tester");

        Assert.False(result);
    }
}
