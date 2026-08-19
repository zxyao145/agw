using System.Text.Json.Serialization;

namespace Agw.Shared.Data.Entities.Tools;

public static class ToolValueObjectKinds
{
    public const string Tool = "tool";
    public const string ToolBlock = "toolBlock";
}

/// <summary>
/// Defines the stable names used as JSON type discriminators for persisted Tool definitions.
/// </summary>
/// <remarks>
/// These values are part of the storage and API contract. Adding a selectable Tool requires a
/// matching <see cref="JsonDerivedTypeAttribute"/> on <see cref="ToolDefinition"/>.
/// </remarks>
public static class ToolDefinitionNames
{
    public const string AskUserQuestion = "ask_user_question";
    public const string Bash = "bash";
    public const string Diff = "diff";
    public const string GenerateGuid = "generate_guid";
    public const string GitClone = "git_clone";
    public const string PowerShell = "powershell";
    public const string RunShell = "run_shell";
    public const string WebFetch = "web_fetch";
    public const string WebSearch = "web_search";

    public static IReadOnlyList<string> All { get; } =
    [AskUserQuestion, Bash, Diff, GenerateGuid, GitClone, PowerShell, RunShell, WebFetch, WebSearch];
}

/// <summary>
/// Defines the stable names used as JSON type discriminators for persisted Tool Block definitions.
/// </summary>
public static class ToolBlockDefinitionNames
{
    public const string Todo = "todo";
    public const string Mode = "mode";
    public const string ProjectMemory = "project-memory";
    public const string UserMemory = "user-memory";
    public const string FileAccess = "file-access";
    public const string BackgroundAgents = "background-agents";

    public static IReadOnlyList<string> All { get; } =
    [Todo, Mode, ProjectMemory, UserMemory, FileAccess, BackgroundAgents];
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ToolValue), ToolValueObjectKinds.Tool)]
[JsonDerivedType(typeof(ToolBlockValue), ToolValueObjectKinds.ToolBlock)]
public abstract record ToolValueObject
{
    public abstract string GetDefinitionName();
}

public sealed record ToolValue : ToolValueObject
{
    [JsonRequired]
    public ToolDefinition Definition { get; init; } = null!;

    public override string GetDefinitionName() => Definition?.GetDefinitionName() ?? string.Empty;
}

public sealed record ToolBlockValue : ToolValueObject
{
    [JsonRequired]
    public ToolBlockDefinition Definition { get; init; } = null!;

    public override string GetDefinitionName() => Definition?.GetDefinitionName() ?? string.Empty;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "name")]
[JsonDerivedType(typeof(AskUserQuestionToolDefinition), ToolDefinitionNames.AskUserQuestion)]
[JsonDerivedType(typeof(BashToolDefinition), ToolDefinitionNames.Bash)]
[JsonDerivedType(typeof(DiffToolDefinition), ToolDefinitionNames.Diff)]
[JsonDerivedType(typeof(GenerateGuidToolDefinition), ToolDefinitionNames.GenerateGuid)]
[JsonDerivedType(typeof(GitCloneToolDefinition), ToolDefinitionNames.GitClone)]
[JsonDerivedType(typeof(PowerShellToolDefinition), ToolDefinitionNames.PowerShell)]
[JsonDerivedType(typeof(RunShellToolDefinition), ToolDefinitionNames.RunShell)]
[JsonDerivedType(typeof(WebFetchToolDefinition), ToolDefinitionNames.WebFetch)]
[JsonDerivedType(typeof(WebSearchToolDefinition), ToolDefinitionNames.WebSearch)]
public abstract record ToolDefinition
{
    public abstract string GetDefinitionName();

    internal abstract object? GetOptions();
}

public abstract record ToolDefinition<TOptions> : ToolDefinition
    where TOptions : class, new()
{
    [JsonRequired]
    public TOptions Options { get; init; } = new();

    internal override object? GetOptions() => Options;
}

public sealed record EmptyToolOptions;

public sealed record AskUserQuestionToolDefinition : ToolDefinition<EmptyToolOptions>
{
    public override string GetDefinitionName() => ToolDefinitionNames.AskUserQuestion;
}

public sealed record BashToolDefinition : ToolDefinition<EmptyToolOptions>
{
    public override string GetDefinitionName() => ToolDefinitionNames.Bash;
}

public sealed record DiffToolDefinition : ToolDefinition<EmptyToolOptions>
{
    public override string GetDefinitionName() => ToolDefinitionNames.Diff;
}

public sealed record GenerateGuidToolDefinition : ToolDefinition<EmptyToolOptions>
{
    public override string GetDefinitionName() => ToolDefinitionNames.GenerateGuid;
}

