using Agw.Projects.Domain.Behaviors;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;

namespace Agw.Projects.Tests;

public class ProjectBehaviorTests
{
    [Fact]
    public void TryPrepareForCreate_BlankName_ReturnsFalse()
    {
        var project = new Project { Name = "  " };

        var result = new ProjectBehavior(project).TryPrepareForCreate();

        Assert.False(result);
        Assert.Equal(Guid.Empty, project.Id);
        Assert.Null(project.CreateBy);
    }

    [Fact]
    public void TryPrepareForCreate_ValidProject_NormalizesNameWithoutAuditStamping()
    {
        var project = new Project { Name = "Project A" };

        var result = new ProjectBehavior(project).TryPrepareForCreate();

        Assert.True(result);
        Assert.NotEqual(Guid.Empty, project.Id);
        Assert.Equal("Project_A", project.Name);
        Assert.Null(project.CreateBy);
        Assert.Equal(default, project.CreateTime);
    }

    [Fact]
    public void TryPrepareForCreate_NameWithSpecialCharacters_NormalizesNameAndDefaultWorkspace()
    {
        var project = new Project { Name = "  Demo Project: Alpha?  ", Workspace = "  " };

        var result = new ProjectBehavior(project).TryPrepareForCreate();

        Assert.True(result);
        Assert.Equal("Demo_Project_Alpha", project.Name);
        Assert.Equal($"~/.agw/projects/{project.Id:N}", project.Workspace);
    }

    [Fact]
    public void TryPrepareForCreate_CustomWorkspace_PreservesTrimmedWorkspace()
    {
        var project = new Project { Name = "Project A", Workspace = "  ~/custom/project-a  " };

        var result = new ProjectBehavior(project).TryPrepareForCreate();

        Assert.True(result);
        Assert.Equal("Project_A", project.Name);
        Assert.Equal("~/custom/project-a", project.Workspace);
    }

    [Fact]
    public void TryPrepareForCreate_NameThatCannotBecomeFolderName_ReturnsFalse()
    {
        var project = new Project { Name = " <>:\"/\\|?* " };

        var result = new ProjectBehavior(project).TryPrepareForCreate();

        Assert.False(result);
        Assert.Equal(Guid.Empty, project.Id);
        Assert.Null(project.CreateBy);
        Assert.Null(project.Workspace);
    }

    [Fact]
    public void TryApplyUpdate_BlankNameAfterUpdate_ReturnsFalse()
    {
        var project = new Project { Id = Guid.CreateVersion7(), Name = "Project A" };

        var result = new ProjectBehavior(project).TryApplyUpdate(current => current.Name = "");

        Assert.False(result);
        Assert.Null(project.UpdateBy);
        Assert.Null(project.UpdateTime);
    }

    [Fact]
    public void TryApplyUpdate_ValidUpdate_ChangesRootWithoutAuditStamping()
    {
        var project = new Project { Id = Guid.CreateVersion7(), Name = "Project A" };

        var result = new ProjectBehavior(project).TryApplyUpdate(current => current.Description = "Updated");

        Assert.True(result);
        Assert.Equal("Updated", project.Description);
        Assert.Null(project.UpdateBy);
        Assert.Null(project.UpdateTime);
    }

    [Fact]
    public void TryPrepareForCreate_NullEnvironmentVariables_NormalizesToEmptyDictionary()
    {
        var project = new Project { Name = "Project A", EnvironmentVariables = null! };

        var result = new ProjectBehavior(project).TryPrepareForCreate();

        Assert.True(result);
        Assert.Empty(project.EnvironmentVariables);
    }

    [Fact]
    public void TryPrepareForCreate_EmptyEnvironmentVariableValue_PreservesValue()
    {
        var project = new Project
        {
            Name = "Project A",
            EnvironmentVariables = new Dictionary<string, string> { ["EMPTY"] = string.Empty },
        };

        var result = new ProjectBehavior(project).TryPrepareForCreate();

        Assert.True(result);
        Assert.Equal(string.Empty, project.EnvironmentVariables["EMPTY"]);
    }

    [Fact]
    public void TryPrepareForCreate_EnvironmentVariableNameWithWhitespace_TrimsName()
    {
        var project = new Project
        {
            Name = "Project A",
            EnvironmentVariables = new Dictionary<string, string> { ["  API_KEY  "] = "secret" },
        };

        var result = new ProjectBehavior(project).TryPrepareForCreate();

        Assert.True(result);
        var environmentVariables = project.EnvironmentVariables;
        Assert.Equal("secret", Assert.Single(environmentVariables).Value);
        Assert.True(environmentVariables.ContainsKey("API_KEY"));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("BAD=NAME")]
    [InlineData("BAD\0NAME")]
    public void TryPrepareForCreate_InvalidEnvironmentVariableName_Throws(string name)
    {
        var project = new Project
        {
            Name = "Project A",
            EnvironmentVariables = new Dictionary<string, string> { [name] = "value" },
        };

        var exception = Assert.Throws<AgwException>(() => new ProjectBehavior(project).TryPrepareForCreate());

        Assert.Equal(ErrorCodes.InvalidProjectEnvironmentVariableName.Code, exception.Code);
    }

    [Fact]
    public void TryPrepareForCreate_DuplicateEnvironmentVariableNamesAfterTrim_Throws()
    {
        var project = new Project
        {
            Name = "Project A",
            EnvironmentVariables = new Dictionary<string, string> { ["API_KEY"] = "first", [" API_KEY "] = "second" },
        };

        var exception = Assert.Throws<AgwException>(() => new ProjectBehavior(project).TryPrepareForCreate());

        Assert.Equal(ErrorCodes.InvalidProjectEnvironmentVariableName.Code, exception.Code);
    }

    [Fact]
    public void TryApplyUpdate_EnvironmentVariables_NormalizesNames()
    {
        var project = new Project { Id = Guid.CreateVersion7(), Name = "Project A" };

        var result = new ProjectBehavior(project).TryApplyUpdate(current =>
            current.EnvironmentVariables = new Dictionary<string, string> { ["  API_KEY  "] = "updated" }
        );

        Assert.True(result);
        Assert.Equal("updated", project.EnvironmentVariables["API_KEY"]);
    }
}
