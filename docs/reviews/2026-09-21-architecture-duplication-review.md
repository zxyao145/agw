# 架构设计、代码重复与职责边界审查

审查日期：2026-09-21。基线：`666be9f2`，审查开始时工作区干净。

## 审查范围与验证方式

覆盖 `src/server/` 的 12 个业务模块与 `src/clients/` 的 22 个工作区包，约 17 万行 C# 与 8.5 万行 TypeScript；排除 `obj`、`bin`、`node_modules`、`.next`、EF Migrations 与生成文件 `packages/api/src/openapi.d.ts`。

判定依据来自四类检查：`AGENTS.md` 与 [docs/rules.md](../rules.md)、[docs/3.Module Organization.md](../3.Module%20Organization.md) 声明的规则与实际代码的对照；`tests/Agw.Architecture.Tests` 守卫覆盖范围的阅读；跨文件规范化行块比对（14 行窗口，忽略空白与注释）；单点符号的调用者枚举。

本次没有运行完整测试套件，没有做并发或故障注入实验，没有评估运行时性能。所有条目都以当前磁盘上的源码为准，标注的行号对应上述基线。编号 AD 表示架构设计，DUP 表示代码重复，RS 表示职责边界。

## 架构设计

### ~~AD-01 · `Agw.Infrastructure` 是不受模块边界约束的中心项目~~
无需修复。

**证据：** [BackendArchitectureTests.cs](../../tests/Agw.Architecture.Tests/BackendArchitectureTests.cs) 第 57 行的依赖矩阵、第 415 行与第 464 行的整体豁免、第 530 行的定向豁免；[EfApiTokenStore.cs](../../src/server/Agw.Infrastructure/Auth/EfApiTokenStore.cs)、[EfSettingsPersistence.cs](../../src/server/Agw.Infrastructure/Settings/EfSettingsPersistence.cs)、[ProjectMemoryPersistence.cs](../../src/server/Agw.Infrastructure/Tools/ProjectMemoryPersistence.cs)、[AgentSessionStatePersistence.cs](../../src/server/Agw.Infrastructure/Agents/AgentSessionStatePersistence.cs)。

依赖矩阵允许 `Agw.Infrastructure` 引用 10 个业务模块。`FindConcreteServiceAndRepositoryViolations` 与 `FindForeignPersistenceAccesses` 两个检查的第一条语句都是 `if (owningProject == "Agw.Infrastructure") yield break;`，因此这个项目完全不参与跨模块持久化访问与具体服务引用的判定；第 530 行另有一条针对 `.Application.Persistence` 结尾命名空间的定向豁免，那一条与实现各模块接缝的设计意图一致。

项目内容包含两类代码。跨所有者事务属于 [docs/3.Module Organization.md](../3.Module%20Organization.md) 第 153 行承认的范围：`AgentDeletionCoordinator`、`ProjectDeletionCoordinator`、`JobOutcomeTransaction`、`ConversationExecutionGate`。另一类是单模块适配器：`EfApiTokenStore` 只访问 `ApiTokens`，`EfSettingsPersistence` 只访问 `Settings`，`ProjectMemoryPersistence` 与 `AgentSessionStatePersistence` 同样限于单个所有者。这四个类都在构造函数中注入具体的 `AgwDbContext`，没有使用本模块已经存在的接缝——`IAuthDbContext`、`ISettingsDbContext`、`IToolsDbContext`、`IAgentsDbContext` 均已定义，且由 `AgwDbContext` 实现。注入具体类型是它们必须位于这个项目的直接原因，业务模块不允许引用 `Agw.Infrastructure`。

**后果：** 需要跨越所有权约束的代码有一个不受守卫检查的合法去处。判断某段持久化代码属于哪个模块时，物理位置不再提供信息。

### ~~AD-02 · 执行程序集穿透 Tools 与 Integrations 的实现层~~

无需修复。

### ~~AD-03 · `IAgentRuntimeService` 的重载链只服务于测试替身~~

**证据：** [IAgentRuntimeService.cs](../../src/server/Agw.Agents.Execution/Agents/Runtime/IAgentRuntimeService.cs) 第 13、82、87、103、124 行；[AgentRuntimeService.CreateAiAgent.cs](../../src/server/Agw.Agents.Execution/Agents/Runtime/AgentRuntimeService.CreateAiAgent.cs)；[AgentflowWorkflowFactory.cs](../../src/server/Agw.Agents.Execution/Agentflows/Workflows/AgentflowWorkflowFactory.cs) 第 150 行。

