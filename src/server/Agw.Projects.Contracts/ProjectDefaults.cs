namespace Agw.Projects.Contracts;

public static class ProjectDefaults
{
    public static readonly Guid DefaultBuiltInId = Guid.Parse("11111111-1111-1111-1111-000000000001");
    public static readonly Guid A2AId = Guid.Parse("11111111-1111-1111-1111-000000000003");

    public const string DefaultBuiltInName = "default-built-in";
    public const string A2AName = "a2a";

    public static Guid GetDefaultProjectIdentifier(Guid? projectId) =>
        projectId == null ? DefaultBuiltInId : projectId.Value;
}
