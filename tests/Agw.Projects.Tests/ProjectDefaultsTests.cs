namespace Agw.Projects.Tests;

public sealed class ProjectDefaultsTests
{
    [Fact]
    public void GetDefaultWorkspace_GuidOnly_UsesLegacyProjectIdPath()
    {
        var projectId = Guid.CreateVersion7();

        var workspace = ProjectDefaults.GetDefaultWorkspace(projectId);

        Assert.Equal($"~/.agw/projects/{projectId:N}", workspace);
    }

    [Fact]
    public void GetDefaultWorkspace_IsolationDisabled_UsesNormalizedNameUnderUserHome()
    {
        var home = Path.Combine(AppContext.BaseDirectory, ".scratch", "project-defaults-home");

        var workspace = ProjectDefaults.GetDefaultWorkspace(Guid.CreateVersion7(), "Project_A", false, home);

        Assert.Equal(Path.GetFullPath(Path.Combine(home, ".agw", "Project_A")), workspace);
        Assert.True(Path.IsPathFullyQualified(workspace));
    }

    [Fact]
    public void GetDefaultWorkspace_IsolationEnabled_UsesProjectIdUnderUserHome()
    {
        var projectId = Guid.CreateVersion7();
        var home = Path.Combine(AppContext.BaseDirectory, ".scratch", "project-defaults-home");

        var workspace = ProjectDefaults.GetDefaultWorkspace(projectId, "Project_A", true, home);

        Assert.Equal(Path.GetFullPath(Path.Combine(home, ".agw", "projects", projectId.ToString("N"))), workspace);
        Assert.True(Path.IsPathFullyQualified(workspace));
    }
}
