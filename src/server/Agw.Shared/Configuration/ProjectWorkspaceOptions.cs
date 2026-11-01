namespace Agw.Shared.Configuration;

public sealed class ProjectWorkspaceOptions
{
    public const string SectionName = "Projects:Workspace";

    public bool IsolationEnabled { get; set; }
}
