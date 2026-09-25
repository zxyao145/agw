---
title: "日志与常见问题"
description: "沿连接、认证、模型、文件与执行状态排查问题。"
weight: 50
lastmod: 2026-09-25
translationKey: docs/operations/troubleshooting
---

排查时先确定故障发生在哪一步：页面连接、登录、模型调用，还是文件和工具操作。用一个简单任务重现问题，通常比反复重启更容易找到原因。

记录出错的 Server、Project、会话和时间，再查看对应服务日志。分离部署的管理问题主要查控制面日志，任务执行问题主要查数据面日志。

## 排查顺序

| 现象 | 优先检查 |
| --- | --- |
| 无法打开界面 | 监听地址、端口、容器映射、代理目标 |
| Setup 无法完成 | 数据库连接、目录写权限、远程 Setup Code |
| 登录或 API Key 失败 | 实际 Server、API Key 撤销情况、数据库认证状态 |
| 第三方登录失败 | `Auth:Oidc:PublicBaseUrl`、提供商侧登记的回调地址、客户端凭据、Server 到提供商的网络 |
| Agent 不回复 | Model Provider、模型 ID、凭据、待审批/待输入状态 |
| CLI 无法启动 | 执行节点的可执行文件、账号与环境 |
| 文件找不到 | 当前目录选择、Server 路径、挂载和访问权限 |
| Job 没运行 | 启用状态、未来时间、UTC Cron、有效目标和日志 |
| 断线后状态不对 | 会话选择、WebSocket 代理、执行是否仍在后台运行；InProcess 模式下 Server 重启后，原来运行中的会话会显示为 Interrupted，不会继续执行 |

## 示例：页面能打开，但 Agent 不回复

1. 查看 Chat 是否正在等待审批或补充信息。如果是，先处理请求。
2. 使用同一个模型连接运行一条纯文字问题。如果仍失败，检查 API 地址、模型 ID 和凭据。
3. 纯文字正常而工具任务失败时，检查工具绑定、工作目录和执行主机的访问权限。
4. 分离部署中，若配置页面正常而对话连接失败，检查 `/api/hubs/exec` 是否转发到数据面，以及代理是否支持 WebSocket。
5. 根据出错时间查找服务日志中的具体错误，修改后重复同一个小任务验证。

这个顺序可以把模型、工具和连接问题分开，避免同时更改多项设置后无法判断原因。

## 第三方登录失败

第三方登录失败时，浏览器回到登录页，地址中带有 `error=oidc-<类别>`，Desktop 在 Server 配置处显示提示。用这个类别配合 `Agw.Auth.Oidc` 日志定位原因：日志记录提供商、客户端、失败所处的阶段、失败类别和 TraceId。

| 类别 | 通常的原因 |
| --- | --- |
| `provider-unavailable`、`provider-timeout` | Server 访问不到提供商：网络、出站代理或防火墙 |
| `provider-rejected` | 能访问提供商，但 OAuth2 的用户信息接口返回错误状态码：检查 `UserInfoEndpoint`、`Scopes` 和令牌权限 |
| `protocol-rejected` | OIDC 提供商返回协议错误：客户端编号、密钥或已登记的回调地址不符 |
| `invalid-state`、`invalid-nonce` | 回调校验未通过：浏览器访问的地址与 `PublicBaseUrl` 不一致，或校验用的 Cookie 被拦截 |
| `invalid-token` | 令牌校验未通过：Authority、签发者、接收方或验签地址配置不符 |
| `protocol-validation-failed` | 其他未归类的协议失败，包括 OAuth2 换取令牌被拒绝：先检查客户端编号、密钥和回调地址 |
| `provisioning-failed`、`grant-creation-failed`、`session-creation-failed` | Server 本地处理失败：先检查数据库连接和迁移是否完成 |
| `authorization-denied` | 用户在提供商页面取消了授权 |

排查时不要关闭签发者、接收方、签名、state、nonce 或 PKCE 校验。反向代理需要保留原始协议和主机名，否则回调会落在另一个来源上。报告问题时不要附带认证相关的查询参数和完整的提供商响应。

## 日志与遥测

`AgwLogDir` 默认 `./logs`，不随 `AgwDataDir` 自动变化。分离部署要查看对应角色日志。需要集中遥测时配置 `OpenTelemetry:OtlpEndpoint`；空值或缺失会回退到 `http://localhost:4317`，不代表禁用导出。

历史采用 Interval 批量写入。Host 模板的 `ConversationHistory:FlushIntervalSeconds` 为 10 秒，省略时回退到 5 秒。即时输出与已落库历史存在时间差。

## Web 开发代理

Web 开发运行在 `3001`，后端默认 `30816`。代理目标依次取 `BACKEND_API_BASE_URL`、`NEXT_PUBLIC_API_BASE_URL`、默认本机地址。静态 export 模式不使用 Next.js 代理，应由 Server 或外部入口提供同源路由。

修复后重复原来失败的小任务，确认界面状态和日志都恢复。报告问题时提供版本、部署方式、复现步骤和脱敏错误，不附带真实 API Key 或完整 OAuth 响应。

## 实现与参考

- [Runtime consistency](https://github.com/zxyao145/agw/blob/main/docs/operations/backend-runtime-consistency.md)
- [Host settings](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Host/appsettings.json)
- [Deployment](https://github.com/zxyao145/agw/blob/main/docs/4.Deployment.md)
