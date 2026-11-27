---
title: "开发 Server 与 Desktop 扩展"
description: "使用 Agw.Extensions.Sdk 与 @agw/extension-sdk 开发、打包与调试可加载扩展。"
weight: 45
lastmod: 2026-11-26
translationKey: docs/development/extension-sdk
---

除了内置的 Tool、ToolBlock 和集成 Plugin，AGW 支持通过独立扩展包向 Server 和 Desktop 增加能力。本页说明两套扩展 SDK 的结构、清单格式、注册方式以及组合扩展的打包与调试流程。

开始前建议先阅读[架构与模块边界]({{< relref "/docs/development/architecture" >}})和[扩展 Tools 与 Integrations]({{< relref "/docs/development/extensions" >}})。运维侧的安装目录、停机更新与启用流程见[Server 扩展与 ACP 主机配置]({{< relref "/docs/operations/extensions" >}})。

## 三种扩展形态

| 形态 | `manifestVersion` | `targets` | 运行环境 | 适用场景 |
| --- | --- | --- | --- | --- |
| Desktop 扩展 | `1` | 无需填写 | Desktop `ExtensionHost` 的独立 `WebContentsView` 会话 | 文件查看器、主区与侧边栏视图、Ribbon 入口、主题、对话与仓库辅助能力 |
| Server 扩展 | `2` | `1` | Server 进程内的独立 collectible `AssemblyLoadContext` | 自定义 HTTP 接口、扩展 Tools 与 Skills、事件处理、通知渠道、后台服务与独立业务表 |
| Server/Desktop 组合扩展 | `2` | `3` | 同时运行在 Server 与 Desktop | 需要服务端持久化或后台处理，并提供专属 Desktop 界面的完整业务扩展（例如 TaskPilot、Feishu） |

所有扩展都使用小写 GUID v7 作为唯一 `id`，在扩展生命周期内保持不变。

## Server 扩展开发（`Agw.Extensions.Sdk`）

Server 扩展是一个独立的 .NET 程序集目录，放置在 `Extensions:Directory`（默认 `./Extensions`）下的子目录中，根部包含 `manifest.json` 与全部声明文件。每个扩展在 Host 进程内按 generation 加载到独立的 collectible `AssemblyLoadContext`，停机更新后在下次启动生效。

### 清单格式（`manifest.json`）

```json
{
  "manifestVersion": 2,
  "id": "019a4c17-5f00-7a80-8b3d-2b732f48a101",
  "name": "taskpilot",
  "displayName": "TaskPilot",
  "version": "1.0.0",
  "description": "Rules, matters, reviews and automated delivery managed through TaskPilot.",
  "author": "Agw",
  "targets": 1,
  "server": {
    "apiVersion": 1,
    "entryAssembly": "server/Agw.Extensions.TaskPilot.dll",
    "entryType": "Agw.Extensions.TaskPilot.TaskPilotExtension",
    "permissions": {
      "events": {
        "publish": ["taskpilot.*"],
        "publishRoot": true
      }
    }
  },
  "files": {
    "server/Agw.Extensions.TaskPilot.dll": "<sha256>"
  }
}
```

- `entryAssembly` 与 `entryType` 指向实现 `IServerExtension` 的程序集与完整类型名。
- `files` 列出包内相对路径及其 SHA-256 摘要；Host 启动时校验清单与文件摘要，任一项不匹配都会阻止启动。
- 发布自定义事件时，需在 `server.permissions.events` 中声明允许发布的事件类型前缀。

### 实现 `IServerExtension` 与 JSON 序列化

引用 `Agw.Extensions.Sdk`，实现 `IServerExtension` 的三个方法：`ConfigureServices`、`StartAsync` 与 `StopAsync`。

Server 扩展必须注册自身源码生成的 `JsonSerializerContext`。运行时会拒绝未注册生成 metadata 的 collectible 扩展类型（包括数组与泛型中嵌套的扩展类型）。Endpoint 请求响应和 `IExtensionTool.CreateFunction(JsonSerializerOptions)` 都使用同一个 generation 拥有的 `JsonOptions`。

```csharp
using System.Text.Json.Serialization;
using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk;
using Microsoft.Extensions.DependencyInjection;

[JsonSerializable(typeof(CalendarRequest))]
[JsonSerializable(typeof(CalendarResponse))]
internal partial class CalendarJsonContext : JsonSerializerContext { }

public sealed class CalendarExtension : IServerExtension
{
    public void ConfigureServices(IServiceCollection services, IExtensionRegistration registration)
    {
        registration.RegisterJsonContext(CalendarJsonContext.Default);
        registration.RegisterEndpoint(
            new ExtensionEndpointDescriptor
            {
                Id = "events-list",
                Method = "GET",
                RelativePath = "events/list",
                Summary = "List calendar events",
            },
            static (provider, context, request, cancellationToken) =>
                ValueTask.FromResult(ExtensionEndpointResponse.OkJson(new CalendarResponse(), context.JsonOptions))
        );
    }

    public ValueTask StartAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
```

