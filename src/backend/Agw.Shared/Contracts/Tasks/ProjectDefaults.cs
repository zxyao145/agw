using Agw.Shared.Enums;
using Agw.Shared.Tasks.Entities;

namespace Agw.Shared.Tasks;

public static class ProjectDefaults
{
    public static readonly Guid DefaultBuiltInId = Guid.Parse("11111111-1111-1111-1111-000000000001");
    public static readonly Guid ClaudeCodeId = Guid.Parse("11111111-1111-1111-1111-000000000002");
    public static readonly Guid A2AId = Guid.Parse("11111111-1111-1111-1111-000000000003");

    public const string DefaultBuiltInName = "default-built-in";
    public const string ClaudeCodeName = "claude-code";
    public const string A2AName = "a2a";

    public static IReadOnlyList<Project> BuiltInProjects { get; } =
    [
        new Project
        {
            Id = DefaultBuiltInId,
            Name = DefaultBuiltInName,
            Description = "Default built-in project for general task execution.",
            Type = ProjectType.DefaultBuiltIn,
            Enable = true
        },
        new Project
        {
            Id = ClaudeCodeId,
            Name = ClaudeCodeName,
            Description = "Built-in project for Claude Code task execution.",
            Type = ProjectType.DefaultBuiltIn,
            Enable = true
        },
        new Project
        {
            Id = A2AId,
            Name = A2AName,
            Description = "Built-in project for A2A task execution.",
            Type = ProjectType.DefaultBuiltIn,
            Enable = true
        }
    ];

    public static Guid GetDefaultProjectIdentifier(Guid? projectId) =>
        projectId == null ? DefaultBuiltInId : projectId.Value;



    public static string GetDefaultProjectIdentifier(string? projectId) =>
        string.IsNullOrWhiteSpace(projectId) ? DefaultBuiltInId.ToString("D") : projectId.Trim();

    public static string GetClaudeCodeProjectIdentifier(string? projectId) =>
        string.IsNullOrWhiteSpace(projectId) ? ClaudeCodeId.ToString("D") : projectId.Trim();

    public static string GetA2AProjectIdentifier(string? projectId) =>
        string.IsNullOrWhiteSpace(projectId) ? A2AId.ToString("D") : projectId.Trim();
}
