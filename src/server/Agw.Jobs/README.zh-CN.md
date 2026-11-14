# Jobs

Jobs 提供定时任务的 API、Agent 工具、请求校验和默认名称。每个 Job 对应 Events 中的一个 Timer 来源和一条 Agent 或 Agentflow 订阅。两条记录的 `managedBy` 都是 `job`，`managedKey` 保存同一个 Job ID。Jobs 通过 `IEventJobFacade` 管理这些记录，Events 管理计划、投递、重试和运行历史。

## 创建和更新

`projectId`、`targetKind`、`agentId`、`instructions` 和 `schedule` 用于定义任务。`targetKind` 为 `0` 时目标是 Agent，为 `1` 时目标是 Agentflow。Project 和目标必须属于当前用户。`instructions` 必须包含内容。省略名称或传入空白名称时，Jobs 根据当前用户的任务数量生成 `job-{序号}-{yyyyMMdd}`，日期采用 UTC。名称最多 200 个字符。

```json
{
  "name": "Daily review",
  "enabled": true,
  "projectId": "019a4c17-0000-7000-8000-000000000001",
  "targetKind": 0,
  "agentId": "019a4c17-0000-7000-8000-000000000002",
  "instructions": "Review the open items in the project.",
  "schedule": { "kind": "Cron", "cron": "0 9 * * 1-5" }
}
```

- `Once` 使用 `at`，值为 RFC 3339 时间。到期后执行一次。时间已经过去时，建立计划后立即触发。
- `Interval` 使用 `interval`，值为大于零的 ISO 8601 时长，例如 `PT15M` 或 `P1D`。首次触发时间是建立计划的时间加上这个间隔。
- `Cron` 使用 `cron`，值为标准五段表达式。下一次触发按 UTC 计算。

## API

所有接口返回 Bens.Results 响应。创建、更新、启停和删除返回 Job。

- `GET /api/jobs/list` 列出当前用户的 Job。可传 `projectId` 筛选。
- `GET /api/jobs/get?id=...` 获取一个 Job。
- `POST /api/jobs/create` 接收上面的定义。
- `POST /api/jobs/update` 接收完整定义和 `id`。
- `POST /api/jobs/enable` 接收 `id` 和 `enabled`。
- `POST /api/jobs/delete` 接收 `id`。
- `GET /api/jobs/runs/list?jobId=...&offset=0&limit=100` 读取已结束的尝试。`offset` 从零开始，`limit` 范围是 1 到 500。

`dueAt` 是当前计划的下次触发时间。一次性计划结束或计划停用时，`dueAt` 为空。`scheduleCompleted` 表示当前计划已经完成，Jobs 页面据此显示 Once Job 的 `Ended` 状态。`lastDelivery` 包含最近一次投递的 `id`、`state`、`attempt` 和 `lastError`。运行历史按 `startedAt` 倒序分页。每条记录包含 `deliveryId`、`attempt`、`startedAt`、`endedAt`、`result`、`error` 和 `conversationId`。`result` 的 `0`、`1`、`2` 分别表示成功、等待重试和失败。

## Agent 工具

为 Agent 绑定 `agw-job` skill 后，可在当前 Project 中使用以下工具。

- `agw_job_list` 列出 Job。
- `agw_job_get` 接收 `jobId`。
- `agw_job_create` 接收 `instructions`、`targetKind`、`agentId`、`kind`，以及相应的 `at`、`interval` 或 `cron`。可以提供 `name` 和 `enabled`。
- `agw_job_update` 接收 `jobId` 和需要修改的字段。修改执行目标时同时提供 `targetKind` 和 `agentId`。
- `agw_job_delete` 接收 `jobId` 和用户确认的 `confirmation`。两者必须匹配。

## 执行和管理

Timer 到期时发布 `job.timer.fired` 事件，事件的 `subject` 是 Job ID。Events 投递 worker 使用订阅里的 Project、目标和指令提交执行。执行使用新会话，`kind` 为 `event:timer`，`relateId` 指向触发事件。运行记录通过 `conversationId` 打开对应会话。

修改时间规则或启用 Job 时重新建立计划。只修改名称、Project、执行目标或指令时，原定触发时间保持不变。已经完成的 `Once` Job 停用后再启用，会按原来的 `at` 再触发一次。

每次触发独立产生投递。执行失败后的重试遵循 Events 的退避和最大尝试次数设置。停用 Job 会取消后续计划。删除 Job 会停用并标记 Timer、取消计划和删除订阅。Events 页面会在来源和订阅上显示 `Managed by Job` 和所属 Job 的 ID，在 Jobs 页面修改这些记录。执行和重试规则详见设计文档。[触发事件并执行 Agent](../../../docs/designs/2026-11-12-jobs-event-timer.md#触发事件并执行-agent)