### 通过 `IExtensionRegistration` 注册能力

`IExtensionRegistration` 提供以下扩展注册点：

| 方法 | 说明 |
| --- | --- |
| `RegisterJsonContext` | 注册源码生成的 `JsonSerializerContext` metadata |
| `RegisterEndpoint` | 注册 HTTP 接口，统一挂载在 `/api/ext/<name>/<RelativePath>` 下 |
| `RegisterTool<TTool>` | 注册实现 `IExtensionTool` 的工具，显式声明 `Name`、`RequiredPermission` 与 `AllowInPlanMode` |
| `RegisterSkill` | 注册扩展自带的 Skill 与内容提供器；启用扩展后在运行时自动提供给能力组合，无需写入 `skill` 表或手动导入 |
| `RegisterEventHandler` | 注册事件处理器，接收匹配订阅的 `ExtensionEventDelivery` |
| `RegisterEventSourceProvider<TProvider>` | 注册自定义事件来源提供器 |
| `RegisterNotificationChannel<TChannel>` | 注册通知渠道（例如 Feishu 通知） |
| `RegisterOutboxResultHandler<THandler>` | 按 `kind` 注册 Outbox 结果处理器 |
| `RegisterBackgroundService<TService>` | 注册随扩展 generation 启动和停止的后台服务 |
| `RegisterDatabase` / `RegisterDbContext` | 注册扩展自有的数据库上下文与迁移 |

运行时通过 `IExtensionContext` 提供当前扩展标识（`ExtensionId`、`GenerationId`、`Epoch`、`ReleaseHash`）、`DataDirectory`、`CurrentUser`、`Leases`、`Secrets`、`JsonOptions`、`Logger` 与 `HostCapabilities`。

### 独立业务表与 EF Core（`Agw.Extensions.Sdk.EntityFrameworkCore`）

需要持久化自有数据的 Server 扩展可引用 `Agw.Extensions.Sdk.EntityFrameworkCore`，并在打包时包含迁移程序集与数据库 Provider 依赖：

1. 在 `ConfigureServices` 中调用 `registration.RegisterDbContext<CalendarDbContext, CalendarMigrationsMarker>()`。
2. 继承 `ExtensionDbContext`，在 `ConfigureExtensionModel` 中配置实体映射。所有业务表实体必须实现 `IExtensionOwnedEntity`（包含 `CreateBy`、`CreateTime`、`UpdateBy`、`UpdateTime`）；子实体可实现 `IExtensionOwnedChild<TRoot, TRootKey>`。
3. 使用简短的小写 `snake_case` 表名；运行时自动加上稳定前缀 `zext_<extensionIdN>_`。查询自动按当前用户过滤，缺少用户上下文时返回空结果。SDK 拒绝 `IgnoreQueryFilters`、`ExecuteUpdate`、`ExecuteDelete`、原生 SQL 查询与命令以及外部事务。
4. 在迁移程序集中为 SQLite 和 PostgreSQL 分别生成迁移类与模型快照，并标记 `[ExtensionMigrationProvider(ExtensionDatabaseProvider.Sqlite)]` 或 `[ExtensionMigrationProvider(ExtensionDatabaseProvider.Postgres)]`。设计时 factory 需启用 `UseSnakeCaseNamingConvention()` 并替换 `IMigrationsAssembly` 为 `ExtensionMigrationsAssembly`。
5. 迁移中禁止使用原生 SQL、外键约束、序列、Check 约束或计算列。需要对业务表全部行写入同一列值时，使用 `ExtensionUpdateAllRowsOperation`。

每个业务事务在执行前与提交前都会持有共享数据库锁并校验本地 generation 与活动作用域；迁移阶段持有独占锁并在同一打开连接上完成整批迁移。停机更新扩展时，系统保留原有业务表与迁移历史，扩展作者需保证新版本与已存数据的兼容性。

## Desktop 扩展开发（`@agw/extension-sdk`）

Desktop 扩展运行在 Electron 的 `ExtensionHost` 中，每个扩展拥有独立会话（`persist:agw-extension-<id>`），每个后台页、侧边栏视图、主区视图与设置页都运行在独立的 `WebContentsView` 中。前端可使用任意能构建为静态文件的技术栈（原生 JS、React、Vue、WASM 等）。