public sealed record GitCloneToolDefinition : ToolDefinition<EmptyToolOptions>
{
    public override string GetDefinitionName() => ToolDefinitionNames.GitClone;
}

public sealed record PowerShellToolDefinition : ToolDefinition<EmptyToolOptions>
{
    public override string GetDefinitionName() => ToolDefinitionNames.PowerShell;
}

public sealed record RunShellToolDefinition : ToolDefinition<EmptyToolOptions>
{
    public override string GetDefinitionName() => ToolDefinitionNames.RunShell;
}

public sealed record WebFetchToolDefinition : ToolDefinition<EmptyToolOptions>
{
    public override string GetDefinitionName() => ToolDefinitionNames.WebFetch;
}

public sealed record WebSearchToolDefinition : ToolDefinition<EmptyToolOptions>
{
    public override string GetDefinitionName() => ToolDefinitionNames.WebSearch;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "name")]
[JsonDerivedType(typeof(TodoToolBlockDefinition), ToolBlockDefinitionNames.Todo)]
[JsonDerivedType(typeof(ModeToolBlockDefinition), ToolBlockDefinitionNames.Mode)]
[JsonDerivedType(typeof(ProjectMemoryToolBlockDefinition), ToolBlockDefinitionNames.ProjectMemory)]
[JsonDerivedType(typeof(UserMemoryToolBlockDefinition), ToolBlockDefinitionNames.UserMemory)]
[JsonDerivedType(typeof(FileAccessToolBlockDefinition), ToolBlockDefinitionNames.FileAccess)]
[JsonDerivedType(typeof(BackgroundAgentsToolBlockDefinition), ToolBlockDefinitionNames.BackgroundAgents)]
public abstract record ToolBlockDefinition
{
    public abstract string GetDefinitionName();

    internal abstract object? GetOptions();
}

public abstract record ToolBlockDefinition<TOptions> : ToolBlockDefinition
    where TOptions : class, new()
{
    [JsonRequired]
    public TOptions Options { get; init; } = new();

    internal override object? GetOptions() => Options;
}

public sealed record TodoToolBlockDefinition : ToolBlockDefinition<EmptyToolOptions>
{
    public override string GetDefinitionName() => ToolBlockDefinitionNames.Todo;
}

public sealed record ModeToolBlockDefinition : ToolBlockDefinition<EmptyToolOptions>
{
    public override string GetDefinitionName() => ToolBlockDefinitionNames.Mode;
}

public sealed record ProjectMemoryToolBlockDefinition : ToolBlockDefinition<ProjectMemoryToolBlockOptions>
{
    public override string GetDefinitionName() => ToolBlockDefinitionNames.ProjectMemory;
}

public sealed record UserMemoryToolBlockDefinition : ToolBlockDefinition<EmptyToolOptions>
{
    public override string GetDefinitionName() => ToolBlockDefinitionNames.UserMemory;
}

public sealed record ProjectMemoryToolBlockOptions
{
    public ProjectMemoryStorage Storage { get; init; } = ProjectMemoryStorage.Database;
}

[JsonConverter(typeof(JsonStringEnumConverter<ProjectMemoryStorage>))]
public enum ProjectMemoryStorage
{
    [JsonStringEnumMemberName("database")]
    Database,

    [JsonStringEnumMemberName("filesystem")]
    FileSystem,
}

public sealed record FileAccessToolBlockDefinition : ToolBlockDefinition<EmptyToolOptions>
{
    public override string GetDefinitionName() => ToolBlockDefinitionNames.FileAccess;
}

public sealed record BackgroundAgentsToolBlockDefinition : ToolBlockDefinition<BackgroundAgentsToolBlockOptions>
{
    public override string GetDefinitionName() => ToolBlockDefinitionNames.BackgroundAgents;
}

public sealed record BackgroundAgentsToolBlockOptions
{
    public IReadOnlyList<Guid> AllowedAgentIds { get; init; } = [];
}

public static class ToolValueObjectValidation
{
    public static string? GetError(IReadOnlyList<ToolValueObject>? values)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values ?? [])
        {
            var hasDefinition = value switch
            {
                ToolValue { Definition: not null } => true,
                ToolBlockValue { Definition: not null } => true,
                _ => false,
            };
            if (!hasDefinition)
            {
                return "Every Tool value must contain a definition.";
            }

            var options = value switch
            {
                ToolValue tool => tool.Definition.GetOptions(),
                ToolBlockValue toolBlock => toolBlock.Definition.GetOptions(),
                _ => null,
            };
            if (options == null)
            {
                return $"Tool definition '{value.GetDefinitionName()}' must contain an options object.";
            }

            var name = value.GetDefinitionName();
            if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
            {
                return $"Tool definition name '{name}' is empty or duplicated.";
            }
        }

        return null;
    }
}
