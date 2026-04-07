# Chat Task Generalization Design

Date: 2026-04-07

## Summary

This design generalizes `/chat` so it is no longer a Claude Code-specific page and no longer depends on `ProjectTask.AgentType`, `ProjectTask.AgentId`, or `ProjectTask.Description`.

After this change:

- `ProjectTask` becomes a task/session container, not a target-binding record.
- `Job` remains the only scheduled execution template and continues to own `AgentType`, `AgentId`, and `Prompt`.
- `/chat` executes against the currently selected target directly and records per-turn target metadata in `TaskRecord.Metadata`.
- A single chat task may contain turns executed against different targets.
- Manual project-task creation and project-task scheduling are removed.

## Goals

- Make `/chat` generic for both `Agent` and `Agentflow`.
- Remove Claude-specific naming and assumptions from `src/frontend/web/src/app/(app)/(interface)/chat`.
- Remove task-level target binding from `ProjectTask`.
- Remove `ProjectTask.Description` and use `Title` as the only task summary field.
- Preserve job execution history by linking job-created tasks back to their source job.
- Allow the same chat task to continue after target switches without changing `taskId`.

## Non-Goals

- Reworking the agent and agentflow runtime APIs.
- Introducing a new execution entity such as `TaskRun` or `TaskExecution`.
- Preserving manual `ProjectTask` creation/edit/reorder/cancel flows.
- Backfilling old tasks to infer originating jobs.

## Current Problems

`/chat` currently inherits Claude-specific concepts, naming, and defaults from the copied `claude-code` page. It also assumes a task is bound to exactly one execution target through `ProjectTask.AgentType` and `ProjectTask.AgentId`.

That model causes two problems:

1. It prevents `/chat` from behaving as a generic workspace chat where the user can switch between agents and agentflows.
2. It conflates a task/session container with execution-target ownership, even though interactive execution already routes based on the current request instead of strictly enforcing the task-stored target.

`ProjectTask.Description` also duplicates summary text that can be represented more cleanly by `Title` plus the first user message in task history.

## Design Decisions

### 1. `ProjectTask` becomes a pure task/session container

Remove these fields from `ProjectTask`:

- `AgentType`
- `AgentId`
- `Description`

Add this field:

- `JobId?`

Retained fields:

- `Id`
- `ProjectId`
- `ContextId`
- `Title`
- `Status`
- `ErrorMessage`
- `FinishedTime`
- standard audit fields

New meaning of `ProjectTask`:

- It identifies a task/session and stores task-level status.
- It does not define which target must execute the task.
- It may optionally point to a source `Job`.

### 2. `Job` remains the only scheduled execution template

`Job` continues to own:

- `AgentType`
- `AgentId`
- `Prompt`

When a job runs, the system creates a `ProjectTask` history record with:

- `JobId = job.Id`
- `Title = job.Name`, or a prompt-derived fallback if the job name is empty

The actual execution still routes by `Job.AgentType` and `Job.AgentId`.

### 3. `/chat` executes directly against the selected target

`/chat` keeps two selectors in the top bar:

- `Project Select`
- `Target Select`

`Target Select` is a single select with two groups:

- `Agent`
- `Agentflow`

`/chat` uses the selected target directly when sending to `/api/executions/{targetId}/ws`.

Target switching rules:

- Switching target disconnects the active websocket.
- Current messages remain visible.
- Current `taskId` remains unchanged.
- The next send uses the newly selected target.

This makes a task a conversation container rather than a single-target execution binding.

### 4. Per-turn target metadata moves to `TaskRecord.Metadata`

Each `/chat` send stores the selected target in the created record metadata.

Recommended metadata keys:

- `targetType`: `agent` or `agentflow`
- `targetId`: target GUID string
- `targetName`: nullable display name

This preserves execution provenance for each turn even when the same task mixes targets.

### 5. `Title` is the only task summary field

Delete `ProjectTask.Description`.

Summary strategy:

- Job-created tasks use `Job.Name`, or a prompt-derived fallback.
- Chat-created tasks use a truncated version of the first user input.

Task detail pages and project task lists display `Title` only.

## Backend Design

### Entity and Contract Changes

Update `ProjectTask`:

- remove `AgentType`
- remove `AgentId`
- remove `Description`
- add nullable `JobId`

Update contracts:

- `ProjectTaskCreateRequest`
- `ProjectTaskUpdateRequest`
- `ProjectTaskSummaryResponse`
- `ProjectTaskResponse`

