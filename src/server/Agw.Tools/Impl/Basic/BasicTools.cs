namespace Agw.Tools.Impl.Basic;

/// <summary>
/// Sample. Provides basic utility tools for agents.
/// </summary>
[AiToolContainer(DefaultCategory = "Utility")]
public static class BasicTools
{
    [AiTool("generate_guid", Category = "Utility", AllowInPlanMode = true)]
    [Description("Generates a new unique identifier (GUID)")]
    [Obsolete("Test method")]
    public static string GenerateGuid()
    {
        return Guid.CreateVersion7().ToString();
    }
}
