# ADR 0003: Agents 执行实现程序集

- 状态：Accepted
- 日期：2026-09-08
- 决策范围：Agw.Agents、Agw.Agents.Execution 与 Host composition

## 背景

Execution 已承担完整的运行时、连接与 turn 生命周期、人工交互、恢复、事件回放和 worker 职责。A2A 和 Jobs 已通过 Contracts 调用执行能力，但定义管理的 Mermaid 查询及持久化 scope reader 仍直接依赖执行实现，无法直接移动目录形成单向项目引用。

## 决策

- 新建 `Agw.Agents.Execution`，承载执行实现、Claude/Pi 运行时适配器及执行 DI。
- `Agw.Agents.Execution → Agw.Agents` 为单向程序集引用；两者仍是同一个逻辑 Agents 模块。
- 原 Agents 保留定义管理、Catalog、Behavior/Policy/Topology 和 Application/Persistence 接缝。
- Host 分别调用 `AddAgents` 与 `AddAgentExecution`。A2A、Jobs 保持 Contracts 调用方式。
- 管理侧依赖 `IAgentflowMermaidProvider`，由同一个 scoped Agentflow runtime 实现，继续构建实际 Workflow 生成 Mermaid。
- Durable manifest、task snapshot、settings 保持单份纯数据定义，位于 Agents 的持久化接缝；映射到运行时任务和 command 的方法位于 Execution。
- `PermissionMode` 移到 Contracts，保留原 JSON enum 名称。执行命令、manifest 和 checkpoint 的持久化格式不因程序集拆分改变。
- 架构测试将两个程序集映射到 Agents 逻辑所有权；项目引用矩阵仍单独精确约束物理依赖。跨模块数据访问与用户隔离检查继续覆盖执行工程。
- Entities、EF configurations、单一 AgwDbContext 与数据库所有权保持现有安排。

## 结果

定义管理无法通过项目引用直接使用执行实现；运行时变更集中在执行程序集，现有调用接口和生命周期封装保持稳定。ADR 0001 的 Connection/turn 规则与 ADR 0002 的贫血数据模式继续有效。

ExternalAgentDefaults 与 AgentNames 留在 Agents，默认配置仍使用 SDK options。Control Plane 仍组合 durable client 和执行实现，完整 SDK 隔离需要另行处理默认配置、Mermaid 和 Host composition；本决策不引入这一扩展范围。
