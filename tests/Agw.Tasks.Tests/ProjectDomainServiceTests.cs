using Agw.Shared.Data.Entities.Tasks;
using Agw.Tasks.Domain.Services;
using Agw.Testing;

namespace Agw.Tasks.Tests;

public class ProjectDomainServiceTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 7, 13, 10, 0, 0, TimeSpan.Zero);
    private readonly ProjectDomainService _service = new(new TestTimeProvider(UtcNow));

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
        var project = new Project { Name = "Project A" };

        var result = _service.TryPrepareForCreate(project, "tester");

        Assert.True(result);
        Assert.NotEqual(Guid.Empty, project.Id);
        Assert.Equal("tester", project.CreateBy);
        Assert.Equal(UtcNow, project.CreateTime);
    }

    [Fact]
    public void TryPrepareForCreate_NameWithSpecialCharacters_NormalizesNameAndDefaultWorkspace()
    {
        var project = new Project
        {
            Name = "  Demo Project: Alpha?  ",
            Workspace = "  "
        };

        var result = _service.TryPrepareForCreate(project, "tester");

        Assert.True(result);
        Assert.Equal("Demo_Project_Alpha", project.Name);
        Assert.Equal("~/.agw/Demo_Project_Alpha", project.Workspace);
    }

    [Fact]
    public void TryPrepareForCreate_CustomWorkspace_PreservesTrimmedWorkspace()
    {
        var project = new Project
        {
            Name = "Project A",
            Workspace = "  ~/custom/project-a  "
        };

        var result = _service.TryPrepareForCreate(project, "tester");

        Assert.True(result);
        Assert.Equal("Project_A", project.Name);
        Assert.Equal("~/custom/project-a", project.Workspace);
    }

    [Fact]
    public void TryPrepareForCreate_NameThatCannotBecomeFolderName_ReturnsFalse()
    {
        var project = new Project { Name = " <>:\"/\\|?* " };

        var result = _service.TryPrepareForCreate(project, "tester");

        Assert.False(result);
        Assert.Equal(Guid.Empty, project.Id);
        Assert.Null(project.CreateBy);
        Assert.Null(project.Workspace);
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
        var project = new Project { Id = Guid.NewGuid(), Name = "Project A" };

        var result = _service.TryApplyUpdate(project, current => current.Description = "Updated", "tester");

        Assert.True(result);
        Assert.Equal("Updated", project.Description);
        Assert.Equal("tester", project.UpdateBy);
        Assert.Equal(UtcNow, project.UpdateTime);
    }
}
