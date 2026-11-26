---
title: "使用 Extensions"
description: "在 Desktop 和 Server 中安装、启用、配置与管理扩展，切换文件查看器与界面主题。"
weight: 105
lastmod: 2026-11-26
translationKey: docs/guides/extensions
---

AGW 支持通过扩展为桌面工作区和服务端增加额外能力。Desktop 扩展用于增强本地文件查看、工作区视图和界面主题；Server 扩展用于向服务端增加业务接口、事件处理、通知渠道和专属 Skills，并可附带对应的 Desktop 视图。

## Desktop 扩展

Desktop 随安装包内置了一组常用扩展，首次启动时默认启用，可按需停用或卸载：

| 内置扩展 | 用途 |
| --- | --- |
| **Markdown** | 打开 `*.md` / `*.markdown` 与文本文件，提供 **Mixed**（分块编辑）、**Preview**（只读渲染）与 **Code**（Monaco 代码编辑与格式化）三种视图，支持 Mermaid 图表渲染与文件编码切换 |
| **Excalidraw** | 在文件标签页中打开、编辑并保存 `*.excalidraw` 绘图文件 |
| **Image Viewer** | 查看 PNG、APNG、JPEG、GIF、WebP、AVIF、BMP、ICO 与 SVG 图片，支持缩放、拖拽平移与像素/文件大小显示 |
| **Bare Repository** | 在 Ribbon 增加 **Repositories** 侧边栏与管理视图，按目录约定管理主分支检出与功能 worktree，浏览任意分支和未提交变更 |
| **Soft Theme** | 提供柔和色调的浅色与深色主题 |
| **Warm Minimalism Theme** | 提供暖色调浅色与深色主题及工作区背景样式 |

### 安装与管理步骤

前提：已打开 AGW Desktop 并连接到目标 Server（安装本地文件夹或 `.zip` 包时无需连接远程 Server）。

1. 打开 **Settings → Extensions**（选择主题分类可只查看主题扩展）。
2. 选择安装来源：
   - **从文件夹安装**：选择包含 `manifest.json` 的扩展目录。
   - **从 `.zip` 包安装**：选择扩展压缩包。
   - **从已连接的 Server 安装**：当服务端已启用组合扩展（例如 TaskPilot、Feishu）时，直接下载并安装其对应的 Desktop 视图包。
3. 点击启用扩展。Desktop 会在独立的确认窗口中列出该扩展请求的全部权限；涉及本地文件读写、云端文件写入、仓库操作、会话执行、A2A、对话观察或读取剪贴板等高风险权限会以警示颜色标出。核对后确认授权。
4. 启用后检查预期入口：
   - 带 Ribbon 或视图的扩展会出现在左侧 Ribbon 或主区标签页。
   - 文件查看器会在打开匹配后缀的文件时显示在文件标题栏右侧；可点击切换查看器，或在 **More** 菜单中设为该文件类型的默认查看器。
   - 主题扩展会出现在 **Settings → Appearance** 的主题列表中，可与 System / Light / Dark 模式组合使用。

在扩展详情页可以执行 **Reload**（重新加载扩展）、停用、**Uninstall**（卸载时可选择是否同时删除扩展数据目录），也可以打开扩展目录或数据目录。如果扩展导致界面异常，可通过托盘菜单的 **Reload without extensions** 进入安全模式，再在 **Settings → Extensions** 点击 **Resume extensions** 恢复。

## Server 扩展

Server 扩展运行在 AGW Host 进程中，由管理员在停机期间放入 `Extensions:Directory` 并重启生效。常见内置源码扩展包括：

- **TaskPilot**：将事件中心订阅的事件转化为用户自己的事项与串行执行回合，支持驱动 Agent 或通过用户的 GitHub Connection 管理 Pull Request 投递，并提供 Desktop 主视图。
- **飞书（Feishu）**：提供飞书通知渠道、卡片交互与账号身份绑定，并提供 Desktop 设置视图。

### 启用与使用步骤

前提：管理员已在所有相关 Host 停机时放入扩展目录并完成重启，配置方法见[Server 扩展与 ACP 主机配置]({{< relref "/docs/operations/extensions" >}})。

1. 管理员登录 Web，打开 **System → Server extensions**（`/extensions`）。
2. 核对已扫描到的扩展版本与当前 Host 的运行状态，点击启用。新发现的扩展默认为停用状态；启用设置对所有 Host 生效。
3. 启用后即可直接使用扩展提供的能力：
   - 扩展注册的 Skills 会自动参与运行时能力组合，无需在 **Skills** 页面手动导入或绑定。
   - 扩展注册的通知渠道与事件处理器会在 **Events** 与 **Notifications** 中可用。
   - 若扩展附带 Desktop 视图（如 TaskPilot、Feishu），用户在 Desktop 的 **Settings → Extensions** 中从已连接的 Server 安装后，即可通过左侧 Ribbon 打开对应视图并完成个人设置。

停用或移除 Server 扩展后，原有启用设置、扩展业务表、迁移历史和数据目录会被保留。

## 限制与延伸阅读

- Desktop 扩展的网络访问受其声明并获批的 `hostPermissions` 限制；查看器或后台页异常时，可在扩展详情页查看最近被拦截的请求。
- Server 扩展的安装、升级与移除都需要在所有参与部署的 Host 停机期间完成。
- 运维部署细节见[Server 扩展与 ACP 主机配置]({{< relref "/docs/operations/extensions" >}})。
- 开发自定义扩展见[开发 Server 与 Desktop 扩展]({{< relref "/docs/development/extension-sdk" >}})。

## 实现与参考

- [Desktop extension runtime](https://github.com/zxyao145/agw/blob/main/src/clients/desktop/README.md)
- [Server extension SDK](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Extensions.Sdk/README.md)
- [TaskPilot extension](https://github.com/zxyao145/agw/blob/main/src/extensions/taskpilot/README.md)
- [Feishu extension](https://github.com/zxyao145/agw/blob/main/src/extensions/feishu/README.md)
