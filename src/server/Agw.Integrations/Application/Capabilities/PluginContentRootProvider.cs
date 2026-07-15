namespace Agw.Integrations.Application.Capabilities;

/// <summary>
/// 提供 Plugin 内容资产的根目录，用于安全解析 Plugin Skill 等相对路径。
/// </summary>
public interface IPluginContentRootProvider
{
    /// <summary>
    /// 获取 Plugin 内容资产所在的绝对根目录。
    /// </summary>
    string ContentRoot { get; }
}

public sealed class AppContextPluginContentRootProvider : IPluginContentRootProvider
{
    public string ContentRoot => AppContext.BaseDirectory;
}

public sealed class FixedPluginContentRootProvider : IPluginContentRootProvider
{
    public FixedPluginContentRootProvider(string contentRoot)
    {
        ContentRoot = contentRoot;
    }

    public string ContentRoot { get; }
}