Remove target and description fields from these contracts and add `JobId?` to responses.

### Task Creation Paths

#### Job execution path

`AgentExecutor` still creates a project task before executing, but no longer writes target fields into the task.

Instead it writes:

- `JobId`
- `Title`
- first user record payload

Execution target selection still comes entirely from the `Job`.

#### Chat execution path

Interactive execution still creates or reuses a `ProjectTask`, but task creation no longer receives target-binding fields.

When a task does not exist yet:

- create a chat task with `JobId = null`
- derive `Title` from the first user input

When a task already exists:

- reuse the same task if it belongs to the selected project
- do not validate the selected target against stored task fields, because target ownership is no longer task-level state

### Hosted Services

Remove or disable `ProjectTaskSchedulerHostedService`.

Reason:

- after this design, `ProjectTask` is no longer a pending execution unit
- only `Job` remains schedulable
- keeping the project-task scheduler would leave an execution path with no target source

`JobHostedService` remains in place as the only scheduled execution loop.

## Frontend Design

### `/chat`

Refactor `src/frontend/web/src/app/(app)/(interface)/chat` into a generic chat page.

Changes:

- remove Claude-specific names, strings, and constants
- remove dependence on Claude-specific info labels such as `claudeCodeVersion`
- replace task-level target assumptions with explicit page state

Page state includes:

- selected project
- selected target type
- selected target id
- current task id

History behavior:

- task history remains filtered only by project
- selecting a task loads its messages
- selecting a task does not override the current target selector state

### Project Task Pages

`/projects/{id}` becomes a task history view, not a task execution management surface.

Remove:

- manual task creation
- task editing
- reorder pending task
- cancel task as an execution queue operation

List items show:

- title
- status
- source marker derived from `jobId`
- timestamps

`/projects/{id}/tasks/{taskId}` becomes a read-only history page.

Remove task-level target-derived links because target ownership is no longer stored on the task.

If the product needs "continue chatting", route the user back to `/chat` with `projectId` and `taskId`.

## Data Flow

### Chat send

1. User selects project and target.
2. User sends input in `/chat`.
3. Frontend reuses the current `taskId` or creates a new one.
4. Frontend calls `/api/executions/{targetId}/ws` with the current `AgentRuntimeType`.
5. Backend resolves or creates the `ProjectTask`.
6. Backend appends task records for the interaction.
7. The turn's selected target metadata is written into `TaskRecord.Metadata`.

### Job execution

1. `JobHostedService` dequeues a due job.
2. `AgentExecutor` creates a `ProjectTask` with `JobId`.
3. Runtime execution uses `Job.AgentType` and `Job.AgentId`.
4. Result messages are written into task records under that task.

## Error Handling

- If `/chat` has no selected target, the send action is blocked client-side with a clear error.
- If the selected target no longer exists or is disabled, execution fails for that turn only. The task remains valid because target ownership is not task-level state.
- If a job references a missing or disabled target, the run fails and the created task is marked failed with the runtime error.
- If a task belongs to another project, reuse is rejected just as it is today.

## Migration

Add a database migration that:

- drops `project_tasks.agent_type`
- drops `project_tasks.agent_id`
- drops `project_tasks.description`
- adds nullable `project_tasks.job_id`

Historical data handling:

- existing tasks keep their history
- `job_id` is left null unless there is a safe and explicit migration path
- no inferred backfill from old target fields

## Testing Strategy

### Backend

- task creation for chat creates a task without target fields and with a generated title
- job execution creates a task with `jobId`
- job execution still routes correctly using `Job.AgentType` and `Job.AgentId`
- interactive execution can reuse the same task across target changes
- task record metadata stores target details for each turn
- project-task scheduler removal leaves no broken DI or startup paths

### Frontend

- `/chat` loads projects and grouped targets
- switching targets disconnects the websocket but keeps messages visible
- subsequent sends continue under the same task with the newly selected target
- task history remains project-scoped
- project task list/detail pages render without task-level target or description fields

## Risks

- Removing task-level target fields changes both API contracts and task-related UI in multiple places.
- If task record metadata is not surfaced in the right places, users may lose visibility into which target produced which turn.
- Removing the project-task scheduler requires checking for any hidden operational reliance on pending project tasks.

## Implementation Notes

- Keep the execution change focused. Do not introduce a new execution entity in this iteration.
- Prefer preserving old task history rather than attempting clever migration backfills.
- Treat `ProjectTask` as conversation history and status, not as a source of execution truth.