接口声明 5 个创建 Agent 的重载。模块外的生产调用点只有 `AgentflowWorkflowFactory` 第 150 行，调用的是 7 参数版本的 `CreateAgentflowNodeAgentAsync`。`CreateAiAgentAsync` 的 3 个公开重载在生产代码中没有外部调用者，`AgentRuntimeService` 内部的两处调用指向接收 `CreateAiAgentRequest` 的版本。

接口中 5 个成员带有抛出 `AgwException` 的默认实现。生产唯一实现 `AgentRuntimeService` 覆盖了全部成员，这些默认体永不执行。实现该接口的另外三个类型全部位于测试：`AgentflowRuntimeServiceTests.cs:1326`、`AgentExecutionFacadeTests.cs:189`、`RuntimeDefinitionRefreshTests.cs:413`。

**后果：** 接口不表达真实能力集合。新增第二个实现时，缺失的能力在运行时才暴露。每个重载需要在接口、实现、三个测试替身共五处维护。

### ~~AD-04 · `AgentRuntimeService` 构造函数承载 24 个参数并在其中做组合决策~~

**证据：** [AgentRuntimeService.cs](../../src/server/Agw.Agents.Execution/Agents/Runtime/AgentRuntimeService.cs) 第 49 至 85 行。

构造函数接收 24 个参数，其中 8 个带 `= null` 默认值。第 78 至 85 行探测 `chatHistoryProvider` 是否提供 `IConversationMessageWriter`，据此决定是否把它包装成 `NormalizedChatHistoryProvider`。

**后果：** 装饰器组合的决定位于服务内部，同一个类型的实际行为取决于传入依赖的内部能力。DI 注册处无法读出最终生效的历史写入链路。

### ~~AD-05 · 分层位置在模块之间不一致~~

| 位置 | 情况 |
| --- | --- |
| Controller 目录命名 | Auth、Files、Settings、Tools 使用 `Api/`；Agents、Integrations、Projects、Providers、Setup、Skills、Host 使用 `Controllers/` |

[docs/rules.md](../rules.md) 第 2 节的分层为 `Api → Application → Domain ← Infrastructure`，`AGENTS.md` 要求 DTO 位于所有者模块的 `Contracts/`。这两条声明约束的是类型归属于哪一层，上表的 Controller 目录名称差异保持现状。

Providers、Files、Jobs、Projects 四个模块的分层位置符合上述两条声明：

- [Agw.Providers](../../src/server/Agw.Providers/Controllers/)：三个 Controller 位于 `Controllers/`，namespace 为 `Agw.Providers.Controllers`。
- [Agw.Files](../../src/server/Agw.Files/)：`Infrastructure/Storage/` 存放本地文件系统适配器与 Project 作用域解析器，`Infrastructure/Git/` 存放 CliWrap 的 git 进程适配器；`Abstracts/` 存放 `IAgwFileSystem`、`ILocalFileSystem`、`IGitCommandService` 与 git 返回模型，`Application/Files/FileAppService` 只依赖这些抽象。
- [Agw.Jobs](../../src/server/Agw.Jobs/Contracts/)：请求与响应 DTO 位于 `Contracts/`，Tool DTO 位于 `Contracts/Tools/`。
- [Agw.Projects](../../src/server/Agw.Projects/Domain/Rules/TaskTitleRules.cs)：会话标题规则位于 `Domain/Rules/`。

### ~~AD-06 · 客户端测试以源码正则断言为主~~

已修复。

客户端测试不再通过 `readFile` 读入源码文本后做正则断言。源码正则断言已转为 jsdom + Testing Library 的真实渲染测试，或删除（纯 CSS 类名、样式取值、JSX 属性书写顺序类断言不补替代）。渲染测试通过新增的 `@agw/test-harness` 包建立 DOM 环境、共享各工作区的 React 实例、补齐 jsdom 缺失的浏览器 API，并通过真实 HTTP 服务器（`startApiServer`）让组件走自身的 `fetch` 路径，不使用替身。

