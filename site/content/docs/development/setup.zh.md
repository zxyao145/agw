---
title: "开发环境与运行"
description: "安装依赖，独立运行后端、Web、Desktop 或 Mobile。"
weight: 10
lastmod: 2026-09-25
translationKey: docs/development/setup
---

前提：.NET 10 SDK、Node.js 24、pnpm 12.5.1（版本由 `src/clients/package.json` 的 `packageManager` 指定）和 Git。只有构建容器镜像时才需要 Docker Buildx。以下应用命令在 AGW 仓库执行，文档站本身不依赖这些工具链。

## 后端

```bash
git clone https://github.com/zxyao145/agw.git
cd agw
git config core.hooksPath .githooks
dotnet restore Agw.slnx
dotnet tool restore
dotnet run --project src/server/Agw.Standalone.Host
```

在 `http://localhost:30816/setup` 初始化。热重载可将 `dotnet run` 改为 `dotnet watch`。

## 客户端

另开终端，从仓库根进入 workspace：

```bash
cd src/clients
pnpm install
pnpm dev:web
```

打开 `http://localhost:3001`。Desktop 使用 `pnpm dev:desktop`，其独立 renderer 运行在 `3000`，不需要同时运行 Web。Mobile 使用 `pnpm dev:mobile`，或 `pnpm android:mobile` / `pnpm ios:mobile`；原生工程由 Expo CNG 生成。

## 启动后应该看到什么

保留后端终端运行，再启动需要的客户端。Web 页面能打开后，先确认 Server 初始化和登录，再按[第一次对话]({{< relref "/docs/start/first-chat" >}})配置模型。仅看到前端页面还不能说明后端连接正常。

后端默认端口是 `30816`，Web 开发端口是 `3001`，Desktop 界面开发端口是 `3000`。端口被占用时先确认是否已有开发进程，不要误把其他服务当成 AGW。手机上的 `localhost` 指手机本身，真机连接需使用手机可访问的电脑地址。

## 验证

确认后端初始化完成，客户端能连接并运行一条简单消息。移动真机需要可达的后端地址；外部 CLI 需在执行进程环境下可用。

运行站点需要 Hugo Extended 与 Go，运行检查脚本 `scripts/check-site.py` 还需要 Python 3，命令见 `site/README.md`。不要将站点加入客户端 Turborepo，也不要让 Web/Desktop 消费站点产物。

## 实现与参考

- [Development commands](https://github.com/zxyao145/agw/blob/main/docs/1.Development.md)
- [Client scripts](https://github.com/zxyao145/agw/blob/main/src/clients/package.json)