### 目录结构与 `manifest.json`

每个扩展包放在以其 `id` 命名的目录中，根部包含 `manifest.json`。清单中的所有路径都必须是包内相对 POSIX 路径，`__agw/` 为宿主保留前缀。`id` 必须是小写 GUID v7，`name`（去除首尾空白后不区分大小写）不能为 `host`。

下面是一个文件查看器扩展的清单示例：

```json
{
  "manifestVersion": 1,
  "id": "01a173b5-526c-74d8-864c-0e2da2d0a21c",
  "name": "Excalidraw",
  "version": "1.0.0",
  "description": "Opens and edits .excalidraw files in the Files view.",
  "author": "Agw",
  "icon": "icon.svg",
  "contributes": {
    "fileViewers": [
      {
        "id": "editor",
        "title": "Excalidraw",
        "icon": "PenTool",
        "patterns": ["*.excalidraw"],
        "entry": "index.html"
      }
    ]
  },
  "permissions": ["fs.cloud.read", "fs.cloud.write"]
}
```

`contributes` 支持声明的扩展点包括：

- `ribbon`：Ribbon 图标按钮，可切换侧边栏视图或打开主区视图。
- `sidebarViews`：左侧栏视图页面。
- `mainViews`：主工作区标签页视图。
- `fileViewers`：按文件名通配符（`*`、`?`，不区分大小写）匹配的文件查看器，支持 `priority`（`default` 或 `option`）。
- `settings`：在 **Settings → Extensions** 中展示的扩展设置页。
- `themes`：JSON 或 CSS 主题定义（`{ id, title, path }`）。
- `background`：扩展后台脚本入口。

### 接入 SDK 与编写视图

开发阶段从 `@agw/extension-sdk` 导入类型与函数；打包工具（如 Vite）将 `@agw/extension-sdk` 标记为 external，并在运行时映射到宿主提供的 `/__agw/sdk.js`（完整地址 `agw-extension://<id>/__agw/sdk.js`，类型声明位于 `__agw/sdk.d.ts`）。在 Desktop 的 **Settings → Extensions** 中也可以直接导出 SDK 类型文件供独立仓库开发使用。

视图页面使用 `defineView`，后台脚本使用 `defineBackground`，也可直接调用 `connect()` 获取 `ExtensionApi`。下面参考内置 `image-viewer` 的入口写法：

```tsx
import { defineView, type ExtensionApi, type FileViewerParams } from "@agw/extension-sdk";

let extensionApi: ExtensionApi | null = null;

defineView({
  onLoad(api, params) {
    api.theme.apply();
    extensionApi = api;
    renderFile(api, params as FileViewerParams);
  },
  onShow(params) {
    if (extensionApi) {
      renderFile(extensionApi, params as FileViewerParams);
    }
  },
  onHide() {
    // 切换标签页时可暂停动画或释放临时状态
  },
  onUnload() {
    cleanup();
  },
});
```

### 常用 `ExtensionApi` 能力

- **工作区与标签页（`workspace`）**：`openMainView(id, params?, options?)` 在当前 Project 的标签页组中打开或激活主视图（支持 `key`、`title`、`preview` 与最大 64 KiB 的 JSON `params`）；`openFile(uri, options?)` 打开云端文件标签页；文件查看器可通过 `setUnsavedChanges(unsaved)` 标记未保存状态，通过 `setFileHeaderItems(items)` 向文件标题栏的 **More** 菜单添加操作项。
- **文件与文档会话（`fs`、`documents`）**：`fs.readTextDocument`、`fs.writeTextDocument` 与 `fs.listTextEncodings` 在主进程处理编码检测与转换；`fs.writeFile` 支持 `expectedSha256` 做并发写入保护；`documents.openText` 与 `documents.openBinary` 提供完整的文档编辑会话，自动接管 `Cmd/Ctrl+S` 保存、保存状态展示与磁盘变更冲突提示。
- **悬浮对话与消息观察（`chat`）**：声明 `chat.composer` 后，视图可调用 `chat.createFloatingChat({ context })` 创建可拖拽停靠的悬浮对话按钮，并通过 `setContext` 同步当前文档路径，或调用 `chat.composer.insertText` 向输入框插入文本。声明 `chat.observe` 可通过 `chat.observe(filter, listener)` 订阅当前 Server 连接上的对话通信事件（每个扩展最多 16 个订阅）。
- **会话执行（`execution`）**：声明 `execution` 后，调用 `execution.createSession` 打开执行会话。Server 记录对话创建者，扩展只能写入自己创建的对话、尚未接受过回合的开放对话以及新建对话；其他方已保存的对话会返回 `readOnly: true`。
- **界面与主题（`ui`、`dialog`、`theme`）**：`ui.openExternal` 打开外部链接（未在 `hostPermissions` 中的地址会弹窗确认）；`ui.previewImage` 复用宿主图片预览层；`dialog.showMessageBox` 弹出确认对话框；`theme.apply()` 与 `theme.onDidChange` 同步宿主明暗模式、主题变量及 CSS 主题。

