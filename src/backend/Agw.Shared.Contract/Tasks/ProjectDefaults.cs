using Agw.Shared.Enums;
using Agw.Shared.Tasks.Entities;

namespace Agw.Shared.Tasks;

public static class ProjectDefaults
{
    public static readonly Guid DefaultBuiltId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid ClaudeCodeId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public const string DefaultBuiltName = "default-built";
    public const string ClaudeCodeName = "claude-code";

    public static IReadOnlyList<Project> BuiltInProjects { get; } =
    [
        new Project
        {
            Id = DefaultBuiltId,
            Name = DefaultBuiltName,
            Description = "Default built-in project for general task execution.",
            Type = ProjectType.DefaultBuilt,
            Enable = true
        },
        new Project
        {
            Id = ClaudeCodeId,
            Name = ClaudeCodeName,
            Description = "Built-in project for Claude Code task execution.",
            Type = ProjectType.ClaudeCode,
            Enable = true
        }
    ];

    public static string GetDefaultProjectIdentifier(string? projectId) =>
        string.IsNullOrWhiteSpace(projectId) ? DefaultBuiltName : projectId.Trim();

    public static string GetClaudeCodeProjectIdentifier(string? projectId) =>
        string.IsNullOrWhiteSpace(projectId) ? ClaudeCodeName : projectId.Trim();
}
