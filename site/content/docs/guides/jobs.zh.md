---
title: "Jobs 定时任务"
description: "通过 Events Timer 安排 Agent 或 Agentflow，并查看投递尝试。"
weight: 70
lastmod: 2026-11-14
translationKey: docs/guides/jobs
---

Job 管理一个 Events Timer 来源和一条 Agent 或 Agentflow 订阅。Timer 到期后，Events 发布 `job.timer.fired`，创建投递并提交后台执行。每次投递尝试都会显示在 Job Logs 中。分离部署时，Control Plane 运行 Events Timer 和投递 Worker，Data Plane 运行 Distributed 执行 Worker。

创建 Job 前，先确认 Project、执行目标、指令、模型和工具均可使用。Project 和执行目标必须属于当前用户，指令不能为空。Standalone 会在同一进程中运行 Events workers 和执行服务。

## 配置步骤

1. 在 Jobs 创建任务，并选择 Project 和 Agent 或 Agentflow 执行目标。填写无人值守运行所需的指令。名称可以留空，系统会自动生成；新 Job 默认启用。
2. 选择 `Once`、`Interval` 或 `Cron`，填写对应的计划字段。
3. 保存后，在 **Next Run** 核对下一次触发时间，在 Job Logs 查看投递和尝试记录。

| 计划类型 | 示例 | 时间语义 |
| --- | --- | --- |
| `Once` | `2026-11-15T01:00:00Z` | `at` 使用 RFC 3339 时间。时间已过时，Events 处理计划后会立即触发一次。 |
| `Interval` | `PT15M` | `interval` 使用大于零的 ISO 8601 时长。首次在建立计划 15 分钟后触发；后续触发时间按 Events 处理上一次触发的时间计算。 |
| `Cron` | `0 1 * * *` | `cron` 使用 UTC 计算的标准五段表达式；此例每天 UTC 01:00 触发。 |

所有 Jobs Timer 计划都使用 UTC。`Interval` 接受 `PT15M` 等大于零的 ISO 8601 时长，不接受“小时:分钟:秒”值。Server 按 Events Timer 配置规则校验计划字段，无效值会被拒绝。Interval 触发不等待上一次执行结束。`Once` 计划发布事件后会变为已完成，`dueAt` 显示为空；已停用的 Once Job 在时间已过后重新启用，会立即触发一次。

![AGW Desktop：Cron Job 的未保存示例。创建前需选择执行目标，并确认计划与提示词。](/images/screenshots/job-create.png)
{caption="AGW Desktop：Cron Job 的未保存示例。创建前需选择执行目标，并确认计划与提示词。"}

## 写一条能独立执行的任务要求

定时任务开始时未必有人补充说明，提示词应交代资料位置、时间范围和输出要求。例如：“读取项目中的本周进展记录，列出已完成事项、未解决问题和下一步建议；缺少记录时明确说明，不要推测。”使用前要为目标 Agent 配好读取能力。

`0 1 * * *` 表示每天 UTC 01:00，对新加坡或中国标准时间是当天 09:00。保存后在任务列表或详情的 **Next Run** 中核对计划。每次 Timer 触发都会创建自己的 Event 投递，不依赖前一次投递或执行是否完成。

## 执行、重试与暂停

同一 Project 中的 Jobs 可以同时执行。每次投递都会在 Project 中开始或恢复后台执行，并使用 Full access；需要人工回答或 HumanGate 的请求会被拒绝。执行失败且可以重试时，Events 使用 `Events:Delivery:BaseBackoff`、`Events:Delivery:MaxBackoff` 和 `Events:Delivery:MaxAttempts` 安排下一次尝试。这些设置作用于 Event 投递，不属于单个 Job。达到尝试上限后，该投递进入 `Parked`，可以在 Events 中手动重试。Job 仍保持启用，后续 Timer 触发仍会创建投递。

每次完成的投递尝试都会记录时间、结果、错误和执行 turn。停用 Job 会取消后续 Timer 计划，已创建的投递仍按 Events 规则处理。删除 Job 会取消尚未提交执行的投递；已提交的执行会继续到结束。

没有记录时，检查 Job 是否启用、`dueAt` 是否有值、执行目标是否有效。Timer 触发与执行是两个阶段：即使投递还在等待或重试，触发记录也可能已经生成。外部写入应支持重复执行，因为执行重试可能再次产生副作用。

## Job Logs：查看每次执行结果

Job Logs 记录一个 Job 的每次执行尝试，用来确认任务是否运行成功、是否发生重试，以及失败原因。它记录执行结果；模型的完整回复和工具活动需要进入 Chat 查看。

### 打开执行日志

1. 在 Jobs 列表找到任务，点击该行的日志入口，进入 **Job Logs**。
2. 根据执行时间找到要检查的记录，查看状态、尝试次数和错误信息。
3. 点击该行的 **Go to Chat** 打开这次执行对应的会话，查看对话和执行内容；尝试没有关联会话时，这个入口会停用。

任务详情中的 **Execution Logs** 也可以查看尝试记录和错误，**Back to Jobs** 则返回任务列表。

