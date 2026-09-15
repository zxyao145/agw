---
title: "开始第一次对话"
description: "从配置模型到运行 Agent 的最短路径。"
weight: 40
lastmod: 2026-09-15
translationKey: docs/start/first-chat
---

前提：已完成初始化，可以进入管理界面，并有可用模型提供商的凭据。此流程先创建自定义 Agent，不需要外部 CLI。

## 1. 准备模型连接

打开模型管理页面，配置下面三项。不同客户端的表单布局可能不同，但需要的信息相同。

| 配置 | 要准备什么 | 用途 |
| --- | --- | --- |
| Provider | 服务商要求的协议、API 地址和 API Key | 告诉 AGW 去哪里调用模型、如何认证 |
| Model | 服务商提供的模型 ID、上下文窗口和最大输出长度 | 告诉 AGW 使用哪个模型及其内容长度限制 |
| Model Provider | 将模型与提供商关联 | 作为 Agent 实际选择的模型连接 |

模型 ID 应使用服务商给出的准确值。自动发现时出现的 `256,000 / 64,000` 只是上下文窗口和最大输出的默认值，需要按实际规格调整。字段说明见[模型提供商]({{< relref "/docs/guides/providers" >}})。

## 2. 创建一个简单的 Agent

在 **Agents** 中创建自定义 Agent，命名为“问答助手”，选择刚配置的 Model Provider，并填写指令：

```text
用中文回答问题。先给出直接答案，再解释必要的背景。
遇到信息不足的情况，请明确说明缺少什么。
```

保存并确认 Agent 已启用。这次只验证文字对话，工具、Skills 和工作流可以稍后再配置。

## 3. 发送第一条消息

打开 **Chat**，确认当前 Server，选择一个可用 Project 和“问答助手”，发送：

```text
请用两句话解释什么是工作目录，并给一个简单例子。
```

应能看到回复逐步出现，随后本次执行结束。再发送“把刚才的解释说得更简单一些”，检查 Agent 能否接着上一条消息回答。这可以同时验证模型连接和连续对话。

![AGW Desktop 对话界面：顶部选择 Project 和 Agent，底部输入消息。](/images/screenshots/desktop-chat.png)
{caption="AGW Desktop：确认 Project 和 Agent 后，在底部发送消息。"}

## 下一步

先验证纯文本对话，再按任务添加[工具与 Skills]({{< relref "/docs/guides/tools-skills" >}})。文件任务需要[配置 Project 的工作目录]({{< relref "/docs/guides/projects" >}})；已有 Codex 或 Claude Code 环境可以使用[外部 Agent]({{< relref "/docs/guides/external-agents" >}})。

## 没有收到回复

检查所选 Model Provider 是否可用、模型 ID 是否正确、凭据与地址是否匹配，并查看 Server 日志。对话可能在等待审批或用户输入；这种状态需要在 Chat 中处理。不要在未验证模型前同时加入大量工具或复杂流程。

## 实现与参考

- [Model configuration UI](https://github.com/zxyao145/agw/tree/main/src/clients/packages/providers)
- [Agent runtime](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Agents.Execution/README.md)
