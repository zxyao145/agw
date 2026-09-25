---
title: "单机与 Docker 部署"
description: "在本机运行包含 Web 的 Standalone Server。"
weight: 10
lastmod: 2026-09-25
translationKey: docs/operations/standalone
---

Standalone 在一个 Server 中提供管理页面、对话执行和定时任务，适合本机试用或单台主机部署。默认使用 SQLite 保存数据，由当前进程运行任务，无需另行部署数据库服务。

可以使用 Docker 镜像，或自行构建与操作系统及处理器架构匹配的 Portable Server。两种方式都包含 Web 界面；下面先完成本机访问，再说明目录挂载和远程访问。

## Docker 本机试用

```bash
docker run -d --name agw \
  --restart unless-stopped \
  -p 127.0.0.1:30816:8080 \
  -v agw-data:/data \
  ghcr.io/zxyao145/agw:latest
```

打开 `http://localhost:30816/setup` 完成初始化。通过 Docker 端口映射访问时，容器看到的请求来源不是回环地址，Setup 页面会要求填写一次性 Setup Code；用 `docker logs agw` 在启动日志中查找 “Agw remote setup code”。镜像包含静态 Web，浏览器直接访问 Server 即可。示例的 `latest` 适用于试用；持续运行的部署应换成 Releases 对应的固定版本标签。

要让 Agent 访问主机项目，额外添加显式 bind mount，再将容器内路径配置为 Project Workspace。不要将主机路径误填成容器可见路径。

## Portable Server

Portable Server 不随 Release 提供，需要在仓库根目录构建，例如：

```bash
PUBLISH_MODE=portable APP_VERSION=0.1.0 RIDS=linux-x64 ./publish.sh
```

产物位于 `artifacts/publish/portable/agw-server-<版本>-<RID>/`，同时生成对应的压缩包。在产物目录中启动：

```bash
./agw-server serve
```

Windows 使用 `agw-server.exe serve`。默认监听 `http://127.0.0.1:30816`，需要调整时使用 `ASPNETCORE_URLS`。未设置 `ASPNETCORE_URLS` 且 30816 端口已被占用时，Server 会改用一个随机的本机空闲端口，并把实际地址记录在 `<AgwDataDir>/runtime/server.json` 中。先验证本机 Setup、Web 和一次对话，再配置远程访问。

## 持久化与网络

Docker 数据目录为 `/data`，日志默认独立写入工作目录下的 `logs`；持久化文件日志需要另外挂载配置的日志路径。Project Workspace 也独立于数据卷。

远程部署需要正确配置 AllowedHosts、受信代理、HTTPS 和 WebSocket 转发。仓库 Compose 是带域名与代理配置的示例，使用前替换成真实环境值。完整持久化范围见[备份与升级]({{< relref "/docs/operations/backup" >}})。

## 实现与参考

- [Compose example](https://github.com/zxyao145/agw/blob/main/deploy/compose.yaml)
- [Deployment](https://github.com/zxyao145/agw/blob/main/docs/4.Deployment.md)
