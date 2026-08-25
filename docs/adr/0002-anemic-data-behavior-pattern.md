# ADR 0002: 永久贫血数据模型与手动构造 Behavior

- 状态：Accepted
- 日期：2026-08-25
- 决策范围：全部后端 Module

## 背景

Agw 使用 anemic domain model。持久实体、领域数据对象、value object 和状态快照负责表达数据，Application 负责用例编排。Agentflow、Durable Execution、Integration Capability 等局部领域又包含图不变量、状态转换和 readiness policy；这些规则在传统 rich model 中通常会进入 entity 或 aggregate root。

把行为写回数据类型会让 EF 实体同时承担持久化、序列化和业务规则，破坏仓库既有约束。把 entity-bound 行为继续命名为宽泛的 `DomainService`，则无法明确该行为对应哪一个 data root，也容易与真正跨多个 consistency boundary 的 Domain Service 混淆。

## 决策

### 1. 数据永远贫血

持久实体、领域数据对象、value object 和状态快照只能保存数据。它们不得包含业务验证、规范化、状态转换、派生领域决策、domain-event collection 或其他在 rich model 中属于 entity 的行为。

自动属性、序列化/EF metadata 和编译器生成的 record equality 仍属于数据表达。Transport DTO 的协议 mapping 由 Mapper/Formatter 负责，不写入业务实体。

### 2. Entity-bound 行为进入 Behavior

若某个 data root 确实拥有业务行为，则在 owner Module 中创建：

```text
Domain/Behaviors/<Entity>Behavior.cs
```

例如，rich model 中本应属于 `Agentflow` 的图验证、规范化和受控状态修改由 `AgentflowBehavior` 承担。

Behavior 可以受控修改构造函数绑定的 root 及其 owned children，但不能修改 foreign entity。跨 boundary 的 Agent、Provider、Connection、actor 和时间等事实必须由 Application 解析为参数或只读 context 后传入方法。

### 3. Behavior 必须手动构造

Behavior 是一个短生命周期、entity-bound wrapper：

```csharp
public sealed class AgentflowBehavior
{
    private readonly Agentflow _agentflow;

    public AgentflowBehavior(Agentflow agentflow)
    {
        ArgumentNullException.ThrowIfNull(agentflow);
        _agentflow = agentflow;
    }

    public bool HasValidName()
    {
        // Root-local precondition; owned children are not needed.
    }

    public bool TryApplyGraphDecision(AgentflowDefinitionDecision decision)
    {
        // Reconcile fully loaded owned Nodes and Edges in place.
    }
}
```

Application 显式执行 `new AgentflowBehavior(agentflow)`。root-local precondition 可以立即调用；任何读取或修改 owned children 的 Behavior 方法执行前，Application 必须先完整加载 consistency boundary。Behavior：

- 不注册到 `IServiceCollection`；
- 不定义 `I<Entity>Behavior` Interface；
- 不引用或注入 Policy、DomainService、Repository、DbContext、TimeProvider、当前用户、HTTP、文件、MAF/MCP、IServiceProvider 或 Infrastructure Adapter；
- 不缓存、不序列化、不跨线程或 use case 复用；
- 没有独立持久状态，其可观察状态完全来自被包装的数据。

只有未来出现两个真实 Adapter 并获得明确架构授权时，才允许为 Behavior 引入 Interface。

### 4. Policy 与 DomainService 使用不同的构造规则

Behavior、Policy 和 DomainService 不是同一种生命周期：

- Behavior 绑定一个本次加载的数据 root，必须由 Application 手动构造；
- 纯 Domain Policy 不绑定数据实例，也没有持久状态，默认在调用点手动构造；这是简单性默认值，不是存在真实组合需求时对 IoC 的绝对禁令；
- 真正跨多个数据 boundary 的 DomainService 可以由 IoC 管理，但必须保持无状态，构造函数只能依赖纯 Domain 组件。

一个只处理单个 root 的 `XxxDomainService` 是错置的 entity Behavior。IoC-managed DomainService 不得捕获 Behavior 或数据 root，也不得依赖 Repository、DbContext、TimeProvider、当前用户、HTTP、文件、MAF/MCP、Application Service 或 Infrastructure Adapter。Application 将领域数据和外部事实作为方法参数传入，并将 DomainService 返回的 data-only decision 交给相应 Behavior 应用。

Behavior 不得引用、注入或手动构造 Policy/DomainService。依赖方向固定，但 Application 根据用例选择调用顺序；root-local 前置条件可以在外部查询和 Policy 求值之前执行：

```text
Application
  → new XxxBehavior(completeRoot)
  → optional root-local preconditions
  → new XxxPolicy()
  → Policy evaluates data + external facts
  → data-only XxxDecision
  → Behavior applies Decision
  → persistence
```

