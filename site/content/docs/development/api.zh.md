---
title: "API 与执行协议"
description: "区分管理 JSON API、SignalR 执行与 A2A 协议。"
weight: 30
lastmod: 2026-09-15
translationKey: docs/development/api
---

AGW 的管理操作和任务执行使用不同接口。创建或查询配置使用普通 HTTP JSON API；持续接收 Agent 回复和状态使用 SignalR 执行连接；对接其他 Agent 系统时可以使用 A2A。

接入前，先准备可访问的开发 Server 和有效 Token。具体参数以运行实例的 OpenAPI 及所属模块 `Contracts` 中的类型定义为准，避免照抄与运行版本不一致的请求。

## 协议边界

| 接口 | 用途与约定 |
| --- | --- |
| 管理 JSON API | Bens.Results `ApiResult` 封装，客户端 typed helpers 解包 |
| `/api/hubs/exec` | SignalR 执行命令、状态和事件 |
| `/api/agents/permission-capabilities` | 查询目标支持的权限能力 |
| A2A | 协议专用响应，仅 Data Plane 和 Standalone 映射 |
| `/openapi/*` | 开发环境的契约入口；生产可用性取决于 Host 配置 |

## 接入流程

1. 自动化使用命名 Bearer Token；浏览器使用 Cookie 发送 POST、PUT、DELETE 等修改请求时，还要按现有客户端流程处理 CSRF 防护，防止其他网站借用登录状态发起操作。
2. 通过管理 API 获取当前用户可访问的资源，保留其稳定标识。
3. 启动执行前查询目标权限能力，按现有执行协议发起命令并订阅事件。
4. 断线重连时恢复会话/执行状态，不把连接断开当作任务完成。

新增接口默认通过 query/body 传标识，遵守仓库规则。具体接口的路由和参数以当前 OpenAPI 与 Contracts 为准。

## 客户端应如何处理结果

管理 JSON API 使用 Bens.Results 统一响应格式。仓库的 `@agw/api` 已提供类型化辅助方法来提取业务数据，调用方应复用这些方法，并分别处理请求失败和业务错误。

执行连接会持续返回事件。客户端需要保留会话与执行标识，展示工具活动和等待输入状态，并在重连后查询实际进度。收到部分文字不代表执行结束，连接断开也不代表执行已经取消。协议消息及顺序见[执行协议说明](https://github.com/zxyao145/agw/blob/main/docs/ws-flow.md)。

## 契约变更

DTO（请求和响应的数据类型）放在所属模块的 `Contracts` 中。预期的业务错误使用 `AgwException` 和稳定的七位 ErrorCode，再由 API 或协议入口转换成响应。WebSocket、OAuth 跳转、A2A 和静态文件使用各自的协议格式。

后端契约更新后，从 `src/clients` 运行 `pnpm gen:api`，再验证调用方。不要手写修改生成的 `openapi.d.ts`，也不要向日志输出实际 Token。

## 实现与参考

- [Execution protocol](https://github.com/zxyao145/agw/blob/main/docs/ws-flow.md)
- [API rules](https://github.com/zxyao145/agw/blob/main/docs/rules.md)
- [API client](https://github.com/zxyao145/agw/tree/main/src/clients/packages/api)
