# Agentflow Trace Cleanup and Observability Design

## Goal

Ensure persisted agentflow node inputs follow project-context deletion semantics, and keep node execution activities connected to exported OpenTelemetry traces without relying on workflow executor agent-name tagging.

## Scope

- Delete `AgentflowNodeExecutionTrace` rows when clearing or deleting one project context.
- Delete all project trace rows when deleting all contexts for a project or deleting the project itself.
- Remove `ObservabilityMiddleware.TagCurrentWorkflowExecutor` and its call sites.
- Rename the persistence activity source so it matches the host's existing `Agw.*` OpenTelemetry source registration.
- Add regression tests for every changed behavior.

The bounded trace queue and frontend/OpenAPI contract are outside this change.

## Trace Cleanup Design

`ProjectContextAppService` will receive `IRepository<AgentflowTrace>` through its explicit constructor. Its existing clear and delete operations will use server-side `ExecuteDeleteAsync` queries:

- `ClearRecordsAsync` and `DeleteAsync`: filter by both `ProjectId` and `ContextId`.
- `DeleteAllAsync`: filter by `ProjectId`.

`ProjectAppService` will receive the same repository and delete all trace rows for the project before removing the project.

This follows the existing application-service and repository style without adding a new abstraction or migration. Cleanup applies to traces persisted when the operation runs. A still-running execution may create new post-cleanup data, consistent with existing task-record behavior.

## Activity Design

The node execution activity remains the timing and completion signal consumed by `AgentflowNodeExecutionTraceCollector`.

Its source name changes from `Agentflow.Execution.Persistence` to `Agw.Agentflow.Execution.Persistence`. The host already registers `Agw.*`, so the intermediate node activity will be exported and child spans will no longer reference an omitted parent span.

`ObservabilityMiddleware` will no longer inspect `Activity.Current`. `TagCurrentWorkflowExecutor` and both calls to it will be removed. Workflow spans therefore no longer receive the custom `gen_ai.agent.name` tag from this middleware.

## Tests

- Extend project-context application-service tests to seed traces and verify cleanup for clear, single-context delete, and project-wide context delete.
- Extend project application-service tests to verify project deletion removes its traces while preserving traces belonging to other projects.
- Update workflow telemetry tests to remove the obsolete agent-name assertion.
- Add an activity test that runs a traced workflow and verifies the persistence activity exists under a workflow activity with the `Agw.Agentflow.Execution.Persistence` source name.
- Run the affected Agents, Tasks, and Host test projects plus `git diff --check`.

## Non-Goals

- No EF migration or schema relationship changes.
- No queue durability or backpressure changes.
- No OpenAPI or frontend changes.
- No Git commit unless explicitly requested.
