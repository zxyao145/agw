# @agw/execution-core

Platform-neutral 执行消息处理核心，供 Web / Desktop（通过 `@agw/chat`）与 Mobile 共享。

## 职责

只包含与平台和传输实现无关的类型、纯函数与定时策略，**不建立任何网络连接**：

- 消息身份与流式合并（`src/message.ts`）
- 工具调用 / 结果的配对与分组（`src/tool-group.ts`）
- 结构化消息类型（`src/types.ts`）
- SignalR command payload、message 级终态与共享重连间隔（`src/protocol.ts`）
- generation-aware 50ms 流式消息合批（`src/batcher.ts`）

传输层由两个 Adapter 各自实现：

- Web / Desktop：SignalR（`@agw/chat` 的 `execution-hub.ts` / `execution-session-manager.ts`）
- Mobile：官方 `@microsoft/signalr` React Native adapter（`mobile/src/features/chat/execution-ws.ts`）

## 核心规则

### 消息身份（单一来源）

流式消息按**四元组**判同，避免不同轮次复用 `item_0` 等 ID 时串联：

```
streamingScopeId + messageId + role + author
```

- `scopeMessagesByUserTurn`：历史按 user 消息切 scope，user 之前用独立 fallback scope。
- `scopeStreamingMessage`：给单条消息打 scope（clone，不污染原对象）。
- `mergeStreamingMessages`：先检查尾消息，未命中才惰性建立身份索引；text 追加、非 text 按序 push。
- `getMessageStreamingScopeId`：优先读 `additionalProperties.streamingScopeId`（服务端持久化），再回退顶层字段。

### 工具配对

- `createMessageFragments`：把一条消息按 content 逐条拆成 `normal / result / function-call / function-result`，支持混合 content。
- `processMessages`：call / result 按 `JSON.stringify([streamingScopeId ?? null, callId])` 分组，支持多 call、乱序结果、跨轮复用 callId 隔离。

## 泛型：无损保留调用方类型

`message.ts` 与 `tool-group.ts` 的核心函数均为泛型，接收并返回调用方自己的消息类型：

- Web / Desktop：`AiMessage`（`author?: string`）
- Mobile：`AgwMessage`（`author?: string | null`）

因此本包**不 import `@agw/api`**，也不依赖任何客户端的类型定义。

## 消费方式

### pnpm Workspace

```jsonc
// 依赖声明
"dependencies": { "@agw/execution-core": "workspace:*" }
```

```ts
import { mergeStreamingMessages, processMessages } from "@agw/execution-core";
```

Web/Desktop 通过 `@agw/chat` 间接消费本包；`@agw/mobile` 作为同一 pnpm Workspace 的成员直接声明 `workspace:*` 依赖。Metro、Jest 和 TypeScript 都通过正常的 workspace package 解析，不维护源码 alias 或独立 lockfile。详见 [`mobile/README.md`](../../mobile/README.md) 的共享代码边界。

## 测试

```sh
pnpm --filter @agw/execution-core test
```
