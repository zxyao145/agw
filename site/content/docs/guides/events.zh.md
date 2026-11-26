---
title: "事件中心与通知"
description: "通过 Timer、Webhook 和 HTTP 轮询接收 CloudEvents，配置订阅转发并处理通知。"
weight: 75
lastmod: 2026-11-26
translationKey: docs/guides/events
---

**Events（事件中心）**是 Jobs 与外部触发能力的事件总线。它接收来自不同来源的 CloudEvents，按用户自己的订阅规则匹配后派发给 Agent、Agentflow 或扩展处理器，并记录每一次投递尝试。**Notifications（通知中心）**收集运行过程或扩展发出的通知与交互请求，并实时推送到 Web、Desktop 以及已启用的通知渠道。

在 Web 左侧导航的 **Activity** 分组下，或 Desktop 的 Ribbon 中打开 **Events** 与 **Notifications**。

## 事件中心

Events 页面包含四个标签页：

| 标签页 | 管理内容 |
| --- | --- |
| **Events** | 已接收的 CloudEvents，支持按 `Type`、`Subject` 和 `ChainId` 筛选，查看载荷和关联投递 |
| **Sources** | 事件来源，包括 `Timer`、`Webhook`、`HTTP poll`、`Extension provider` 与 `Publisher` |
| **Subscriptions** | 订阅过滤规则与转发目标（Project、Agent、Agentflow 或扩展处理器） |
| **Deliveries** | 投递记录与状态（`Pending`、`Running`、`Waiting for execution`、`Succeeded`、`Retry scheduled`、`Held`、`Parked`、`Cancelled`），支持 **Retry** 与 **Cancel** |

### 配置来源与订阅

1. 在 **Sources** 中创建 `Timer`、`Webhook` 或 `HTTP poll` 来源。创建 `Webhook` 来源后，妥善保存界面展示的接入地址与密钥。由 Jobs 创建的 `Timer` 来源或扩展托管的记录会显示托管标记，请在对应页面或扩展中维护。
2. 在 **Subscriptions** 中创建订阅，按需设置事件类型、主题或来源筛选条件，并选择目标 Project 与 Agent、Agentflow 或扩展处理器。
3. 发送一条测试事件，在 **Events** 中确认事件已被接收，再在 **Deliveries** 中查看投递结果。

可重试的投递达到 `Events:Delivery:MaxAttempts` 上限后会进入 `Parked` 状态。修复目标或配置问题后，可以在 **Deliveries** 中点击 **Retry** 重新投递。分离部署时，必须把 `Events:Transport:Provider` 配置为 `Redis`，让 Control Plane 与 Data Plane 共享事件流。

## 通知中心

未读通知数量会显示在侧边栏导航的 **Notifications** 旁，并通过 SSE 实时同步（`Notifications:Stream:PollInterval`）。Desktop 还会针对当前连接的 Server 与用户弹出系统通知。

- 查看通知正文、标记已读或跳转来源上下文。
- 当任务或扩展（例如 TaskPilot）请求人工决定或回复时，直接在通知卡片中提交答复。
- 需要把通知发送到外部消息渠道（例如飞书）时，安装并启用对应的 [Server 扩展]({{< relref "/docs/operations/extensions" >}})。

## 实现与参考

- [Events module](https://github.com/zxyao145/agw/tree/main/src/server/Agw.Events)
- [Notifications module](https://github.com/zxyao145/agw/tree/main/src/server/Agw.Notifications)
- [Events UI](https://github.com/zxyao145/agw/blob/main/src/clients/packages/events/src/ui-web/pages/events-page.tsx)
- [Notification Center UI](https://github.com/zxyao145/agw/blob/main/src/clients/packages/notifications/src/notification-center.tsx)
