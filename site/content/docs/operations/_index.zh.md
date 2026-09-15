---
title: "部署运维"
description: "运行、配置并维护自托管实例。"
weight: 30
lastmod: 2026-09-15
translationKey: docs/operations/_index
icon: fa-solid fa-server
---

本章面向负责安装和维护 Server 的用户。首次部署可从单机开始；只有需要拆开管理与执行服务时，才需要阅读分离部署。

- [单机与 Docker 部署]({{< relref "/docs/operations/standalone" >}})：启动服务、保存数据并挂载项目目录。
- [分离部署]({{< relref "/docs/operations/split" >}})：准备共享服务，配置控制面、数据面和代理路由。
- [配置与认证]({{< relref "/docs/operations/configuration" >}})：按用途查找配置项、默认值和生效方式。
- [备份与升级]({{< relref "/docs/operations/backup" >}})：保留数据库、密钥和文件，验证能否恢复。
- [日志与常见问题]({{< relref "/docs/operations/troubleshooting" >}})：按故障现象逐步排查。
