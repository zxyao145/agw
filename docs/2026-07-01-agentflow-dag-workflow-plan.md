# DAG-first Agentflow Workflow 编排计划

## Summary

- 重建 Agentflow 为 DAG-first 模型：UI 保存完整 DAG，后端只根据 DAG 编译 `Microsoft.Agents.AI.Workflows.Workflow`。
- 不考虑兼容：移除以 `AgentflowOrchestrationPattern` 为核心的执行模型，`Pattern` / `ConfigurationJson` 不再参与运行语义。
- v1 支持节点：`Agent`、`WorkflowAsAgent`、`PromptAdapter`、`HumanGate`、`CheckpointMarker`。
- C# 能力依据：
  - https://learn.microsoft.com/en-us/agent-framework/workflows/workflows
  - https://learn.microsoft.com/en-us/agent-framework/workflows/executors
  - https://learn.microsoft.com/en-us/agent-framework/workflows/edges
  - https://learn.microsoft.com/en-us/agent-framework/workflows/human-in-the-loop
  - https://learn.microsoft.com/en-us/agent-framework/workflows/checkpoints
  - https://learn.microsoft.com/en-us/agent-framework/workflows/as-agents

## Key Changes

- 数据模型改为：
  - `Agentflow`: `Id`, `Name`, `Description`, `SystemPrompt`, `Enable`
  - `AgentflowNode`: `NodeId`, `Kind`, `RelateId`, `Name`, `PositionJson`, `Instructions`, `ConfigJson`
  - `AgentflowEdge`: `EdgeId`, `SourceNodeId`, `TargetNodeId`, `Kind`, `Label`, `ConditionJson`, `ConfigJson`
- 节点语义：
  - `Agent`: 引用已有 Agent，运行时创建临时 `AIAgent`。
  - `WorkflowAsAgent`: 引用另一个 Agentflow，编译后 `.AsAIAgent()`。
  - `PromptAdapter`: 把上游输出按节点 `Instructions` 重写为下游输入。
  - `HumanGate`: 编译为发出 `RequestInfoEvent` 的 executor，等待用户输入或审批。
  - `CheckpointMarker`: 形成显式 checkpoint 边界，并把 checkpoint 元数据暴露给 AGW。
- 边语义：
  - v1 实现 `Direct`、`Conditional`、`FanInBarrier`。
  - 条件不允许写 C#，只支持受控 JSON DSL，例如字段匹配、布尔判断、枚举值匹配。
  - 默认禁止环；DAG 校验失败则拒绝保存。
- 后端编译器：
  - 新增 `AgentflowWorkflowCompiler`，输入持久化 DAG，输出 `Workflow`。
  - 校验 start/end、无环、节点引用存在、嵌套 workflow 无循环、边条件合法、fan-in group 合法。
  - 统一用 `WorkflowBuilder` 构建图，而不是 `AgentWorkflowBuilder.BuildSequential/Concurrent/...`。
- 执行：
  - streaming 默认用 `InProcessExecution.OffThread.RunStreamingAsync`。
  - 测试使用 `InProcessExecution.Lockstep`，保证事件顺序稳定。
  - 监听 `AgentResponseUpdateEvent`、`WorkflowOutputEvent`、`RequestInfoEvent`、`SuperStepCompletedEvent`。
  - HITL pending 时保存 request payload、request id、run/session id、最近 checkpoint。
  - 用户提交 HITL response 后从 checkpoint/resume 继续执行。
- 前端：
  - React Flow 节点侧栏支持编辑：节点类型、instructions、checkpoint、human request schema、边条件。
  - 删除 pattern 选择器，改成 DAG 校验状态和运行预览。
  - 节点上直接显示 `Agent` / `Workflow` / `Human` / `Checkpoint` 类型徽标。
  - 提供 Mermaid/graph preview，展示后端实际编译结果。

## Public API / Types

- 删除 create/update 请求中的 `Pattern` 和 `ConfigurationJson`。
- `AgentflowNodeRequest` 改为包含 `Kind`, `RelateId?`, `Name?`, `PositionJson?`, `Instructions?`, `ConfigJson?`。
- `AgentflowEdgeRequest` 改为包含 `Kind`, `Label?`, `ConditionJson?`, `ConfigJson?`。
- 新增执行状态接口：
  - `POST /api/agentflows/{id}/runs`
  - `GET /api/agentflows/runs/{runId}`
  - `POST /api/agentflows/runs/{runId}/human-responses`
  - `GET /api/agentflows/runs/{runId}/checkpoints`
- 所有 JSON API 仍返回 `AgwApiResult` / Bens.Results envelope。

## Test Plan

- Domain tests:
  - 拒绝环图、孤立节点、重复 node/edge id、非法引用、非法条件 DSL。
  - 允许多 start 仅当编译器能生成明确 fan-out start，否则拒绝。
  - 嵌套 `WorkflowAsAgent` 检测循环引用。
- Compiler tests:
  - direct DAG 编译成 `WorkflowBuilder.AddEdge`。
  - `PromptAdapter` 正确接收上游输出并转交下游 agent。
  - `HumanGate` 触发 `RequestInfoEvent` 并暂停。
  - `CheckpointMarker` 后能在 `SuperStepCompletedEvent` 中记录 checkpoint。
  - fan-in barrier 等待所有来源后再触发目标。
- Runtime tests:
  - streaming 输出按 agent 更新转换为 `AgwMessage`。
  - HITL response 后 workflow 继续并产生最终输出。
  - Lockstep 模式下事件顺序可断言。
- Frontend tests/manual checks:
  - 拖拽 Agent 和 WorkflowAsAgent，连线保存后后端 DAG 一致。
  - 节点 instructions、human gate 配置、checkpoint marker 保存/重新打开不丢失。
  - 无效 DAG 在保存前和保存时都有明确错误。

## Assumptions

- 不保留旧 `Pattern` 执行路径，也不迁移旧 Agentflow 数据。
- v1 只做 DAG 编排，不实现 GroupChat/Handoff/Magentic 的旧特殊模式。
- checkpoint 的 UI 语义是“在该位置暴露/命名 superstep checkpoint”，不是强行在任意代码行即时保存。
- 节点级提示词通过临时 agent instructions 或 `PromptAdapter` 生效，不修改原 Agent 定义。
