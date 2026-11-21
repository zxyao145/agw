---
title: "Scheduled jobs"
description: "Schedule Agent or Agentflow runs with Events Timers and inspect delivery attempts."
weight: 70
lastmod: 2026-11-14
translationKey: docs/guides/jobs
---

A Job manages an Events Timer source and an Agent or Agentflow subscription. When the timer fires, Events publishes `job.timer.fired`, creates a delivery, and submits a background execution. Each delivery attempt appears in Job Logs. In split deployments, Control Plane runs Events Timer and delivery workers, while Data Plane runs Distributed execution workers.

Before scheduling a Job, confirm its Project, target, instructions, model, and tools. The Project and target must belong to the current user, and instructions must not be blank. Standalone runs the Events workers and execution in one process.

## Configure a job

1. Create a Job and choose its Project and Agent or Agentflow target. Enter instructions for the unattended run. A blank name is generated automatically; new Jobs are enabled by default.
2. Choose `Once`, `Interval`, or `Cron` and fill in the matching schedule field.
3. After saving, check the next trigger time in **Next Run** and inspect the delivery and attempt history in Job Logs.

| Schedule kind | Example | Time semantics |
| --- | --- | --- |
| `Once` | `2026-11-15T01:00:00Z` | `at` is an RFC 3339 timestamp. If it is in the past, the timer fires once as soon as Events processes the schedule. |
| `Interval` | `PT15M` | `interval` is a positive ISO 8601 duration. The first trigger is 15 minutes after scheduling; each later trigger is based on the time Events processes the previous occurrence. |
| `Cron` | `0 1 * * *` | `cron` is a standard five-field expression evaluated in UTC; this example fires daily at 01:00 UTC. |

All Jobs Timer schedules use UTC. `Interval` accepts a positive ISO 8601 duration such as `PT15M`, not an hours:minutes:seconds value. The Server validates the Timer configuration and rejects invalid schedule fields. Interval triggers do not wait for the previous execution to finish. A `Once` schedule becomes completed after its event fires and `dueAt` becomes `null`; re-enabling a disabled Once Job after its `at` time causes one immediate trigger.

![AGW Desktop: an unsaved Cron Job example. Choose an execution target and confirm the schedule and prompt before creating it.](/images/screenshots/job-create.png)
{caption="AGW Desktop: an unsaved Cron Job example. Choose an execution target and confirm the schedule and prompt before creating it."}

## Write instructions that stand on their own

A scheduled task may run without anyone available to clarify it. Specify the source, time range, and output. For example: “Read this week’s project progress notes. List completed work, unresolved issues, and next steps. Say when records are missing instead of guessing.” Configure the target agent’s reading capability before use.

`0 1 * * *` fires daily at 01:00 UTC, which is 09:00 in Singapore or China Standard Time. After saving, check **Next Run** in the job list or details. Each timer occurrence creates its own Event delivery, independent of prior deliveries and executions.

## Execution, retries, and pausing

Jobs in the same Project can execute concurrently. Each delivery starts or resumes a background execution in the Project with Full access; requests that need a human answer or HumanGate are rejected. When an execution failure is retryable, Events schedules the next attempt using `Events:Delivery:BaseBackoff`, `Events:Delivery:MaxBackoff`, and `Events:Delivery:MaxAttempts`. These settings apply to Event deliveries rather than to an individual Job. After the attempt limit, that delivery becomes `Parked` and can be retried from Events. The Job remains enabled, and future Timer occurrences continue to create deliveries.

Each completed delivery attempt records its time, result, error, and execution turn. Disabling a Job cancels its future Timer schedule, while already-created deliveries continue under Events rules. Deleting a Job cancels deliveries that have not submitted an execution; submitted executions continue to completion.

For a missing run, check that the Job is enabled, `dueAt` is set, and the target is still valid. Timer delivery and execution are separate: a trigger can be recorded even while a delivery is waiting or retrying. Make external writes repeatable because an execution retry can repeat side effects.

## Job Logs: inspect execution results

Job Logs records each execution attempt for a Job. Use it to check success, retries, and failure reasons. It contains execution outcomes; open Chat for full model replies and tool activity.

### Open execution logs

1. Find the task in Jobs and open its row’s logs action to enter **Job Logs**.
2. Locate the relevant record by execution time and inspect its status, attempt number, and error.
3. Select **Go to Chat** to open the conversation for that run and inspect its conversation and execution content. The action is disabled when the attempt has no conversation.

