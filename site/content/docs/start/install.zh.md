---
title: "安装与配置 Server"
description: "选择安装方式，完成 Server 初始化并连接客户端。"
weight: 20
lastmod: 2026-09-25
translationKey: docs/start/install
aliases: ["/docs/start/setup/"]
---

AGW 需要一个持续运行的 Server，以及用来操作它的客户端。首次在本机使用可选 Desktop Full；已有 Server 则选 Desktop Client，或直接使用浏览器。安装后再配置模型服务或外部 Agent，即可开始对话。

## 安装方式

| 方式 | 适合场景 | 主要区别 |
| --- | --- | --- |
| Desktop Full | 在本机使用图形界面 | 同时安装桌面客户端和 Server，Server 作为当前用户的后台服务运行 |
| Desktop Client | 连接已运行的 Server | 只安装桌面客户端，不包含 Server，可连接本机或远程 Server |
| Docker | 容器化部署、自托管 | 在容器中运行 Server，包含 Web 界面，通过挂载保存数据和访问工作目录 |
| Portable Server | 直接在主机上部署服务 | 直接运行 Server 可执行程序，包含 Web 界面，无需 Docker 或桌面客户端 |
| 源码运行 | 开发与调试 | 自行构建和运行后端及所需客户端，可修改代码并调试各模块 |

## Desktop

1. 打开 [GitHub Releases](https://github.com/zxyao145/agw/releases)。
2. 按平台选择 Full 或 Client。Windows、Ubuntu 当前提供 x64；macOS 提供 x64 和 arm64。
3. Full 首次启动时完成 Server 的初始化；Client 连接现有 Server。

Full 和 Client 使用相同的应用标识，只能安装其中一种。Full 安装包含 Desktop Client 以及当前用户级 Server 后台服务；关闭 Desktop 不会自动停止 Server。安装包当前未签名或公证。

![AGW Desktop 对话界面：选择 Project 和 Agent 后输入消息。](/images/screenshots/desktop-chat.png)
{caption="AGW Desktop 对话界面：选择 Project 和 Agent 后输入消息。"}

## Docker 与 Portable Server

Docker 镜像随版本发布到 `ghcr.io/zxyao145/agw`。Portable Server 不随 Release 提供，需要在仓库根目录自行构建，例如 `PUBLISH_MODE=portable APP_VERSION=0.1.0 RIDS=linux-x64 ./publish.sh`，再按单机部署文档启动 `agw-server serve`。

根据部署需求选择以下方式：

- [单机与 Docker 部署]({{< relref "/docs/operations/standalone" >}})：在一个 Server 中运行完整服务，适合本地试用和单机自托管。
- [Control/Data Plane 分离部署]({{< relref "/docs/operations/split" >}})：将管理与调度、任务执行分开运行，适合需要独立部署或扩展执行节点的场景，包含共享数据库、目录及 Nginx 路由配置说明。

需要团队共享或远程访问时，配置域名、HTTPS 和反向代理。

Docker 镜像不包含 Claude Code、Codex、Pi 等任何外部 Agent；如需使用，请自行在容器中安装并完成相关配置。

## 源码

参考[开发环境]({{< relref "/docs/development/setup" >}})，先启动 Standalone Host，再运行 Web。后端默认端口为 `30816`，Web 开发端口为 `3001`。

## 配置 Server

前提：Server 已启动，数据库配置有效，数据目录可写。默认单机模式使用 SQLite 和 InProcess 执行。

### 首次初始化

1. 打开 Server 的 `/setup` 页面，例如本机的 `http://localhost:30816/setup`。Docker 或 Portable Server 包含 Web；源码模式也可先完成服务端初始化。
2. 设置管理员密码。只有在 Server 所在主机上直接用 `localhost` 或回环地址访问时，才不需要 Setup Code。通过域名、反向代理或其他主机访问，以及访问 Docker 容器映射的端口时，还需要填写启动日志中的一次性 Setup Code。
3. 提交后等待数据库初始化完成，页面会进入应用，无需再次重启。

初始化成功后，密码和登录设置会保存在数据库中，后续启动无需重复设置。需要备份或迁移时，请一并保存数据库和加密密钥，参见[备份与升级]({{< relref "/docs/operations/backup" >}})。

![Server 首次初始化页面：本机访问时设置管理员密码。此截图未提交初始化。](/images/screenshots/server-setup.png)
{width="480" caption="Server 首次初始化页面：本机访问时设置管理员密码。此截图未提交初始化。"}

### 连接客户端

- 远程 Web 使用管理员密码登录，获得会话 Cookie。Server 配置了身份提供商时，Web 登录页还会显示第三方账号按钮；Desktop 在 **Settings → Connections & app** 中每个 Server 下方显示 **Sign in with …** 按钮，通过系统浏览器登录后自动获得 API Key，见[配置与认证]({{< relref "/docs/operations/configuration" >}})。
- Desktop、Mobile 和自动化使用 API Key，通过 `Authorization: Bearer agw_...` 请求头发送。API Key 明文只在创建时显示一次。
- Desktop Full 的本地初始化由 Server 页面完成，主进程随后配置自己的 API Key，并使用操作系统凭据存储保护它。

API Key（访问密钥）相当于客户端连接 Server 的钥匙。给 Desktop Client 或 Mobile 配置远程连接时，先登录 Web，在 **Settings** 的 **Server access** 页面中，于 **API tokens** 填写 **Token name** 并创建一个 API Key，再将 Server 地址和完整的 API Key 填入客户端。Desktop Client 在 **Settings → Connections & app** 中点击 **+**（Add remote Server），填写 **Name**、**Server URL** 和 **API token**。手机上的 `localhost` 指手机本身，不能用它访问电脑上的 Server。

### 无人值守初始化

可以通过环境变量 `Setup__AdminPassword` 注入初始密码。分离部署仅在 Control Plane 初始化；Data Plane 不提供 Setup。不要将实际密码写进代码或站点文档。

已经初始化的服务不会被 Setup 参数覆盖。数据库与执行方式应在启动前使用标准配置设置，不能通过 Setup 表单切换。

完成后进入[第一次对话]({{< relref "/docs/start/first-chat" >}})。登录失败时先确认请求连接的是预期 Server。

## 实现与参考


- [Desktop packages](https://github.com/zxyao145/agw/blob/main/src/clients/desktop/README.md)
- [Server deployment](https://github.com/zxyao145/agw/blob/main/docs/4.Deployment.md)
- [Setup](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Setup/README.md)
- [Authentication](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Auth/README.md)
