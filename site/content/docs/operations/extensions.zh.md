---
title: "Server 扩展与 ACP 主机配置"
description: "安装 Server 扩展、配置 ACP registry Agent，并管理宿主机运行时依赖。"
weight: 35
lastmod: 2026-11-26
translationKey: docs/operations/extensions
---

Server 扩展可以为 AGW Host 增加服务端接口、事件处理器、通知渠道、Skills 以及随包发布的 Desktop 视图；ACP registry Agent 则需要在执行主机上提前安装外部可执行程序或运行环境。两者都需要在主机文件系统完成准备，并配合重启生效。

## Server 扩展

安装、更新和移除 Server 扩展时，必须先停止**全部**相关 Host（`Standalone`、`Control Plane` 与 `Data Plane`）。

每个扩展放在 `Extensions:Directory`（默认为相对 Server 可执行文件目录的 `./Extensions`）下的独立子目录中，根部包含 `manifest.json` 和全部声明文件。分离部署时，需要为全部 Host 准备完全一致的扩展目录内容。

```json
{
  "Extensions": {
    "Directory": "./Extensions",
    "PublicBaseUrl": "https://agw.example.com"
  }
}
```

### 启用与管理扩展

1. 停止全部 Host，在 `Extensions:Directory` 中放入或更新扩展目录，再重新启动。每个 Host 在启动时扫描一次目录并保留本地快照；目录不存在时使用空清单，manifest 或文件摘要校验失败会阻止启动。
2. 以管理员身份登录 Web，打开 **System → Server extensions**（`/extensions`），查看已发现的版本以及当前 Host 的运行状态。
3. 启用扩展。新发现的扩展默认为停用状态；启用设置保存在全局 `setting` 表（`extensions.enabled.<id>`）中并作用于各 Host，运行状态和错误则保存在各自 Host 本地。

停用或移除扩展目录后，系统仍保留启用设置、扩展业务表、迁移历史和数据目录。已启用的扩展会在运行时自动提供其注册的 Skills，无需在 Skills 页面导入或手动绑定。

### 仓库内扩展（TaskPilot 与飞书）

仓库 `src/extensions/` 下提供两个内置源码的 Server 扩展：

- **TaskPilot**（`@agw/extension-taskpilot`）：把订阅事件转换为用户自己的事项与执行回合，利用用户的 GitHub Connection 管理 Pull Request 投递，并提供 Desktop 视图。在 `src` 目录执行 `pnpm --filter @agw/extension-taskpilot build` 构建。
- **飞书（Feishu）**（`@agw/extension-feishu`）：提供飞书通知与身份绑定。在 `src` 目录执行 `pnpm --filter @agw/extension-feishu build` 构建。运行飞书扩展的 Host 需要 `PATH` 中存在 `node`（Node.js）供其 SDK worker 使用，并配置 `Extensions:PublicBaseUrl` 为浏览器可达的 HTTPS 地址（本地开发允许回环 HTTP）。

## ACP registry Agent

ACP Agent 运行在执行主机（`Standalone` 或 `Data Plane`）上。安装 registry 中的 Agent 时，请使用与运行 Server 相同的操作系统账号和 `AgwDataDir`（或相同的 `AcpAgents:InstallRoot`，默认 `{AgwDataDir}/acp-agents`）：

```bash
./agw-server acp install goose codex-acp
```

官方容器镜像默认不包含 Node.js 和 `uv`。需要使用 `npx` 或 `uvx` 分发的 ACP Agent 时，请在派生执行镜像中安装 Node.js 或 `uv` 并在构建镜像阶段完成安装，同时在安装命令和容器启动参数中保持 `--AcpAgents:InstallRoot=/opt/agw/acp-agents` 一致。

在 Distributed 模式下，为全部 Data Plane 副本部署相同的派生镜像，使用 `FullAccess` 权限模式，并把 Agent 自身的会话目录挂载到各副本一致的共享路径（例如在环境变量中配置 `CODEX_HOME=/shared/agw-acp/codex` 并挂载共享卷）。

## 实现与参考

- [配置与认证]({{< relref "/docs/operations/configuration" >}})
- [Server extension SDK](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Extensions.Sdk/README.md)
- [TaskPilot extension](https://github.com/zxyao145/agw/blob/main/src/extensions/taskpilot/README.md)
- [Feishu extension](https://github.com/zxyao145/agw/blob/main/src/extensions/feishu/README.md)
- [ACP 部署配置](https://github.com/zxyao145/agw/blob/main/docs/4.Deployment.md)
