using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Runtime;
using Agw.Shared.Utils;

namespace Agw.Agents.Execution.Agents.Runtime;

public static class AgentRuntimeFactoryUtil
{
    /// <summary>
    /// <para>把执行上下文中的工作区快照（缺失时按项目配置兜底生成）写回项目对象，保证两条创建路径的目录一致。</para>
    /// <para>
    /// Applies the execution workspace snapshot (or one derived from the project) onto the project object so both
    /// creation paths resolve directories identically.
    /// </para>
    /// </summary>
    public static ProjectWorkspaceSnapshot ApplyWorkspaceSnapshot(Project project)
    {
        var workspace =
            ExecutionContextSlot.GetWorkspaceSnapshot(project.Id)
            ?? ProjectWorkspacePaths.CreateSnapshot(
                project.Id,
                project.Workspace,
                project.AdditionalDirectories.Select(directory => new ProjectWorkspaceDirectory(
                    directory.Id,
                    directory.Path
                ))
            );
        project.Workspace = workspace.Workspace;
        project.AdditionalDirectories = workspace
            .AdditionalDirectories.Select(directory => new ProjectDirectory
            {
                Id = directory.Id,
                Path = directory.Path,
            })
            .ToList();

        return workspace;
    }

    public static IReadOnlyDictionary<string, string> MergeEnvironmentVariables(
        params IReadOnlyDictionary<string, string>?[] variableLayers
    )
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var variables in variableLayers)
        {
            if (variables == null)
            {
                continue;
            }

            foreach (var (key, value) in variables)
            {
                merged[key] = value;
            }
        }

        return merged;
    }

    public static string BuildInstructions(string? systemPrompt)
    {
        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            systemPrompt = "You are a helpful agent.";
        }

        return systemPrompt;
    }
}
