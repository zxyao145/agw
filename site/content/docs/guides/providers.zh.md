---
title: "模型提供商"
description: "配置 Provider、Model 与 Model Provider，校准模型限制。"
weight: 10
lastmod: 2026-09-15
translationKey: docs/guides/providers
---

让自定义 Agent 回答问题之前，需要告诉 AGW 使用哪个模型、请求发往哪里，以及如何认证。模型服务商通常会提供 API 地址、模型 ID 和 API Key；请先准备好这些信息。

本页介绍 AGW 中的模型配置。已有命令行工具配置的用户，也可以按[外部 Agent 指南]({{< relref "/docs/guides/external-agents" >}})接入。

## 三类配置

**Provider** 描述服务端点和认证；**Model** 描述模型及其限制；**Model Provider** 将两者关联，供 Agent 选择。仅创建 Model 不等于已经连通服务。

1. 在 Providers 选择匹配的协议类型并填写地址、凭据。
2. 在 Models 添加或发现模型，核对模型标识。
3. 填写上下文窗口及最大输出 token，建立 Model Provider。
4. 在 Agent 中选择该关联，用短问题验证回复。

当前协议类型包括 OpenAI Chat Completions、OpenAI Responses 和 Anthropic。兼容服务也需要匹配具体协议，不能仅凭名称包含 OpenAI 判断可用性。

![AGW Desktop：创建 Model Provider，配置协议、端点、认证和模型。图中尚未填写认证信息。](/images/screenshots/provider-create.png)
{caption="AGW Desktop：创建 Model Provider，配置协议、端点、认证和模型。图中尚未填写认证信息。"}

## 上下文限制

每个模型都有内容长度限制，填写时需要区分两个值：

- **上下文窗口**：一次请求能容纳的内容总量，包括历史对话、当前问题、工具结果以及模型的回复。
- **最大输出 token**：模型一次最多能生成多长的回复。token 是模型计算内容长度的单位，不等同于字数。

AGW 会根据这两个值为回复留出空间，并在对话内容过长时压缩发送给模型的内容。

自动发现模型时，AGW 可能填入 `256,000`（上下文窗口）和 `64,000`（最大输出 token）作为默认值。这不代表所选模型实际支持这么长的内容，请按模型服务商公布的限制填写。数值设得过大，可能导致请求被拒绝；如果短对话正常、聊久后报错，先核对这两个值，再查看 Server 日志中的具体错误。

成功标志是 Agent 能完成一次对话。凭据无效、地址错误或模型不可用时，先修复连接，再添加工具。不要把真实 API Key 放入共享提示词或 Git 文件。

## 实现与参考

- [Provider UI](https://github.com/zxyao145/agw/tree/main/src/clients/packages/providers)
- [Execution behavior](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Agents.Execution/README.md)