### 主题扩展（JSON 与 CSS）

- **JSON 主题**：包含 `common`、`light`、`dark` 字段，映射受支持的主题 Token（颜色、`--radius`、字体变量），由宿主写入 `<html>` 并分发给扩展页面。
- **CSS 主题**：支持标准 CSS、包内 `@import` 以及包内图片与字体资源内联。主进程在加载阶段使用 PostCSS 与 `css-tree` 校验并内联资源，禁止外部 URL、越界路径与 SVG 脚本。宿主 `<html>` 带有 `data-agw-document="desktop"`，扩展页面带有 `data-agw-document="extension"` 和 `data-agw-extension="<id>"`，可按作用域编写样式。

### 权限声明与安全隔离

扩展必须在清单的 `permissions` 中显式声明所需权限，启用时由主进程独立确认窗口向用户展示审批。可用权限包括：

- 常规权限：`context.user`、`context.project`、`context.agent`、`fs.cloud.read`、`chat.composer`、`clipboard.write`。
- 高风险权限（审批窗口高亮提示）：`project.repositories`、`fs.local.read`、`fs.local.write`、`fs.cloud.write`、`execution`、`a2a`、`chat.observe`、`clipboard.read`。

网络访问受 `hostPermissions` 约束：每个扩展有独立的 CSP 和本地回环出口代理，未获批准的外部主机请求、页面导航、弹窗与 Service Worker 都会被拦截。

## Server/Desktop 组合扩展

组合扩展使用 `manifestVersion: 2` 且 `targets: 3`，在同一个 `manifest.json` 中同时声明 `server` 与 `desktop` 段：

- `desktop` 段包含 `apiVersion`、`contributes` 与 `permissions`，全部 Desktop 静态文件放在包内的 `desktop/` 子目录中。
- 根级 `files` 需包含每个 Server 与 Desktop 文件的 SHA-256 摘要。
- 管理员在 Server 启用扩展后，Desktop 可直接从已连接的 Server 下载该扩展的 Desktop 包。下载的 ZIP 保留原始 `manifest.json` 与完整的 `desktop/` 文件集合；`releaseHash` 标识原始清单，`packageHash` 校验下载压缩包。

## 构建、调试与测试

仓库 `src/extensions/` 提供了可直接参考的完整实现：

- 组合扩展：`src/extensions/taskpilot`、`src/extensions/feishu`
- Desktop 文件查看器与视图：`src/extensions/markdown`、`src/extensions/excalidraw`、`src/extensions/image-viewer`、`src/extensions/bare-repository`
- 主题扩展：`src/extensions/soft-theme`、`src/extensions/warm-minimalism-theme`

构建仓库内扩展（在 `src` 目录执行）：

```bash
pnpm --filter @agw/extension-taskpilot build
pnpm --filter @agw/extension-feishu build
```

本地调试 Desktop 扩展时，可在 **Settings → Extensions** 从构建产物目录安装扩展，修改并重新构建后点击 **Reload** 刷新，在详情页点击 **Inspect** 打开对应 frame 的 DevTools，并查看最近被拦截的网络或权限请求。

在 `src` 目录运行扩展相关自动化测试（运行 `test:extensions` 会启动独立的 Electron 与 SQLite Standalone Host）：

```bash
pnpm --filter @agw/desktop test
pnpm --filter @agw/desktop test:extension-network
pnpm --filter @agw/desktop test:extensions
```

## 实现与参考

- [Server extension SDK](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Extensions.Sdk/README.md)
- [Server extension EF Core SDK](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Extensions.Sdk.EntityFrameworkCore/README.md)
- [Desktop extension SDK](https://github.com/zxyao145/agw/blob/main/src/clients/packages/extension-sdk/README.md)
- [Desktop extension runtime](https://github.com/zxyao145/agw/blob/main/src/clients/desktop/README.md)
- [TaskPilot extension](https://github.com/zxyao145/agw/blob/main/src/extensions/taskpilot/README.md)
- [Feishu extension](https://github.com/zxyao145/agw/blob/main/src/extensions/feishu/README.md)
