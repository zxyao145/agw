namespace Agw.Tools.Impl.Tools.Basic;

/// <summary>
/// Sample. Provides basic utility tools for agents.
/// </summary>
[AgwToolContainer(AgwToolPermission.None, DefaultCategory = "Utility")]
public static class BasicTools
{
    [AgwTool("generate_guid", AgwToolPermission.None, Category = "Utility", AllowInPlanMode = true)]
    [Description("Generates a new unique identifier (GUID)")]
    [Obsolete("Test method")]
    public static string GenerateGuid()
    {
        return Guid.CreateVersion7().ToString();
    }
}