跨包 import 约束集中到 `tools/scripts/check-client-boundaries.mjs`，改用 TypeScript 编译器 API 提取每个源文件的 import 说明符并解析相对导入的跨工作区越界，替换原先的目录存在性判据与正则匹配。

修复过程中确认并修正一处缺陷：`UserInputWithRef` 的 ref 转发只保留挂载时的句柄，`insertText`/`setInput` 始终以空输入为基准，现改为直接转发 ref。

**证据：** 约 60 个测试文件通过 `readFile` 读入源码文本后 `assert.match` 或 `assert.doesNotMatch`，累计 1362 个断言。

[packages/chat/src/ui-web/pages/chat/page.test.ts](../../src/clients/packages/chat/src/ui-web/pages/chat/page.test.ts) 有 91 个断言，内容包括 `/compactToolbar && "h-8 p-2"/` 这样的 CSS 类名字符串，以及 `/<ChatWorkspace routeBasePath="\/chat" showProjectSelect\s*\/>/` 这样的 JSX 属性书写顺序。[packages/chat-native/src/package-boundary.test.ts](../../src/clients/packages/chat-native/src/package-boundary.test.ts) 以边界命名，35 个断言中包含 `/messageBlock: \{ width: "100%", gap: 4 \}/` 与 `/code: "#f4f4f4"/` 这类样式取值。

使用 testing-library 执行真实渲染的文件为 `packages/chat` 的 5 个与 `mobile` 的 17 个。[tools/scripts/check-client-boundaries.mjs](../../src/clients/tools/scripts/check-client-boundaries.mjs) 以目录与文件是否存在作为判据，没有分析 import 图。

**后果：** 这些测试不执行被测代码，格式化工具调整空白即失败，被断言的渲染行为本身没有覆盖。测试文件名（`*-boundary`、`*-theme`、`*-usage`）与实际验证内容不对应。

### ~~AD-07 · 三处 C# primary constructor~~

**证据：** [AgwContent.cs](../../src/server/Agw.Agents.Contracts/Messages/AgwContent.cs) 第 184 行、[AgentflowWorkflowCompiler.cs](../../src/server/Agw.Agents.Execution/Agentflows/Workflows/AgentflowWorkflowCompiler.cs) 第 1115 行、[SignalRExecutionMessageSink.cs](../../src/server/Agw.Agents.Execution/Outbound/SignalR/SignalRExecutionMessageSink.cs) 第 7 行。

`AGENTS.md` 的编码规则为 "Never use C# primary constructors"，当前没有守卫覆盖这一条。同一规则集中的 `DateTime`、`new HttpClient()`、Controller 返回裸 `IActionResult` 三条在全仓库检查中没有出现违反实例。

## 代码重复

### ~~DUP-01 · `CloneResult` 完整定义两份~~

已修复。

`CloneResult` 定义在 [Agw.Shared/Tooling/CloneResult.cs](../../src/server/Agw.Shared/Tooling/CloneResult.cs)，与同目录的 `ToolValueObject`、`ToolDefinitionNames` 一起构成跨模块的 Tool 契约。`Agw.Integrations` 直接引用 `Agw.Shared`，`Agw.Tools` 经由 `Agw.Files`、`Agw.Auth` 传递引用取得，两侧都不需要新增 `ProjectReference`，依赖矩阵保持原样。属性名与 `Description` 文案逐字保留，模型看到的 JSON schema 不变。

`GitTools.cs` 的 namespace 改为 `Agw.Tools.Impl.Tools.Git`，与目录一致。

### ~~DUP-02 · `getApiErrorMessage` 三份，取值优先级不同~~

**证据：** [packages/api/src/utils.ts](../../src/clients/packages/api/src/utils.ts) 第 3 行、[packages/projects/src/ui-web/pages/projects/details/page.tsx](../../src/clients/packages/projects/src/ui-web/pages/projects/details/page.tsx) 第 46 行、[packages/observability/src/ui-web/pages/dashboard/page.tsx](../../src/clients/packages/observability/src/ui-web/pages/dashboard/page.tsx) 第 21 行。

`@agw/api` 的版本按 `detail` → `title` 取值；两个页面的本地版本按 `message` → `error` → `detail` → `title` 取值。`packages/api/src/index.ts` 已经 `export * from "./utils"`，`AGENTS.md` 也把 Bens.Results 的解包归属给 `@agw/api`。

