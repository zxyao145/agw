---
title: "MCP 服务"
description: "连接工具服务，并核对服务端的网络与进程环境。"
weight: 90
lastmod: 2026-09-15
translationKey: docs/guides/mcp
---

MCP（Model Context Protocol）是一种让 Agent 使用外部工具的协议。MCP 服务会列出自己提供的操作，AGW 连接后可将这些工具交给自定义 Agent 使用。具体能做什么取决于所连接的服务。

配置前，准备服务提供的启动命令或地址、连接方式，以及所需凭据。模型本身仍在 Model Provider 中配置。

## 连接路径

1. 在 MCP 配置界面创建工具服务，按其要求配置传输、命令或端点与凭据。
2. 本地进程服务需要执行节点能启动对应命令；远程服务需要从执行节点访问。
3. 将服务绑定给自定义 Agent，开启新回合。
4. 检查可发现的工具，使用一个只读操作验证返回内容。

目录、命令和网络都以实际 Server/执行节点为准。远程 Desktop 连接不会使本机安装的 MCP 服务自动出现在 Server 上。

![AGW Desktop：以 stdio 方式配置 MCP Server，填写命令、参数、工作目录及必要的环境变量。](/images/screenshots/mcp-create.png)
{caption="AGW Desktop：以 stdio 方式配置 MCP Server，填写命令、参数、工作目录及必要的环境变量。"}

## 选择连接方式

| 方式 | 如何连接 | 配置前检查 |
| --- | --- | --- |
| stdio | AGW 启动一个进程，通过它的输入输出通信 | 执行主机上有对应程序，命令、参数和工作目录正确 |
| HTTP / SSE | AGW 访问正在运行的工具服务 | 服务地址和认证方式正确，执行主机能够访问 |

例如，stdio 命令在个人终端能运行，但 Server 使用另一个账号或运行在容器中时，可能找不到同一个程序。应在 Server 所用环境中检查命令和环境变量。远程地址则需要从执行主机测试连通性。

## 与 Integrations 的区别

MCP 是工具协议。Integration 是带目录定义、用户配置、凭据和 Connection 生命周期的能力接入方式；Integration 自身也可以通过 MCP 暴露工具。

Plugin MCP 支持 stdio、HTTP 和 SSE 源。向 HTTP/SSE 注入凭据的 Plugin MCP 源必须使用 HTTPS；凭据在调用作用域中解析，不应放进公开 URL 或提示词。

## 故障排查

工具未出现时检查绑定、启动命令、可执行文件、网络可达性与凭据。先在相同执行环境确认服务可用，再重试新的 Agent 回合。外部 CLI 的 MCP 配置按其自身机制处理，不等同于 AGW Connection 注入。

## 实现与参考

- [Integrations and MCP](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Integrations/README.md)
- [Agent execution](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Agents.Execution/README.md)