Decision 放在中性的 `Domain/Decisions`，只能携带数据。Policy、compiler、runtime 共同需要的只读算法放在 `Domain/Topology`、`Domain/Rules` 或 `Domain/Algorithms`；它们不是 Policy，也不通过 Behavior 转发。

对 EF tracked consistency boundary，Application 必须先加载完整 root 与 Behavior 会修改的 owned navigations。Behavior 按稳定 child key 原地 reconcile：更新同键实例、移除缺失 child、只添加新 child。禁止先替换 tracked navigation 再查询旧行，也禁止在一个 unit of work 中删除并重新添加同主键的两个实例。

### 5. Application 与 Infrastructure 职责不变

Application 负责鉴权、外部事实查询、Behavior 构造、用例顺序、错误映射和 transaction。Infrastructure 负责 EF、加密、HTTP、文件和外部 Provider Adapter。审计字段继续由 EF interceptor 维护，不属于 Behavior。

如果 Behavior 产生有意义的结果，它返回 data-only result/fact；Application 在持久化成功后决定是否处理。Domain event 不保存在贫血实体中，也不因采用 Behavior Pattern 自动引入 mediator 或 outbox。

### 6. 简单 CRUD 不创建空 Behavior

只有存在真实业务不变量或状态转换时才创建 Behavior。纯 list/get/create/update/delete、配置表、审计记录和 read model 保持简单 Application + `I<Module>DbContext`。

### 7. Agentflow 采用选择性 DDD

Agentflow 是当前明确采用选择性 DDD 的子域，但 `Agw.Agents` 不是整体 DDD 化：

- `Agentflow` 及其 owned `AgentflowNode`、`AgentflowEdge` 构成图定义的一致性边界。
- `AgentflowDefinitionPolicy` 根据 proposed graph 和 Agent/ModelProvider 外部事实产生 data-only `AgentflowDefinitionDecision`；`AgentflowBehavior` 只应用该 Decision，不引用 Policy。
- `AgentflowTopology` 承担 definition Policy、compiler 和 runtime 共用的只读拓扑与配置解析算法。
- Agentflow 更新先完整加载 root、Nodes、Edges，再由 Behavior 按 NodeId/EdgeId 原地 reconcile，避免 EF relationship fix-up 和 identity tracking 冲突。
- Agentflow 的 list/get/create/update/delete、API DTO、compiler/runtime、checkpoint/history、trace 和 durable execution 仍由 Application/Infrastructure 编排。
- Auth、Jobs、Providers、Skills、基础 Integrations/Tools 等简单 CRUD 模块不因为 Agentflow 的选择性 DDD 而整体引入 Aggregate、Behavior 或 Domain Event。

## 备选方案

### 行为丰富 entity/aggregate root

最接近经典 DDD，但违反永久贫血模型约束，并使 persistence entity 同时承担业务行为。拒绝。

### 由 IoC 管理无状态 Behavior

方便注入依赖，但会鼓励 Repository、clock、当前用户和外部 Adapter 进入领域行为，并模糊 Behavior 与 Application Service 的职责。拒绝。

### Behavior 内部构造或调用 Policy

虽然都位于 Domain，但这会把 Policy 编排隐藏在 Behavior 内，使 Behavior 退化为 Policy 的薄包装。采用 Application 显式协调 Policy、Decision 与 Behavior；调用顺序可因 root-local 前置校验调整，但 Behavior 永远不依赖 Policy。拒绝。

### Behavior 返回完整数据副本

纯函数性质更强，但会为 EF tracked graph 和大型 Agentflow 引入无必要复制。采用受控原地修改。

### 每个实体创建一个 Behavior

形式统一但会产生浅 Module 和空壳类型。拒绝。

## 结果

- 数据模型持续简单，EF 和 wire compatibility 不受领域行为影响；
- rich model 的规则通过同名 Behavior 获得 Locality；
- Application 保持 use-case orchestration，Behavior 保持 framework-free；
- Behavior 由构造函数绑定完整数据 boundary，调用方必须显式看见其生命周期；
- 纯 Policy 默认显式构造；真正跨数据 boundary 的无状态 DomainService 可以通过 IoC 复用纯领域组件；
- Policy 只产生 data-only Decision，Behavior 只应用 Decision；Architecture Tests 禁止 Behavior 引用 Policy/DomainService；
- EF-tracked aggregate 通过完整加载和同键原地 reconcile 维持单实例 identity；
- 现有 entity-specific `DomainService` 与实体方法通过递减 allowlist 渐进迁移，禁止新增 single-root DomainService 债务。
