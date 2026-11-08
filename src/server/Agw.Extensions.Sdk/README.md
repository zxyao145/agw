# Server extension SDK

Install Server extensions while every Host is stopped. Place each extension in its own folder under `Extensions:Directory` (default `./Extensions` relative to the Server executable), with `manifest.json` at that folder's root and all declared files present. Every Host scans once at startup. New extensions are disabled until an administrator enables them in Web at **System → Server extensions**. Updates and directory removal take effect on the next startup; enablement and business data are retained.

Server extension 在全部 Host 停机期间安装。每个扩展使用独立目录，位于 `Extensions:Directory` 配置的位置；默认路径为 Server 程序目录下的 `./Extensions`。目录根部包含 `manifest.json` 和全部声明文件。各 Host 启动时扫描一次，新扩展默认停用；管理员通过 Web 的 **System → Server extensions** 启用。更新及目录删除在下次启动时生效，启用设置和业务数据继续保留。

Executions resume against the currently enabled extension by ID. Extension authors own compatibility with saved state, schemas, tool arguments, and responses; incompatible calls fail through normal execution handling.

执行恢复时根据 ID 使用当前已启用的 extension。扩展作者负责已保存状态、schema、Tool 参数和响应的兼容性；不兼容调用按正常执行失败处理。

Skills registered through `IExtensionRegistration.RegisterSkill` are provided automatically by enabled extensions during runtime capability composition. Keep each descriptor ID stable within the extension. The descriptor and content provider belong to the current generation; registration does not create rows in the `skill` table or require Agent/Project Skill selection. Execution manifests record the contributing extension ID for recovery and disablement waiting.

启用的 extension 通过 `IExtensionRegistration.RegisterSkill` 自动向运行时能力组合提供 Skills。每个 descriptor ID 在 extension 内保持稳定。Descriptor 和内容提供器属于当前 generation；注册过程无需写入 `skill` 表，也无需配置 Agent/Project Skill 选择。执行记录保存提供 Skill 的 extension ID，用于执行恢复和停用等待。

Server extensions register JSON metadata from their own source-generated `JsonSerializerContext`. The runtime rejects collectible extension types that do not have registered generated metadata, including extension types nested inside arrays and generic types.

服务端 extension 必须注册自身生成的 `JsonSerializerContext` JSON metadata。Runtime 会拒绝未注册生成 metadata 的 collectible extension 类型，也会检查数组及泛型类型中的 extension 类型。

For combined Server/Desktop packages, place every Desktop asset under `desktop/` and include its SHA-256 in `files`. Desktop entry, background, icon, and theme paths use that directory. Server downloads preserve the original `manifest.json` and include the complete Desktop file set; `releaseHash` identifies the original manifest, while `packageHash` verifies the downloadable ZIP.

Server/Desktop 组合扩展的全部 Desktop 文件保存在 `desktop/` 下，并在 `files` 中声明 SHA-256。Desktop 入口、后台脚本、图标和主题使用该目录中的路径。下载包保留原始 `manifest.json` 并包含完整的 Desktop 文件集合；`releaseHash` 标识原始 manifest，`packageHash` 校验下载 ZIP。

```csharp
[JsonSerializable(typeof(CalendarRequest))]
[JsonSerializable(typeof(CalendarResponse))]
internal partial class CalendarJsonContext : JsonSerializerContext { }

public sealed class CalendarExtension : IServerExtension
{
    public void ConfigureServices(IServiceCollection services, IExtensionRegistration registration)
    {
        registration.RegisterJsonContext(CalendarJsonContext.Default);
    }
}
```

Use `IExtensionContext.JsonOptions` for endpoint request and response serialization. Extension Tools receive the same generation-owned options through `IExtensionTool.CreateFunction(JsonSerializerOptions serializerOptions)`.

Endpoint 请求和响应使用 `IExtensionContext.JsonOptions` 完成序列化。Extension Tool 通过 `IExtensionTool.CreateFunction(JsonSerializerOptions serializerOptions)` 接收同一组 generation-owned options。