**后果：** 这两个页面显示的后端错误信息与其余页面来自不同字段。

### ~~DUP-03 · 环境变量名称校验两份~~

**证据：** [AgentBehavior.cs](../../src/server/Agw.Agents/Definitions/Domain/Behaviors/AgentBehavior.cs) 第 110 行与 [ProjectBehavior.cs](../../src/server/Agw.Projects/Domain/Behaviors/ProjectBehavior.cs) 第 47 行。

循环体、`Trim()`、`Contains('=')`、`Contains('\0')`、`TryAdd`、`StringComparer.Ordinal` 完全一致，差异只在抛出的 `ErrorCode`。[docs/rules.md](../rules.md) 第 3 节把这类可复用的只读规则归属到 `Domain/Rules`，Projects 模块已有 `ProjectRules`。

### ~~DUP-04 · `updateConfigJson` 与 `shouldRemoveConfigValue` 两份~~

**证据：** [block-membership.ts](../../src/clients/packages/agents/src/ui-web/pages/agentflows/components/block-membership.ts) 第 259、272 行与同目录 [visual-agentflow-builder.tsx](../../src/clients/packages/agents/src/ui-web/pages/agentflows/components/visual-agentflow-builder.tsx) 第 2853、2866 行，逐字相同。

### ~~DUP-05 · 所有权校验在两个拦截器中各写一份且已经分化~~

**证据：** [EntityCreatorInterceptor.cs](../../src/server/Agw.Infrastructure/Data/Interceptors/EntityCreatorInterceptor.cs) 第 55 行与 [EntityModifierInterceptor.cs](../../src/server/Agw.Infrastructure/Data/Interceptors/EntityModifierInterceptor.cs) 第 81 行的 `EnsureOwnerMatchesCurrentUser`。

Creator 版本跳过 `JobLog`，`UserId` 为空时补填当前用户；Modifier 版本不跳过 `JobLog`，`UserId` 为空时抛出异常。新建与修改的语义差别可以解释部分分化，同一条所有权规则写在两处的结果是：后续修订需要人工判断该同步哪一半。

两个拦截器与 [EntitySoftDeleteInterceptor.cs](../../src/server/Agw.Infrastructure/Data/Interceptors/EntitySoftDeleteInterceptor.cs) 的 `SavingChanges`、`SavingChangesAsync`、`BeforeSaveChanges` 构成三份相同结构。

### ~~DUP-06 · 测试替身同名类在同一个程序集里各写三份~~

无需修复，测试项目允许重复。

**证据：** `Agw.Files.Tests` 的 `FakeGitCommandService` 与 `FakeFileSystemResolver` 分别定义在 [FilesControllerSearchTests.cs](../../tests/Agw.Files.Tests/FilesControllerSearchTests.cs)、[FileAppServiceTests.cs](../../tests/Agw.Files.Tests/FileAppServiceTests.cs)、[FilesControllerProjectFileSystemTests.cs](../../tests/Agw.Files.Tests/FilesControllerProjectFileSystemTests.cs)。

全部测试项目中带 `Stub`、`Fake`、`Recording`、`Mock`、`Noop` 前缀的类共 222 个。`AGENTS.md` 的测试规则为 "Do not use mocks or fake implementations"，`GitCommandService` 与本地文件系统在测试机器上都是可直接运行的真实组件。

### ~~DUP-07 · `AgentflowNodeScopedAgent` 内部两段 15 行重复~~

**证据：** [AgentflowNodeScopedAgent.cs](../../src/server/Agw.Agents.Execution/Agentflows/Context/AgentflowNodeScopedAgent.cs) 第 181 至 197 行与第 271 至 287 行。

`RunAsync` 与 `RunStreamingAsync` 的前置准备逐字相同，包含 interaction node id 读取、pending function call id 的读取与保存、输入变换、activity 启动、`ToolTurnPersistence` 构造。

### ~~DUP-08 · JSON 序列化约定两套并存~~

**证据：** [JsonUtil.cs](../../src/server/Agw.Shared/Utils/JsonUtil.cs)；[AgentflowCheckpointStore.cs](../../src/server/Agw.Agents.Execution/Agentflows/Checkpoints/AgentflowCheckpointStore.cs) 第 24 行；[AgentMessageProjection.cs](../../src/server/Agw.Agents.Execution/Agents/History/AgentMessageProjection.cs) 第 42 行。

