namespace Agw.Tools.Impl.Basic;

/// <summary>
/// Sample. Provides basic utility tools for agents.
/// </summary>
[AiToolContainer(DefaultCategory = "Utility")]
public static class BasicTools
{
    [AiTool("generate_guid", Category = "Utility")]
    [Description("Generates a new unique identifier (GUID)")]
    public static string GenerateGuid()
    {
        return Guid.NewGuid().ToString();
    }
}