### 如何理解字段

| 字段 | 含义 |
| --- | --- |
| Result | 投递尝试结果：`Succeeded`、`Retry` 或 `Failed`；API 分别返回 `0`、`1` 或 `2` |
| Attempt | 一个投递内的尝试序号：`#1` 是首次尝试，`#2` 是第一次重试 |
| Delivery ID | 这次尝试所属的 Event 投递标识 |
| Time | 尝试开始和结束时间；按客户端本地时间显示 |
| Error | 失败原因；没有错误信息时显示 `-` |
| Actions | 有 `conversationId` 时，**Go to Chat** 会打开这次执行的会话 |

例如，同一个 Delivery ID 先出现 `Retry / #1`，随后出现 `Succeeded / #2`，表示 Events 重试了这次投递，第二次尝试成功结束。后续 Timer 触发会创建新的投递，尝试序号从 `#1` 开始。

### 用日志排查问题

- **没有记录**：检查 Job 是否启用、Timer 是否已触发。Job Runs API 返回已经结束的尝试；投递处于 `Running` 或 `WaitingExecution` 时，当前尝试会暂时隐藏。
- **执行失败**：先看 Error，再通过 Go to Chat 检查模型回复和工具活动。连接不到模型、工具执行失败或工作目录不可用时，结合 Server 日志定位原因。
- **日志成功但结果不符合预期**：Succeeded 表示执行成功结束，仍需检查实际输出、生成文件或外部操作是否符合任务要求。
- **有失败记录但任务已恢复正常**：保留的失败记录不会因后续重试成功而消失，按时间检查后续尝试和任务当前状态。

## 定时与后台执行

Jobs 通过 Events Timer 计划运行 Agent 或 Agentflow。后台 Agent 能力用于把子任务交给其他 Agent，并在后续获取结果。

1. 先确认 Project、执行目标和所需工具可以正常工作。
2. 创建 Job，填写任务要求和计划；需要委派子任务时配置 Background Agents 能力。
3. 在任务记录中查看状态、结果与错误，按实际需要调整计划。

关闭 Chat 页面不会自动取消执行；Server 和执行 Worker 必须保持运行。后台 Agent 不能等待新的人工审批；无人值守 Job 遇到必须由人回答的问题或 HumanGate 时也无法自动完成。

## Jobs API

Jobs 接口返回 Bens.Results envelope。计划字段沿用 Events Timer：`kind` 为 `Once`、`Interval` 或 `Cron`；`at` 是 RFC 3339 时间；`interval` 是大于零的 ISO 8601 时长；`cron` 是按 UTC 计算的五段表达式。

| 方法与路径 | 用途 |
| --- | --- |
| `GET /api/jobs/list?projectId=` | Job 列表，可按 Project 筛选 |
| `GET /api/jobs/get?id=` | 查询一个 Job |
| `POST /api/jobs/create` | 创建 Job |
| `POST /api/jobs/update` | 修改 Job，请求带 `id` |
| `POST /api/jobs/enable` | 按 `{ "id", "enabled" }` 启用或停用 Job |
| `POST /api/jobs/delete` | 按 `{ "id" }` 删除 Job |
| `GET /api/jobs/runs/list?jobId=&offset=0&limit=100` | 查询已结束的投递尝试；`limit` 为 1–500 |

创建和修改请求包含 `name`、`enabled`、`projectId`、`targetKind`、`agentId`、`instructions` 和 `schedule`。`targetKind` 为 `0` 时表示 Agent，为 `1` 时表示 Agentflow。创建请求示例：

```json
{
  "name": "daily-summary",
  "enabled": true,
  "projectId": "11111111-1111-4111-8111-000000000001",
  "targetKind": 0,
  "agentId": "22222222-2222-4222-8222-000000000001",
  "instructions": "Summarize the latest project progress.",
  "schedule": { "kind": "Interval", "interval": "PT15M" }
}
```

Job 响应包含 `dueAt`、`scheduleCompleted` 和 `lastDelivery`。Timer 计划未处于活动状态时，`dueAt` 为空。`scheduleCompleted` 表示计划已完成，Once Job 的 **Next Run** 此时显示 **Ended**。`lastDelivery.state` 使用 Events 投递状态值。每条运行记录包含 `id`、`deliveryId`、`attempt`、`startedAt`、`endedAt`、`result`、`error` 和 `conversationId`。

[配置工具与 Skills]({{< relref "/docs/guides/tools-skills" >}}) · [了解记忆能力]({{< relref "/docs/features/memory" >}}) · [查看执行状态]({{< relref "/docs/guides/chat" >}})

## 实现与参考

- [Job facade](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Events/Application/Jobs/EventJobFacade.cs)
- [Timer schedule synchronization](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Events/Application/Sources/EventTimerScheduleSync.cs)
- [Delivery attempt results](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Data/Entities/Events/EventEnums.cs)
- [Job Logs UI](https://github.com/zxyao145/agw/blob/main/src/clients/packages/jobs/src/ui-web/pages/jobs/logs/page.tsx)
