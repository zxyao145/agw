# @agw/execution-types

Web、Desktop、Mobile 与 extension SDK 共享的 execution 类型定义：

- 结构化消息与消息内容
- SignalR command 输入、Turn 信息与人机交互
- 通信事件与订阅条件
- 权限状态、工具分组与流式消息合批接口

本包仅通过 `types` 条件导出类型，没有运行时入口或依赖。

```ts
import type { ExecutionMessage, ChatEvent, InteractionResponse } from "@agw/execution-types";
```

`@agw/execution-core` 使用这些类型实现消息处理，并通过包入口继续导出公共类型。`@agw/extension-sdk` 直接依赖本包。

```sh
pnpm --filter @agw/execution-types typecheck
pnpm --filter @agw/execution-types test
```

类型测试覆盖 command 输入、事件与人机交互的类型判断，以及无效权限和事件通道。