The task details view also exposes attempts and errors under **Execution Logs**. **Back to Jobs** returns to the task list.

### Read the fields

| Field | Meaning |
| --- | --- |
| Result | Attempt result: `Succeeded`, `Retry`, or `Failed`; the API returns `0`, `1`, or `2` respectively |
| Attempt | Attempt number within one delivery: `#1` is its initial attempt and `#2` is its first retry |
| Delivery ID | Identifier for the Event delivery that owns this attempt |
| Time | Attempt start and end time, displayed in the client’s local time |
| Error | Failure reason, or `-` when no error is provided |
| Actions | **Go to Chat** opens the run’s conversation when it has a `conversationId` |

For example, `Retry / #1` followed by `Succeeded / #2` for the same Delivery ID means Events retried that delivery and its second attempt completed. A later Timer occurrence creates a different delivery and starts its attempt count at `#1`.

### Troubleshoot with logs

- **No records**: check whether the Job is enabled and whether its Timer has fired. The Job Runs API lists completed attempts; the current attempt is omitted while its delivery is `Running` or `WaitingExecution`.
- **Failed execution**: read Error, then use Go to Chat to inspect model replies and tool activity. Consult Server logs for model connection failures, tool errors, or unavailable workspaces.
- **Success but an unexpected result**: Succeeded means execution completed successfully. Still verify the output, generated files, or external actions against the task requirements.
- **Failures remain after recovery**: a successful retry does not remove earlier failure records. Check later attempts by time and inspect the Job’s current state.

## Scheduled and background execution

Jobs schedule Agent or Agentflow work through Events Timers. Background Agents can delegate subtasks to other agents and retrieve their results later.

1. Verify that the Project, target, and required tools work.
2. Create a Job with instructions and a schedule; configure Background Agents for delegated subtasks.
3. Inspect status, results, and errors in task records and adjust the schedule as needed.

Closing Chat does not automatically cancel execution, but the Server and its execution workers must keep running. Background Agents cannot wait for new human approval. Unattended Jobs cannot automatically complete requests that need a real human answer or HumanGate decision.

## Jobs API

Jobs endpoints return Bens.Results envelopes. Schedule fields are the Events Timer fields: `kind` is `Once`, `Interval`, or `Cron`; `at` is an RFC 3339 timestamp; `interval` is a positive ISO 8601 duration; and `cron` is a five-field UTC expression.

| Method and path | Purpose |
| --- | --- |
| `GET /api/jobs/list?projectId=` | List Jobs, optionally filtered by Project |
| `GET /api/jobs/get?id=` | Get one Job |
| `POST /api/jobs/create` | Create a Job |
| `POST /api/jobs/update` | Update a Job; the request includes `id` |
| `POST /api/jobs/enable` | Enable or disable a Job with `{ "id", "enabled" }` |
| `POST /api/jobs/delete` | Delete a Job with `{ "id" }` |
| `GET /api/jobs/runs/list?jobId=&offset=0&limit=100` | List completed delivery attempts; `limit` is 1–500 |

Create and update requests include `name`, `enabled`, `projectId`, `targetKind`, `agentId`, `instructions`, and `schedule`. `targetKind` is `0` for Agent or `1` for Agentflow. For example:

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

The Job response includes `dueAt`, `scheduleCompleted`, and `lastDelivery`. `dueAt` is `null` when the Timer schedule is inactive. `scheduleCompleted` identifies a completed schedule, which appears as **Ended** for Once Jobs. `lastDelivery.state` uses the Events delivery state values. Each run response includes `id`, `deliveryId`, `attempt`, `startedAt`, `endedAt`, `result`, `error`, and `conversationId`.

[Configure tools and Skills]({{< relref "/docs/guides/tools-skills" >}}) · [Learn about memory]({{< relref "/docs/features/memory" >}}) · [Check execution status]({{< relref "/docs/guides/chat" >}})

## Implementation and references

- [Job facade](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Events/Application/Jobs/EventJobFacade.cs)
- [Timer schedule synchronization](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Events/Application/Sources/EventTimerScheduleSync.cs)
- [Delivery attempt results](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Data/Entities/Events/EventEnums.cs)
- [Job Logs UI](https://github.com/zxyao145/agw/blob/main/src/clients/packages/jobs/src/ui-web/pages/jobs/logs/page.tsx)
