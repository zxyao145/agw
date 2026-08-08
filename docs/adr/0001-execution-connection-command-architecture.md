# ADR 0001: Execution Connection 与可扩展 Command Handler

- 状态：Accepted
- 日期：2026-08-05
- 决策范围：`Agw.Agents.Execution`

## 背景

实时执行入口接收 `SettingCommand`、`ExecCommand`、`InterruptCommand` 和 `HumanResponseCommand`。旧实现虽然已经把 switch 拆成多个 handler，但每个 handler 都能直接读写连接对象上的 settings、task、target 和 runtime。结果是：

- handler 与 SignalR connection 生命周期耦合；
- connection 状态的不变量分散在多个 handler；
- 新增 command 时需要同时维护 handler DI 和 `AgentRunCommand` 上的 JSON 派生类型列表；
- runtime skill 通过 `IRuntimeTurnContextAccessor` 既能读 `Current`，也能调用 `Push` 覆盖上下文；
- project、workspace、agent、task 等跨 command 数据缺少一个明确的所有者。

目标是支持编译期增加 command/handler，同时保持同一 execution connection 的状态一致，并让 turn 内代码获得稳定的只读上下文。

## 决策

采用 `Command Pattern + typed handler strategy registry + Execution Connection kernel`。

### 1. Connection 是实时执行的生命周期边界

`ExecutionConnectionRegistry` 仍是 SignalR adapter，负责把 connection id 映射到 `ExecutionConnection`。

名称直接反映当前不变量：对象随 SignalR connection id 创建、断开并最终释放，不提供重连、持久化执行身份或跨 connection 恢复。MAF 的 `AgentSession` 继续专指模型对话状态；只有未来出现可重连、可持久化且独立于 transport connection 的执行身份时，才引入单独的 `ExecutionSession`。

`ExecutionConnection` 负责：

- 串行分派 command；
- attached/detached 生命周期；
- connection 级 DI scope 的释放；
- 在断线后等待活动 turn 收敛。

`ExecutionConnectionContext` 是深模块，独占 settings、resolved task、workspace、target、runtime 和 HumanGate waiting 状态。它通过原子操作维护状态，不向 handler 暴露 runtime 或可写字段。

### 2. Handler 是薄的命令翻译层

每个 handler 实现：

```csharp
IExecutionCommandHandler<TCommand>
```

handler 只校验/翻译自己的 command，并调用 `ExecutionConnectionContext` 的稳定操作。它不依赖 SignalR、不解析 project/task、不持有锁、不直接读写 runtime。

dispatcher 通过内部 adapter 擦除泛型并按 command CLR type 查找 handler。重复 command type 立即失败。

`Commands` 采用按能力垂直切片的目录结构。每个 command 子目录共置 transport contract 与 handler；`Abstracts/` 只保存 `AgentRunCommand` 和 handler 接口，dispatcher 与注册 seam 保留在 `Commands/` 根目录。这样一次 command 变更具有更好的 locality，不需要在独立的 `Contracts` 与 `Commands` 目录之间同步移动。

### 3. 一个注册 seam 同时维护分派和 wire contract

command 通过以下扩展注册：

```csharp
services.AddExecutionCommand<TCommand, THandler>(discriminator);
```

该调用同时注册：

- `IExecutionCommandHandler<TCommand>`；
- dispatcher 使用的内部 adapter；
- SignalR JSON 的 command type/discriminator 映射。

`AgentRunCommand` 不再维护 `[JsonDerivedType]` 中心列表。重复 CLR type 或 discriminator 在构造 dispatcher/options 时失败。

扩展范围是 compile-time module composition；本决策不引入 assembly scanning、动态程序集加载或运行时插件协议。

### 4. Connection state 与 turn snapshot 分离

`ExecutionSettings` 是从可变 transport command 复制出的不可变值。`ExecutionConnectionContext` 可在多个 command/turn 间更新状态。

启动 turn 时，context 生成不可变 `RuntimeTurnContext`，包含：

- `ExecutionSettings`；
- `TaskProjection`；
- `ExecutionTarget`；
- project/context/agent 的便捷标识；
- user、workspace、message sink；
- HumanGate pending 状态回调。

`IRuntimeTurnContextAccessor` 只公开 `Current`。写入 scope 的 `Push` 留在 Agents 模块内部，由 `RuntimeBase` 建立和恢复，runtime skill 只能读取。

### 5. 状态失效规则

- 相同 settings：无操作；
- project/context/environment/resume 改变：释放 runtime，清空 task、workspace 和 target；
- target 改变：释放 runtime，保留 task 与 workspace；
- turn 正常结束：保留 runtime，供同一 target 下一轮复用；
- connection dispose：释放 runtime 与 DI scope；
- 活动 turn 中修改 settings 或再次 exec：返回 busy error。

## 备选方案

### Handler 直接操作 connection 状态

代码量较少，但接口很宽，状态不变量分散；新增 command 容易绕过既有失效规则。拒绝。

### 中心 switch/mediator

中心 switch 会让每次扩展修改同一个文件。引入通用 mediator 能减少自建代码，但无法自然解决 SignalR discriminator 单一注册、connection 生命周期和 runtime 状态所有权问题。当前四类命令不需要额外框架。拒绝。

### Runtime plugin/assembly scanning

动态发现降低显式注册，但增加启动不确定性、部署与兼容性成本。当前需求只要求编译期模块扩展。拒绝。

### 把所有共享数据放进 AsyncLocal

AsyncLocal 适合一次 turn 的只读传播，不适合跨 command 的可变 connection 状态。否则断线、settings 失效和 runtime 复用缺少可靠所有者。拒绝。

## 结果

正向结果：

- 新增 command 不修改 dispatcher 或 command 基类；
- handler 的接口更窄，能够独立测试和组合；
- connection 状态规则集中，runtime 复用与释放具有单一所有者；
- runtime skill 可读取完整 turn snapshot，但不能覆盖上下文；
- SignalR transport 与执行内核之间形成清晰 seam。

代价与约束：

- `ExecutionConnectionContext` 是关键深模块，新增操作必须维护现有状态不变量；
- 新 command 必须显式调用注册扩展；
- JSON 多态配置依赖 DI options，脱离 Host 的序列化测试需要使用同一 command 注册构造 options；
- 本方案不支持运行时安装 command，若未来出现该需求，需要新的插件安全、版本和隔离决策。