`JsonUtil` 配置了 camelCase 命名策略、`JsonStringEnumConverter` 与全 Unicode 编码器，被 12 个文件、23 处使用。另有 16 处各自声明裸 `new(JsonSerializerDefaults.Web)`，其中包含持久化路径：checkpoint 的存取，以及消息投影中用于事件去重的签名计算。两套选项在枚举写法上产生不同输出。`JsonUtil` 内还保留一行注释掉的 Encoder 配置，字段名 `OPTIONS` 与 `AGENTS.md` 的 PascalCase 成员命名规则不一致。

## 职责边界

### ~~RS-01 · `AgentBehavior.ApplyUpdate` 把变更权交回 Application~~

**证据：** [AgentBehavior.cs](../../src/server/Agw.Agents/Definitions/Domain/Behaviors/AgentBehavior.cs) 第 28 至 63 行；[AgentAppService.cs](../../src/server/Agw.Agents/Definitions/Agents/AgentAppService.cs) 第 255、264、390、420 行。

`ApplyUpdate` 接收 `Action<Agent>` 委托，先保存 6 个字段的原值，执行委托，再把这 6 个字段还原。字段更新规则位于 Application：`ApplyExternalAgentUpdate` 与 `ApplySystemAgentUpdate` 对实体逐字段赋值。

"External Agent 哪些字段不可变" 这条规则因此表达了两遍：Application 的赋值方法体现为不写这些字段，Behavior 体现为保存与还原。[docs/rules.md](../rules.md) 第 3 节要求 Behavior 拥有实体的变更规则。

**后果：** Agent 新增字段时需要同时维护两处，任何一处遗漏都不会在编译期暴露。

### ~~RS-02 · `AgentRuntimeService` 在执行路径上是转发外壳~~

**证据：** [AgentRuntimeService.Execution.cs](../../src/server/Agw.Agents.Execution/Agents/Runtime/AgentRuntimeService.Execution.cs) 第 22 至 45 行；[AgentRuntime.cs](../../src/server/Agw.Agents.Execution/Agents/Runtime/AgentRuntime.cs) 第 70、92、100、223、246 行；[AgentTurnExecutor.cs](../../src/server/Agw.Agents.Execution/Agents/Runtime/AgentTurnExecutor.cs) 第 28、40、99、169 行。

`AgentRuntimeService` 的四个公开执行方法都是单行委托给 `_turnExecutor`，实现位于 `AgentTurnExecutor`。`AgentRuntime` 自身另有一套同名的 `ExecuteStreamingAsync` 与 `ExecuteAsync`。同名方法分布在三个类型上，调用方需要判断从哪个入口进入。

### ~~RS-03 · `EfProjectMemoryStore` 不接触 EF~~

**证据：** [EfProjectMemoryStore.cs](../../src/server/Agw.Tools/Impl/ToolBlocks/Storage/EfProjectMemoryStore.cs)。

全部操作通过 `IProjectMemoryPersistence` 端口完成，类名的 `Ef` 前缀指向它并不拥有的持久化技术。该类的 6 个方法各自重复 `CreateAsyncScope()` 与 `GetRequiredService<IProjectMemoryPersistence>()` 两行。

## 与上一次审查的关系

[2026-09-12 技术债审查](2026-09-12-technical-debt-review.md) 覆盖的是并发互斥、资源生命周期与故障恢复，与本次范围不重叠。该文档中"架构守卫已有效约束模块依赖"的结论在原始持久化访问 inventory 上仍然成立，`AllowedLegacyForeignPersistenceAccessCounts` 当前为空字典，`AllowedLegacyDataBehaviorMembers` 与 `AllowedLegacyEntityDomainServices` 同样为空。

本次记录的 AD-01 与 AD-02 位于守卫规则的字面覆盖之外：`Agw.Infrastructure` 由显式分支跳过检查，`Impl` 与 `Mcp` 两个命名空间段不在 `ContainsInternalLayer` 的判定集合内。

同一份文档中"不以文件长度、分层数量、没有采用某种框架等代理指标代替缺陷证据"的边界在本次继续沿用：所有条目都给出可定位的源码位置与可复核的对照对象，没有以文件规模本身作为判定依据。
