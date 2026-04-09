using Agw.Shared.Data.Entities.Tasks;
using Agw.Tasks.Domain.Services;

namespace Agw.Tasks.Tests;

public class ProjectDomainServiceTests
{
    private readonly ProjectDomainService _service = new();

    [Fact]
    public void TryPrepareForCreate_BlankName_ReturnsFalse()
    {
        var project = new Project { Name = "  " };

        var result = _service.TryPrepareForCreate(project, "tester");

        Assert.False(result);
        Assert.Equal(Guid.Empty, project.Id);
        Assert.Null(project.CreateBy);
    }

    [Fact]
    public void TryPrepareForCreate_ValidProject_AssignsMetadata()
    {
        var before = DateTime.UtcNow;
        var project = new Project { Name = "Project A" };

        var result = _service.TryPrepareForCreate(project, "tester");

        Assert.True(result);
        Assert.NotEqual(Guid.Empty, project.Id);
        Assert.Equal("tester", project.CreateBy);
        Assert.InRange(project.CreateTime, before, DateTime.UtcNow);
    }

    [Fact]
    public void TryApplyUpdate_BlankNameAfterUpdate_ReturnsFalse()
    {
        var project = new Project { Id = Guid.NewGuid(), Name = "Project A" };

        var result = _service.TryApplyUpdate(project, current => current.Name = "", "tester");

        Assert.False(result);
        Assert.Null(project.UpdateBy);
        Assert.Null(project.UpdateTime);
    }

    [Fact]
    public void TryApplyUpdate_ValidUpdate_SetsMetadata()
    {
        var before = DateTime.UtcNow;
        var project = new Project { Id = Guid.NewGuid(), Name = "Project A" };

        var result = _service.TryApplyUpdate(project, current => current.Description = "Updated", "tester");

        Assert.True(result);
        Assert.Equal("Updated", project.Description);
        Assert.Equal("tester", project.UpdateBy);
        Assert.InRange(project.UpdateTime!.Value, before, DateTime.UtcNow);
    }
}
