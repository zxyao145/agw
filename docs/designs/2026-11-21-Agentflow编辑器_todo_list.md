# Agentflow 编辑器：实施计划

> **给执行计划的 agent：** 必须使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 按任务逐个实施。步骤使用 checkbox（`- [ ]`）记录进度。

**目标：** Agentflow 的定义改为带 revision 的类型化模型，Server 校验返回 Problems，Turn 受理时固定根与嵌套引用的 revision，执行过程发送节点状态消息，并按设计文档重写 Web 的 Agentflow 编辑器。

**架构：** 定义保存在 `agentflow_revision`、`agentflow_node`、`agentflow_edge`、`agentflow_block_member` 中，每次保存新增一个 revision，发布只修改 `agentflow.published_revision`。Turn 受理事务生成 `AgentflowRevisionSnapshot`，写入 Turn 的 `extra` 与 Durable manifest，之后的编译、分段、重试与 checkpoint 恢复都只读取这份快照。编辑器的文档是 `pnpm gen:api` 生成的类型化节点与连线，连线路径、分支操作、运行状态、剪贴板由没有 React 依赖的模块计算，校验全部来自 `POST /api/agentflows/validate`。

**技术栈：** .NET 10、ASP.NET Core、EF Core（SQLite、PostgreSQL）、Microsoft.Agents.AI Workflows、xUnit v3；Next.js、React 19、TypeScript、reactflow 11.11.4、elkjs 0.12.0、zustand 5、Node test runner（`tsx --test`）、Playwright、Electron 集成测试驱动。

**设计文档：** `docs/designs/2026-11-21-Agentflow编辑器.md`。执行每个任务之前阅读设计文档的对应章节，下文的"第 N 节"都指这份文档"详细设计"中的小节。

## 全局约束

- 没有兼容性要求。任务 10 结束后代码中没有旧的列、旧的接口与旧的消息属性；任务 1 到任务 9 之间为了保持可以编译而保留的旧成员，由任务 10 统一删除。
- 名称：
  - revision 自己的列是 `revision`，其他表引用它的列是 `agentflow_revision`，属性 `AgentflowRevision`，JSON 中是 `agentflowRevision`。
  - 只作记录的 JSON 列是 `extra`，属性 `Extra`，键使用 camelCase。
  - 接口中的 `kind` 与 `routingStrategy` 是 camelCase 的枚举名字符串。
- 错误码（`src/server/Agw.Shared/Exceptions/ErrorCodes.cs`）：
  - `AgentflowDefinitionInvalid = new(400_0080, "The Agentflow definition has problems.", HttpStatusCode.BadRequest)`
  - `AgentflowRevisionNotFound = new(404_0018, "Agentflow revision was not found.", HttpStatusCode.NotFound)`
  - `AgentflowRevisionConflict = new(409_0038, "The Agentflow was saved from another revision.", HttpStatusCode.Conflict)`
  - `AgentflowNotPublished = new(409_0039, "The Agentflow has no published revision.", HttpStatusCode.Conflict)`
  - `AgentflowInUse = new(409_0040, "The Agentflow revision is referenced by an active execution or a resumable checkpoint.", HttpStatusCode.Conflict)`
- 接口：JSON 接口返回 Bens.Results（`ApiResult.Ok(...)`），参数放在 query 或 body 中，路径没有参数。预期失败抛出 `AgwException(ErrorCodes.X)`。
- 当前用户通过 `UserInfoUtil.RequiredUserId` 或 `IUserInfoService` 读取。他人的 id 与不存在的 id 返回相同的 `ResourceNotFound`。
- 设计文档第 6.5 节的 `ExecutionSessionHandlers` 在代码中的名称是 `ExecutionHubHandlers`（`src/clients/packages/chat-runtime/src/execution-session.ts`），本计划使用代码中的名称。
- 测试：
  - Server 使用 xUnit v3，命名为 `Method_Condition_ExpectedResult`，使用真正运行的实现与 SQLite；定义、受理与持久化的新用例使用 `AgwDbContext`，不新增内存中的替身。
  - PostgreSQL 用例：`tests/Agw.Agents.Tests` 使用 `public static bool PostgresEnabled => !string.IsNullOrWhiteSpace(DurableTestConnections.PostgresConnectionString);` 与 `[Fact(SkipUnless = nameof(PostgresEnabled), Skip = "...")]`；`tests/Agw.Setup.Tests` 沿用 `DatabaseInitializationStateStoreTests` 中读取 `AGW_TEST_POSTGRES_CONNECTION_STRING` 的写法。
  - 客户端的单元测试使用 `tsx --test`，用例名是英文句子。
  - 测试运行时产生的文件保存在测试项目的输出目录或 `.scratch/` 下。
- model 与 migration：任务 1 到任务 9 修改 entity，migration 只在任务 10 生成，执行生成命令之前向用户确认。在这之间，`tests/Agw.Projects.Tests/Infrastructure/InitialMigrationTests.cs` 的 `HasPendingModelChanges` 断言与通过 `MigrateAsync` 建库的 PostgreSQL 用例反映 model 领先于 migration 的状态，它们在任务 10 的步骤中运行；任务 1 到任务 9 的验证使用 `EnsureCreatedAsync` 建库的 SQLite 用例。
- 过滤参数在本机无效时，先执行 `dotnet build tests/<项目>`，再运行 `dotnet tests/<项目>/bin/Debug/net10.0/<项目>.dll -class "<完整类名>"`。
- 修改 entity、持久化访问或 Behavior 之后运行 `dotnet test --project tests/Agw.Architecture.Tests`；修改 `ErrorCodes` 之后运行 `dotnet test --project tests/Agw.Shared.Tests`。
- 格式化只处理修改过的文件：C# 使用 `dotnet csharpier format <文件>`；客户端 package 使用 `pnpm --filter <包名> exec oxfmt -c ../../.oxfmtrc.json <文件>`（`@agw/web`、`@agw/desktop` 使用 `-c ../.oxfmtrc.json`）。客户端命令都在 `src` 目录下运行。
- 注释使用中文和英文两种语言，数量保持克制。时间使用 `DateTimeOffset` 与 `TimeProvider`。
- 颜色只使用设计文档第 20 节列出的主题变量与色阶。图标按钮都有 `aria-label`。界面文字使用设计文档中的英文原文。
- Desktop extension 集成测试（`pnpm --filter @agw/desktop test:extensions`）每次运行之前都向用户确认，并写明这次运行要回答的问题。
- 每个任务最后的提交步骤只在用户要求提交时执行，提交信息使用 Conventional Commits。不使用 Git 回退或丢弃任何修改。
- 创建子代理时，提示词要求它使用 Rider MCP 工具查找和阅读代码。
- 不修改 `docs/human/` 下的文档。工作区中与本计划无关的未提交修改保持原状。

## 复核重点

下面六种输入或状态最容易在使用中出错，每一项的测试已经加入对应任务。

1. 嵌套引用指回根 Agentflow（R 的被执行 revision 引用 B，B 已发布的 revision 引用 R）：生成快照时返回 `InvalidParam`，没有 Turn 行，展开过程会结束。测试在任务 6。
2. 受理过程中有新的 revision 被发布：extension 的选择、Turn 的 `extra` 与 Durable manifest 使用同一份快照中的 revision。测试在任务 11。
3. PostgreSQL 的序列化冲突（SqlState `40001`）发生在受理事务中：异常直接返回给调用方，没有 Turn 行，不被当作 `turnId` 冲突处理。测试在任务 11。
4. 升级前受理、manifest 中没有快照属性的 Durable 执行在升级后被领取：该 Turn 以 `DurableExecutionConflict` 结束，不按 revision 0 读取定义。测试在任务 11。
5. `result` 规则的路径指向对象、数组或 null：条件不成立；指向数字或布尔值时使用它的 JSON 文本对比。测试在任务 4。
6. 导入的文件中有节点的 `kind` 不属于 12 种节点：对话框显示该节点的 `nodeId` 与 `kind`，当前文档与 Undo 历史保持原状。测试在任务 27。

## 文件结构

设计文档"变更清单"之外，本计划新增下列文件：

| 位置 | 内容 |
| --- | --- |
| `src/server/Agw.Agents/Definitions/Domain/ValueObjects/AgentflowDefinition.cs` | 一个 revision 的节点、连线、成员，以及 `config_json`、`condition_json` 对应的选项与规则类型 |
| `src/server/Agw.Agents/Definitions/Domain/ValueObjects/AgentflowDefinitionJson.cs` | 选项与规则的 camelCase 序列化 |
| `src/server/Agw.Agents/Definitions/Agents/AgentflowDefinitionMapper.cs` | `AgentflowDefinitionDto` 与 `AgentflowDefinition` 之间的转换 |
| `src/server/Agw.Agents/Definitions/Contracts/CamelCaseEnumConverter.cs` | 接口中枚举的 camelCase 字符串转换 |
| `src/server/Agw.Agents/Application/Persistence/AgentflowRevisionSnapshot.cs`、`IAgentflowRevisionSnapshotReader.cs` | 依赖快照的类型与读取接口 |
| `src/server/Agw.Agents/Definitions/Persistence/AgentflowRevisionSnapshotReader.cs` | 按 published pointer 展开嵌套引用 |
| `src/server/Agw.Agents.Execution/Agentflows/Checkpoints/AgentflowCheckpointRuntime.cs` | 常量 `Version` |
| `src/server/Agw.Host/HostStartupInitialization.cs` | `Program.cs` 中启动初始化与系统数据维护的代码 |
| `src/clients/packages/agents/src/ui-web/pages/agentflows/components/agentflow-document.ts` | 编辑器文档类型与新节点、新连线的默认值 |

每个任务的"文件"列出它创建、修改与删除的全部文件。Server 的路径以 `src/server/` 开头，客户端 package 的路径以 `src/clients/packages/` 开头，下文在任务内省略这两个前缀时会写明。编辑器文件的目录 `src/clients/packages/agents/src/ui-web/pages/agentflows/components/` 在任务 19 到任务 27 中记为 `editor/`。

## 准备

- [ ] 在仓库根目录运行 `dotnet build Agw.slnx`、`dotnet test --project tests/Agw.Agents.Tests`、`dotnet test --project tests/Agw.Projects.Tests`、`dotnet test --project tests/Agw.Setup.Tests`、`dotnet test --project tests/Agw.Architecture.Tests`，记录基线。
- [ ] 在 `src` 目录运行 `pnpm install`、`pnpm test`、`pnpm typecheck`，记录基线。

---

### 任务 1：数据模型的新增部分、`extra` 与错误码

**文件**（前缀 `src/server/`）：

- 创建：`Agw.Data/Entities/Agentflows/AgentflowRevision.cs`、`AgentflowRevisionConfiguration.cs`、`AgentflowBlockMember.cs`、`AgentflowBlockMemberConfiguration.cs`、`AgentflowRoutingStrategy.cs`
- 创建：`Agw.Data/Conversions/ExtraJson.cs`（文件已经存在时直接使用）、`Agw.Data/Entities/Observable/AgentflowTraceExtra.cs`、`Agw.Data/Entities/Projects/ProjectConversationTurnExtra.cs`
- 修改：`Agw.Data/Entities/Agentflows/Agentflow.cs`、`AgentflowNode.cs`、`AgentflowEdge.cs` 及三个 Configuration；`Agw.Data/Entities/Executions/AgentflowCheckpointRecord.cs`；`Agw.Data/Entities/Observable/AgentflowTrace.cs`、`AgentflowTraceConfiguration.cs`；`Agw.Data/Entities/Projects/ProjectConversationTurn.cs`、`ProjectConversationTurnConfiguration.cs`
- 修改：`Agw.Agents/Application/Persistence/IAgentsDbContext.cs`、`Agw.Infrastructure/Data/AgwDbContext.cs`、`Agw.Infrastructure/Data/UserScopeModelBuilderExtensions.cs`、`Agw.Shared/Exceptions/ErrorCodes.cs`
- 修改：`tests/Agw.Architecture.Tests/BackendArchitectureTests.cs`（`EntityOwners`）
- 测试：`tests/Agw.Projects.Tests/Infrastructure/EntityTypeConfigurationTests.cs`、`tests/Agw.Agents.Tests/AgentflowRevisionModelTests.cs`

**接口：**

- 产出：
  - `enum AgentflowRoutingStrategy { Direct = 0, FanOut = 1, Switch = 2, Choice = 3 }`
  - `Agentflow`：`int LatestRevision`、`int PublishedRevision`
  - `[Table("agentflow_revision")] AgentflowRevision : BaseEntity`：`Guid AgentflowId`、`int Revision`，主键 `(AgentflowId, Revision)`
  - `AgentflowNode`：`int AgentflowRevision`、`double PositionX`、`double PositionY`、`AgentflowRoutingStrategy RoutingStrategy`、`Guid? SummaryModelProviderId`；主键 `(AgentflowId, AgentflowRevision, NodeId)`，索引 `(AgentflowId, AgentflowRevision, Kind, RelateId)`
  - `[Table("agentflow_block_member")] AgentflowBlockMember : BaseEntity`：`Guid AgentflowId`、`int AgentflowRevision`、`string NodeId`、`string MemberId`（64）、`AgentflowNodeKind Kind`、`Guid RelateId`、`string Name`（200）、`string? Instructions`（8000）、`int SortOrder`、`bool IsManager`；主键 `(AgentflowId, AgentflowRevision, NodeId, MemberId)`，索引 `(AgentflowId, AgentflowRevision, Kind, RelateId)`
  - `AgentflowEdge`：`int AgentflowRevision`、`bool IsFanInBarrier`、`bool IsSwitchDefault`、`int SwitchCaseOrder`、`string? ChoiceId`（64）；主键 `(AgentflowId, AgentflowRevision, EdgeId)`；指向源节点与目标节点的关系都加入 `AgentflowRevision`
  - `AgentflowCheckpointRecord`：`int AgentflowRevision`、`int RuntimeVersion`
  - `AgentflowTrace.Extra`（`AgentflowTraceExtra`）、`ProjectConversationTurn.Extra`（`ProjectConversationTurnExtra`），列 `extra`，text，非空，默认 `{}`
  - `IAgentsDbContext`：`DbSet<AgentflowRevision> AgentflowRevisions`、`DbSet<AgentflowBlockMember> AgentflowBlockMembers`
  - 全局约束中的五个错误码

  ```csharp
  public static class ExtraJson
  {
      /// <summary>
      /// extra 列的转换：record 与 camelCase 的 JSON 文本。
      /// Conversion for extra columns: a record and its camelCase JSON text.
      /// </summary>
      public static ValueConverter<T, string> Converter<T>() where T : class;
  }

  public sealed record AgentflowTraceExtra
  {
      public int AgentflowRevision { get; init; }
  }

  public sealed record ProjectConversationTurnExtra
  {
      public int AgentflowRevision { get; init; }

      [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
      public IReadOnlyList<ProjectConversationTurnAgentflowDependency>? AgentflowDependencies { get; init; }
  }

  public sealed record ProjectConversationTurnAgentflowDependency(
      Guid ParentAgentflowId,
      int ParentRevision,
      string ParentNodeId,
      string? BlockMemberId,
      Guid TargetAgentflowId,
      int TargetRevision
  );
  ```

- 这个任务不删除任何旧成员。新增的属性都有默认值（revision 为 0），现有的代码与测试数据继续使用 revision 0。

- [ ] **步骤 1：编写失败的测试**

  `EntityTypeConfigurationTests`：

  - `Extra_EmptyObject_ReadsRevisionZero`：用 `ExtraJson.Converter<ProjectConversationTurnExtra>()` 读取 `{}`，得到 `AgentflowRevision == 0` 与 `AgentflowDependencies == null`；写入 `new ProjectConversationTurnExtra { AgentflowRevision = 3, AgentflowDependencies = [] }` 得到 `{"agentflowRevision":3,"agentflowDependencies":[]}`，再读取后 `AgentflowDependencies` 是空数组；`AgentflowTraceExtra { AgentflowRevision = 3 }` 写入 `{"agentflowRevision":3}`。
  - `AgentflowEntities_DeclareMatchingConfigurations`：`AssertConfigured(typeof(AgentflowRevision), typeof(AgentflowBlockMember))`。
  - 带有 `[Table]` 的实体数量断言由 55 改为 57。

  `AgentflowRevisionModelTests`（SQLite，`EnsureCreatedAsync`）：

  - `SaveChanges_SameNodeIdInTwoRevisions_KeepsBothRows`：同一个 `AgentflowId`、`NodeId` 为 `input` 的节点分别以 revision 1 与 2 保存，两行都存在。
  - `SaveChanges_ExtraNotAssigned_StoresEmptyObject`：新增一行 `AgentflowTrace` 与一行 `ProjectConversationTurn`，用原始 SQL 读取 `extra`，trace 为 `{"agentflowRevision":0}`，没有经过 EF 写入的行（原始 SQL 插入并省略该列）为 `{}`。
  - `Query_BlockMemberOfAnotherUser_IsNotVisible`：他人 Agentflow 的 `AgentflowRevision` 与 `AgentflowBlockMember` 在当前用户的查询中不可见。

- [ ] **步骤 2：运行测试，确认失败**

  运行：`dotnet test --project tests/Agw.Projects.Tests --filter-class "*EntityTypeConfigurationTests"`
  预期：编译失败，`ExtraJson` 不存在。

- [ ] **步骤 3：实现**

  - `ExtraJson.Converter<T>()` 使用一个 `JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }`。两个实体的属性初始化为 `new()`，Configuration 使用 `HasConversion(ExtraJson.Converter<T>())` 与 `HasDefaultValueSql("'{}'")`。
  - `AgentflowRevision` 与 `AgentflowBlockMember` 的用户范围过滤条件与 `UserScopeModelBuilderExtensions` 中 `AgentflowNode` 的写法相同（经 `Agentflow.CreateBy` 判断）。
  - `EntityOwners` 增加 `["AgentflowRevision"] = "Agw.Agents"` 与 `["AgentflowBlockMember"] = "Agw.Agents"`。

- [ ] **步骤 4：运行测试，确认通过**

  运行：`dotnet build Agw.slnx`、`dotnet test --project tests/Agw.Projects.Tests --filter-class "*EntityTypeConfigurationTests"`、`dotnet test --project tests/Agw.Agents.Tests --filter-class "*AgentflowRevisionModelTests"`、`dotnet test --project tests/Agw.Architecture.Tests`、`dotnet test --project tests/Agw.Shared.Tests`
  预期：全部通过。再运行 `dotnet test --project tests/Agw.Agents.Tests`，结果与基线相同。

- [ ] **步骤 5：格式化并提交（用户要求时）**

  ```bash
  git add src/server/Agw.Data src/server/Agw.Agents src/server/Agw.Infrastructure src/server/Agw.Shared tests
  git commit -m "feat(agents): add revision columns, block members and extra to the data model"
  ```

---

### 任务 2：类型化的定义对象与接口类型

**文件**（前缀 `src/server/Agw.Agents/Definitions/`）：

- 创建：`Domain/ValueObjects/AgentflowDefinition.cs`、`Domain/ValueObjects/AgentflowDefinitionJson.cs`、`Contracts/AgentflowDefinitionDto.cs`、`Contracts/AgentflowProblem.cs`、`Contracts/CamelCaseEnumConverter.cs`、`Agents/AgentflowDefinitionMapper.cs`
- 测试：`tests/Agw.Agents.Tests/AgentflowDefinitionJsonTests.cs`、`tests/Agw.Agents.Tests/AgentflowDefinitionMapperTests.cs`

**接口：**

- 产出（任务 3 到任务 9 使用）：

  ```csharp
  // AgentflowDefinition.cs
  public sealed record AgentflowDefinition(
      IReadOnlyList<AgentflowNode> Nodes,
      IReadOnlyList<AgentflowEdge> Edges,
      IReadOnlyList<AgentflowBlockMember> Members
  );
  public sealed record AgentflowConditionRule(string Field, string Value, string Path);
  public sealed record AgentflowGateChoice(string ChoiceId, string Label);
  public sealed record HumanGateOptions(string? HumanPrompt, IReadOnlyList<AgentflowGateChoice> Choices, bool AllowResponseText);
  public sealed record CheckpointMarkerOptions(string? CheckpointName);
  public sealed record OutputOptions(bool EnableSummary);
  public sealed record HandoffBlockOptions(string? HandoffInstructions, bool? EnableReturnToPrevious, bool? Autonomous, int? AutonomousTurnLimit, string? ContinuationPrompt);
  public sealed record GroupChatBlockOptions(int? MaxRounds);
  public sealed record MagenticBlockOptions(int? MaxRounds, int? MaxStalls, int? MaxResets, bool? RequirePlanSignoff);

  // AgentflowDefinitionJson.cs
  public static class AgentflowDefinitionJson
  {
      public const string EmptyOptions = "{}";
      public const string EmptyConditions = "[]";
      public static T ReadOptions<T>(AgentflowNode node) where T : class;
      public static string WriteOptions<T>(T options) where T : class;
      public static IReadOnlyList<AgentflowConditionRule> ReadConditions(AgentflowEdge edge);
      public static string WriteConditions(IReadOnlyList<AgentflowConditionRule> rules);
  }

  // AgentflowProblem.cs：设计文档第 3 节的 record 与注释
  // AgentflowDefinitionMapper.cs
  public static class AgentflowDefinitionMapper
  {
      public static AgentflowDefinition ToDefinition(AgentflowDefinitionDto dto);
      public static AgentflowDefinitionDto ToDto(AgentflowDefinition definition);
  }
  ```

  - Block 选项的属性类型与 `AgentflowBlockBuildContext.cs` 中 `AgentflowBlockConfig` 的同名属性相同；上面写的是可空的形式，与现有类型不一致时以现有类型为准。
  - `AgentflowDefinitionDto(IReadOnlyList<AgentflowNodeDto> Nodes, IReadOnlyList<AgentflowEdgeDto> Edges)`。
  - `AgentflowNodeDto` 是抽象类型，带有 `[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]`，属性为 `NodeId`、`Name`、`Position`（`AgentflowPositionDto(double X, double Y)`）、`RoutingStrategy`。12 个子类型的鉴别值与字段见设计文档第 1 节的表格：`input`、`agent`、`workflowAsAgent`、`promptAdapter`、`clearMessages`、`humanGate`、`checkpointMarker`、`output`、`concurrentBlock`、`handoffBlock`、`groupChatBlock`、`magenticBlock`，类型名为 `Agentflow{Kind}NodeDto`。
  - `AgentflowBlockMemberDto(string MemberId, AgentflowNodeKind Kind, Guid? RelateId, string Name, string? Instructions, bool IsManager)`、`AgentflowGateChoiceDto(string ChoiceId, string Label)`、`AgentflowConditionRuleDto(string Field, string Value, string Path)`。
  - `AgentflowEdgeDto(string EdgeId, string SourceNodeId, string TargetNodeId, bool IsFanInBarrier, bool IsSwitchDefault, int SwitchCaseOrder, string? ChoiceId, string? Label, IReadOnlyList<AgentflowConditionRuleDto> Conditions)`。
  - `CamelCaseEnumConverter<TEnum> : JsonStringEnumConverter<TEnum>`，无参构造函数调用 `base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)`；用在 DTO 的 `RoutingStrategy` 与成员的 `Kind` 上。
  - `ToDefinition` 生成的实体中 `AgentflowId` 为 `Guid.Empty`、`AgentflowRevision` 为 0，由任务 7 的保存流程赋值。成员的 `SortOrder` 是数组下标。`relateId` 为 null 的成员保留为 `Guid.Empty`，由任务 3 的 `reference-missing` 报告。

- [ ] **步骤 1：编写失败的测试**

  `AgentflowDefinitionJsonTests`：

  - `WriteOptions_HumanGate_UsesCamelCaseKeys`：`HumanGateOptions("Review", [new("choice-1", "Approve")], true)` 写入 `{"humanPrompt":"Review","choices":[{"choiceId":"choice-1","label":"Approve"}],"allowResponseText":true}`，读取后相等。
  - `ReadConditions_EmptyArray_ReturnsNoRules`：`[]` 得到空列表。
  - `WriteConditions_ResultRule_KeepsPath`：`new AgentflowConditionRule("result", "approved", "review.status")` 往返后相等。

  `AgentflowDefinitionMapperTests`：

  - `ToDefinition_EveryNodeKind_RoundTripsThroughToDto`：用 `JsonSerializer`（`JsonSerializerDefaults.Web`）读取包含 12 种节点、一个带两名成员的 Magentic Team、四种连线（`conditions` 非空、`isSwitchDefault`、`choiceId`、`isFanInBarrier`）的 JSON，`ToDto(ToDefinition(dto))` 序列化后与输入的 JSON 相同（`JsonNode.DeepEquals`）。
  - `ToDefinition_OutputNode_StoresSummaryModelOnColumn`：`summaryModelProviderId` 写入 `AgentflowNode.SummaryModelProviderId`，`ConfigJson` 为 `{"enableSummary":true}`。
  - `ToDefinition_NodeWithoutOptions_StoresEmptyObject`：`agent` 节点的 `ConfigJson` 为 `{}`，`RoutingStrategy` 为 `Direct`。
  - `Deserialize_UnknownKind_Throws`：`kind` 为 `unknown` 时 `JsonSerializer.Deserialize` 抛出 `JsonException`。

- [ ] **步骤 2：运行测试，确认失败**

  运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*AgentflowDefinitionJsonTests" --filter-class "*AgentflowDefinitionMapperTests"`
  预期：编译失败，类型不存在。

- [ ] **步骤 3：实现**

  `AgentflowDefinitionJson` 使用 `JsonNamingPolicy.CamelCase`。没有选项的种类写入 `EmptyOptions`，没有条件的连线写入 `EmptyConditions`。

- [ ] **步骤 4：运行测试，确认通过**

  运行步骤 2 的命令。预期：全部通过。

- [ ] **步骤 5：格式化并提交（用户要求时）**

  ```bash
  git add src/server/Agw.Agents/Definitions tests/Agw.Agents.Tests
  git commit -m "feat(agents): add typed Agentflow definition objects and DTOs"
  ```

---

### 任务 3：校验

**文件**（前缀 `src/server/Agw.Agents/Definitions/`）：

- 修改：`Domain/Behaviors/AgentflowBehavior.cs`、`Domain/Services/AgentflowDefinitionDomainService.cs`、`Domain/Repositories/IAgentDefinitionRepository.cs`、`Persistence/AgentDefinitionRepository.cs`
- 测试：`tests/Agw.Agents.Tests/AgentflowBehaviorValidateTests.cs`、`tests/Agw.Agents.Tests/AgentflowDefinitionValidationTests.cs`

**接口：**

- 使用（任务 2）：`AgentflowDefinition`、`AgentflowDefinitionJson`、`AgentflowProblem`。
- 产出（任务 7 使用）：

  ```csharp
  // AgentflowBehavior
  public IReadOnlyList<AgentflowProblem> Validate(string name, AgentflowDefinition definition);

  // AgentflowDefinitionDomainService
  public Task<IReadOnlyList<AgentflowProblem>> ValidateAsync(
      Guid? agentflowId,
      string name,
      AgentflowDefinition definition,
      CancellationToken cancellationToken
  );

  // IAgentDefinitionRepository
  Task<IReadOnlyDictionary<Guid, int>> ListOwnedAgentflowPublishedRevisionsAsync(IReadOnlyCollection<Guid> agentflowIds, CancellationToken cancellationToken);
  Task<IReadOnlyDictionary<Guid, IReadOnlyCollection<Guid>>> LoadPublishedAgentflowReferencesAsync(IReadOnlyCollection<Guid> agentflowIds, CancellationToken cancellationToken);
  Task<IReadOnlySet<Guid>> FilterStructuredResultAgentIdsAsync(IReadOnlyCollection<Guid> agentIds, CancellationToken cancellationToken);
  ```

  - `ValidateAsync` 的结果是 `Validate` 的结果加上设计文档第 3 节表格中"实现位置"为 DomainService 的五条规则，顺序为 Behavior 在前。
  - `LoadPublishedAgentflowReferencesAsync` 按层读取各 Agentflow 已发布 revision 中的 `WorkflowAsAgent` 节点与成员。`FilterStructuredResultAgentIdsAsync` 返回 `EnableSummary` 为 true 并且 `ResponseSchema` 不为空的 Agent。
  - 成员的问题定位到所属的 Block 节点。
  - `message` 的写法（`{node}`、`{source}`、`{target}` 是节点名称）：

  | `code` | `message` |
  | --- | --- |
  | `name-required` | `The Agentflow needs a name of at most 200 characters.` |
  | `input-invalid` | `The flow needs exactly one Input node with no incoming connections.` |
  | `duplicate-id` | `{id} is used more than once.` |
  | `runner-required` | `Add an Agent, a Workflow, or a Block with members.` |
  | `node-unreachable` | `{node} is not reachable from Input.` |
  | `edge-endpoint-missing` | `A connection points to a node that does not exist.` |
  | `routing-invalid` | `{node} cannot hand off this way.` |
  | `switch-case-required` | `{node} needs at least one branch with a condition.` |
  | `switch-condition-required` | `The branch from {source} to {target} needs a condition.` |
  | `switch-default-duplicate` | `{node} has more than one Else branch.` |
  | `switch-order-duplicate` | `{node} has two branches in the same position.` |
  | `condition-invalid` | `The connection from {source} to {target} has a condition that cannot be used.` |
  | `choice-required` | `{node} needs at least one choice, and every choice needs a label.` |
  | `edge-choice-invalid` | `The connection from {source} to {target} is not bound to a choice of {source}.` |
  | `block-member-count` | `{node} needs at least one member.`（Concurrent Block）、`{node} needs at least two members.`（其他 Block） |
  | `block-manager-invalid` | `{node} needs exactly one manager.` |
  | `cycle-exit-required` | `The loop through {node} needs a branch or a choice that leaves it.` |
  | `cycle-barrier-invalid` | `{target} is in a loop and cannot wait for {source}.` |
  | `summary-output-count` | `Only one Output can exist when a summary is written.` |
  | `reference-missing` | `{node} has no agent or workflow selected, or it is no longer available.` |
  | `nested-unpublished` | `{node} uses a workflow that is not published.` |
  | `nested-cycle` | `{node} makes this Agentflow contain itself.` |
  | `summary-model-missing` | `{node} needs a summary model.` |
  | `condition-result-unavailable` | `The connection from {source} to {target} reads a Result, but {source} does not produce one.` |

- [ ] **步骤 1：编写失败的测试**

  `AgentflowBehaviorValidateTests`（纯内存，定义由 JSON 经 `AgentflowDefinitionMapper.ToDefinition` 得到）：

  - `Validate_EachRule_ReportsProblemWithCodeAndTarget`：`[Theory]`，表格中"实现位置"为 Behavior 的 19 个 `code` 各一行数据，每行给出只违反这一条规则的定义，断言结果只有一项，`Code`、`NodeId` 或 `EdgeId` 与预期相同，`Message` 与上表相同。
  - `Validate_TwoIndependentProblems_ReportsBoth`：一个不可到达的节点加一条缺少条件的 `switch` 分支，结果恰好两项。
  - `Validate_ValidDefinition_ReportsNothing`：设计文档第 19 节的五个模板结构（Agent 都有 `relateId`）各自没有 Problem。
  - `Validate_CycleLeavingThroughChoice_ReportsNothing`：环经 Human Gate 的 choice 离开。

  `AgentflowDefinitionValidationTests`（SQLite，`AgentDefinitionRepository` 与真正的 `AgentflowDefinitionDomainService`）：

  - `ValidateAsync_EachCrossAggregateRule_ReportsProblem`：`reference-missing`（`relateId` 为空、Agent 属于他人、成员引用的 Agentflow 不存在各一例）、`nested-unpublished`、`nested-cycle`（A 引用 B，B 已发布的 revision 引用 A）、`summary-model-missing`、`condition-result-unavailable`。
  - `ValidateAsync_ResultRuleOnStructuredAgent_ReportsNothing`。
  - `ValidateAsync_AgentflowIdNull_ValidatesWithoutSelfReference`。

- [ ] **步骤 2：运行测试，确认失败**

  运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*AgentflowBehaviorValidateTests" --filter-class "*AgentflowDefinitionValidationTests"`
  预期：编译失败，`Validate` 不存在。

- [ ] **步骤 3：实现**

  - 环的检查沿用 `AgentflowTopology.FindCyclicComponents`；可到达性从 `input` 出发沿全部连线计算。
  - `condition-invalid` 支持的 `field`：`contains`、`notContains`、`equals`、`author`、`role`、`minMessages`、`result`；`result` 的 `path` 不能为空，其余 `field` 的 `path` 必须为空字符串。
  - 现有的 `TryReplaceGraph`、`IsValidGraph` 与 `TryDefineGraphAsync` 在这个任务中保持原样。

- [ ] **步骤 4：运行测试，确认通过**

  运行步骤 2 的命令与 `dotnet test --project tests/Agw.Architecture.Tests`。预期：全部通过。

- [ ] **步骤 5：格式化并提交（用户要求时）**

  ```bash
  git add src/server/Agw.Agents/Definitions tests/Agw.Agents.Tests
  git commit -m "feat(agents): validate Agentflow definitions into problems"
  ```

---

### 任务 4：编译：分支方式、条件与成员

**文件**（前缀 `src/server/Agw.Agents.Execution/Agentflows/`）：

- 修改：`Workflows/AgentflowWorkflowCompiler.cs`、`Workflows/AgentflowWorkflowMetadata.cs`、`Workflows/Builders/AgentflowBlockBuildContext.cs`、`AgentflowBlockBuildSupport.cs`、`ConcurrentBlockBuilder.cs`、`HandoffBlockBuilder.cs`、`GroupChatBlockBuilder.cs`、`MagenticBlockBuilder.cs`、`Runtime/AgentflowRuntimeFactory.cs`、`Workflows/AgentflowMermaidProvider.cs`
- 测试：`tests/Agw.Agents.Tests/AgentflowWorkflowCompilerTests.cs`、`AgentflowNodeOrderingTests.cs`、`AgentflowWorkflowMetadataTests.cs`，以及使用 `AgentflowEdgeKind` 或 `participantNodeIds` 构造数据的其他执行测试（`git grep -n -w -E "AgentflowEdgeKind|participantNodeIds" -- tests` 列出）

**接口：**

- 使用（任务 1、2）：`AgentflowRoutingStrategy`、连线的四个字段、`AgentflowBlockMember`、`AgentflowDefinitionJson`、各选项类型。
- 产出（任务 5、6、13、15 使用）：

  ```csharp
  internal sealed record AgentflowCompilation(Workflow Workflow, IReadOnlyDictionary<string, string> NodeIdsByExecutorId);

  // AgentflowWorkflowCompiler：两个 Compile 都在 agentflow 之后增加 agentflowRevision，在 edges 之后增加 members，返回 AgentflowCompilation?
  internal AgentflowCompilation? Compile(
      Agentflow agentflow,
      int agentflowRevision,
      IReadOnlyList<AgentflowNode> orderedNodes,
      IReadOnlyList<AgentflowEdge> edges,
      IReadOnlyList<AgentflowBlockMember> members,
      IReadOnlyDictionary<string, AIAgent> nodeIdToAgent,
      AgentflowAgentSessionScope? sessionScope,
      /* 其余可选参数的名称、类型与顺序保持现状 */
  );

  private static Func<List<ChatMessage>?, bool>? BuildCondition(IReadOnlyList<AgentflowConditionRule> rules);
  ```

  - `AgentflowWorkflowMetadata` 增加 `IReadOnlyDictionary<string, string> NodeIdsByExecutorId`。
  - `AgentflowBlockBuildContext`：构造函数的 `nodeMap` 改为 `IReadOnlyList<AgentflowBlockMember> members`（该 Block 的成员，按 `SortOrder`），并增加 `int agentflowRevision`；`AgentflowBlockConfig` 删除，各 builder 用 `AgentflowDefinitionJson.ReadOptions<T>(blockNode)` 读取选项。
  - `nodeIdToAgent` 中成员的键是 `{blockNodeId}.{memberId}`，Manager 的键沿用现有的后缀。
  - 编译规则是设计文档第 1 节"编译"的表格与第 5 节的规则表格。`choice` 源节点在这个任务中编译为没有谓词的 `AddEdge`，任务 5 加入 choice 的谓词。
  - `result` 规则：在消息中从后向前找到 `AdditionalProperties` 的 `type` 为 `AgwMessageTypes.Result` 并且 `AgwMessageClassifier.ResultFormatKey` 为 `json` 的消息，把它的文本解析为 JSON，沿 `path`（`.` 分隔）读取属性。读取到字符串时取内容，数字与布尔值取 JSON 文本，与 `value` 忽略大小写对比；对象、数组、null、路径不存在或没有这样的消息时不成立。

- [ ] **步骤 1：编写失败的测试**

  `AgentflowWorkflowCompilerTests` 的数据 helper 改为新的字段（节点的 `RoutingStrategy`，连线的 `IsFanInBarrier`、`IsSwitchDefault`、`SwitchCaseOrder`、`ChoiceId`、`ConditionJson` 为规则数组，成员为 `AgentflowBlockMember`），现有用例的断言保持原样。新增：

  - `Compile_EachRoutingStrategy_BuildsExpectedEdges`：`direct` 带条件的连线只在条件成立时执行；`fanOut` 的两个目标都执行，带条件的目标在条件不成立时不执行；`switch` 按 `SwitchCaseOrder` 取第一个成立的分支，都不成立时执行 `IsSwitchDefault` 的连线；等待边的目标在全部源节点完成后执行一次。
  - `Compile_EveryNodeKind_MapsPrimaryExecutorToNodeId`：包含 12 种节点的定义，`NodeIdsByExecutorId` 的值集合等于全部 `NodeId`，键中没有以 `.output` 结尾的 executor。
  - `Compile_BlockMembers_CreatesParticipantsAndManagerFromMemberRows`：Magentic Team 的三名成员中 `IsManager` 的一名成为 Manager，其余按 `SortOrder` 成为参与者。
  - `BuildCondition_ResultRule_ComparesValueAtPath`：`[Theory]`，Result 为 `{"review":{"status":"Approved","score":7,"ok":true,"tags":["a"],"note":null}}`。`review.status` 对 `approved` 成立；`review.score` 对 `7` 成立；`review.ok` 对 `true` 成立；`review.tags`、`review.note`、`review.missing` 不成立；消息中没有 json Result 时不成立；有两条 Result 时使用后一条。
  - `BuildCondition_TextRules_MatchExistingSemantics`：`contains`、`notContains`、`equals`、`author`、`role`、`minMessages` 各一组成立与不成立的数据；两条规则同时存在时都成立才成立。

- [ ] **步骤 2：运行测试，确认失败**

  运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*AgentflowWorkflowCompilerTests"`
  预期：编译失败，`Compile` 的参数不匹配。

- [ ] **步骤 3：实现**

  - `AgentflowWorkflowCompiler` 中读取 `edge.Kind`、`edge.ConfigJson`、`AgentflowConfigurationParser` 的位置全部改为读取源节点的 `RoutingStrategy` 与连线的字段。
  - `AgentflowRuntimeFactory.CreateAiWorkflow` 与 `AgentflowMermaidProvider` 调用新的 `Compile`：`agentflowRevision` 传入 0，`members` 传入空数组（任务 6 改为按 revision 读取），并把 `NodeIdsByExecutorId` 放入 `AgentflowWorkflowMetadata`。
  - `AgentflowRuntimeFactory` 为成员创建 Agent 时使用成员行的 `Kind`、`RelateId`、`Name`、`Instructions`。

- [ ] **步骤 4：运行测试，确认通过**

  运行：`dotnet test --project tests/Agw.Agents.Tests`
  预期：全部通过。

- [ ] **步骤 5：格式化并提交（用户要求时）**

  ```bash
  git add src/server/Agw.Agents.Execution tests/Agw.Agents.Tests
  git commit -m "feat(agents): compile routing strategies, condition rules and block members"
  ```

---

### 任务 5：Human Gate 的 choices

**文件**（前缀 `src/server/`）：

- 修改：`Agw.Agents.Contracts/Execution/HumanInteraction.cs`、`Agw.Agents.Execution/HumanInteraction/Application/InteractionRules.cs`、`Agw.Agents.Execution/Agentflows/Messaging/AgentflowMessageMapper.cs`、`Agentflows/Workflows/AgentflowWorkflowMetadata.cs`、`Agentflows/Workflows/AgentflowWorkflowCompiler.cs`、`Agentflows/Turns/AgentflowTurnExecutor.cs`
- 测试：`tests/Agw.Agents.Tests/InteractionRulesTests.cs`、`AgentflowMessageMapperTests.cs`、`AgentflowTurnExecutorTests.cs`、`InteractionTestData.cs`，以及构造 `WorkflowGateInteraction` 或 `WorkflowGateDecision` 的其他测试（`git grep -n -w -E "WorkflowGateInteraction|WorkflowGateDecision|humanMode" -- tests` 列出）

**接口：**

- 产出（任务 15 与客户端任务 18 使用）：

  ```csharp
  public sealed record WorkflowGateChoice(string ChoiceId, string Label);

  public sealed record WorkflowGateInteraction : InteractionRequest
  {
      public IReadOnlyList<WorkflowGateChoice> Choices { get; init; } = [];
      public bool AllowResponseText { get; init; }
      public string? InputPreview { get; init; }
  }

  public sealed record WorkflowGateDecision : InteractionResponse
  {
      public required bool Approved { get; init; }
      public string? ChoiceId { get; init; }
      public string? ResponseText { get; init; }
  }

  internal sealed record AgentflowHumanGateNode(string NodeId, string? Name, HumanGateOptions Options);

  // AgentflowMessageMapper
  internal static List<ChatMessage> CreateHumanGateResponseMessages(
      IReadOnlyList<ChatMessage> messages,
      WorkflowGateInteraction request,
      WorkflowGateDecision decision
  );
  ```

  - `InteractionRules.ValidateAndNormalize`：`Approved` 为 true 时 `ChoiceId` 必须是 `request.Choices` 中的一个，否则抛出 `AgwException(ErrorCodes.InvalidParam, "choiceId is not a choice of this request.")`；`request.AllowResponseText` 为 false 时返回的 `ResponseText` 为 null。`Decline` 保持 `Approved = false`。
  - 人工回复消息：文本为 `ResponseText`，为空时为所选 choice 的 `Label`；`AuthorName` 为 `human`；`AdditionalProperties["choiceId"]` 为所选的 `ChoiceId`。
  - Human Gate 的每条非等待出边编译为带谓词的边：最近一条 `AuthorName` 为 `human` 的消息的 `choiceId` 等于连线的 `ChoiceId`。
  - `InteractionTestData.Decision` 增加 `choiceId` 参数。

- [ ] **步骤 1：编写失败的测试**

  - `InteractionRulesTests.ValidateAndNormalize_GateChoiceNotInRequest_ThrowsInvalidParam`：请求的 choices 为 `choice-1`、`choice-2`，回答 `choice-3` 抛出 `InvalidParam`；`Approved` 为 false 并且没有 `ChoiceId` 时通过。
  - `InteractionRulesTests.ValidateAndNormalize_ResponseTextNotAllowed_DropsResponseText`。
  - `AgentflowMessageMapperTests.CreateHumanGateResponseMessages_EmptyResponseText_UsesChoiceLabel`：追加的消息文本为 `Approve`，`choiceId` 为 `choice-1`。
  - `AgentflowTurnExecutorTests.RunAsync_HumanGateChoice_FollowsEdgesBoundToChoice`：Human Gate 有 `choice-1`（连接 A 与 B 两个节点）与 `choice-2`（连接 C）；回答 `choice-1` 后 A、B 有输出，C 没有输出。
  - `AgentflowTurnExecutorTests.RunAsync_HumanGateChoiceWithoutEdge_EndsThatPath`：回答没有连线的 choice，Turn 正常结束，下游节点没有输出。
  - `AgentflowTurnExecutorTests.RunAsync_HumanGateNotApproved_StopsWorkflow`：输出中有 `human-gate-rejected`，下游节点没有输出。

- [ ] **步骤 2：运行测试，确认失败**

  运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*InteractionRulesTests" --filter-class "*AgentflowMessageMapperTests"`
  预期：编译失败，`Choices` 不存在。

- [ ] **步骤 3：实现**

  `AgentflowMessageMapper.CreateWorkflowGateInteraction` 从 `node.Options` 填入 `Choices` 与 `AllowResponseText`。`AgentflowTurnExecutor.SendResponseAsync` 把请求传给 `CreateHumanGateResponseMessages`。`BuildCondition` 的 `useLatestHumanReply` 参数在任务 4 中已经不存在，这里只增加 choice 的谓词。

- [ ] **步骤 4：运行测试，确认通过**

  运行：`dotnet test --project tests/Agw.Agents.Tests`
  预期：全部通过。

- [ ] **步骤 5：格式化并提交（用户要求时）**

  ```bash
  git add src/server/Agw.Agents.Contracts src/server/Agw.Agents.Execution tests/Agw.Agents.Tests
  git commit -m "feat(agents): route Human Gate answers by choice"
  ```

---

### 任务 6：revision 快照与按 revision 读取定义

**文件**（前缀 `src/server/`）：

- 创建：`Agw.Agents/Application/Persistence/AgentflowRevisionSnapshot.cs`、`IAgentflowRevisionSnapshotReader.cs`、`Agw.Agents/Definitions/Persistence/AgentflowRevisionSnapshotReader.cs`
- 修改：`Agw.Agents/Application/Persistence/IAgentflowDefinitionReader.cs`、`Agw.Agents/Definitions/Persistence/AgentflowDefinitionReader.cs`、`Agw.Agents/DependencyInjection.cs`、`Agw.Agents/Definitions/Controllers/AgentflowsController.cs`
- 修改：`Agw.Agents.Execution/Agentflows/Runtime/AgentflowRuntimeFactory.cs`、`Agentflows/Workflows/AgentflowMermaidProvider.cs` 与 `IAgentflowMermaidProvider` 所在的文件、`Agentflows/Turns/AgentflowTurnExecutor.cs`、`Turns/TurnInput.cs`、`Turns/TurnAcceptanceService.cs`、`Agents/Composition/ExtensionExecutionSelectionResolver.cs`
- 测试：`tests/Agw.Agents.Tests/AgentflowRevisionSnapshotReaderTests.cs`、`AgentflowRuntimeFactoryTests.cs`、`AgentflowMermaidProviderTests.cs`、`AgentflowTurnExecutorTests.cs`（其中的 `TestAgentflowDefinitionReader`）

**接口：**

- 产出（任务 8、11、12、13 使用）：

  ```csharp
  // AgentflowRevisionSnapshot.cs
  public readonly record struct AgentflowDependencyRevision(
      Guid ParentAgentflowId,
      int ParentRevision,
      string ParentNodeId,
      string? BlockMemberId,
      Guid TargetAgentflowId,
      int TargetRevision
  );

  public sealed record AgentflowRevisionSnapshot(int RootRevision, IReadOnlyList<AgentflowDependencyRevision> Dependencies)
  {
      /// <summary>
      /// 一条引用固定的目标 revision；快照中没有这条引用时抛出 DurableExecutionConflict。
      /// The target revision pinned for one reference; throws DurableExecutionConflict when the snapshot has no such reference.
      /// </summary>
      public int GetTargetRevision(Guid parentAgentflowId, int parentRevision, string parentNodeId, string? blockMemberId);
  }

  // IAgentflowRevisionSnapshotReader.cs
  public interface IAgentflowRevisionSnapshotReader
  {
      Task<AgentflowRevisionSnapshot> CreateRevisionSnapshotAsync(
          Guid agentflowId,
          int requestedRevision,
          string ownerUserId,
          CancellationToken cancellationToken = default
      );
  }

  // IAgentflowDefinitionReader：FindVisibleAsync 保持现状
  Task<IReadOnlyList<AgentflowNode>> ListNodesAsync(Guid agentflowId, int agentflowRevision, CancellationToken cancellationToken = default);
  Task<IReadOnlyList<AgentflowEdge>> ListEdgesAsync(Guid agentflowId, int agentflowRevision, CancellationToken cancellationToken = default);
  Task<IReadOnlyList<AgentflowBlockMember>> ListBlockMembersAsync(Guid agentflowId, int agentflowRevision, CancellationToken cancellationToken = default);
  ```

  - `CreateRevisionSnapshotAsync` 的规则：
    - 根 Agentflow 对 `ownerUserId` 不可见：`ResourceNotFound`。
    - `requestedRevision` 小于 0：`InvalidParam`；等于 0 时取 `PublishedRevision`，它为 0 时 `4090039`；大于 0 并且 `agentflow_revision` 中没有这一行：`4040018`。
    - 从根出发展开每个 revision 中 `WorkflowAsAgent` 的节点与成员。目标的 revision 取它的 `PublishedRevision`，为 0 时 `4090039`，目标不可见时 `ResourceNotFound`。同一个目标在一次快照中只读取一次 published pointer；根的 id 固定为根 revision。
    - 每一条引用记录一项 `AgentflowDependencyRevision`（节点引用的 `BlockMemberId` 为 null）。
    - 展开路径上再次出现同一个 Agentflow 时抛出 `AgwException(ErrorCodes.InvalidParam, $"Nested Agentflow cycle at '{agentflowId}'.")`。
  - `AgentflowRevisionSnapshotReader` 使用 `IAgentsDbContext`，注册为 scoped，与调用方共用同一个 `AgwDbContext` 及其事务。
  - `TurnInput` 增加 `public AgentflowRevisionSnapshot? AgentflowSnapshot { get; init; }`。
  - `AgentflowRuntimeFactory`：
    - `PrepareTurnAsync(AgentflowRuntime runtime, ExecutionScope scope, AgentflowRevisionSnapshot? snapshot, CancellationToken cancellationToken)`；`AgentflowTurnResources` 增加 `AgentflowRevisionSnapshot Snapshot`。
    - 两个 `CreateAiWorkflow` 都在 `agentflow`（或 `agentflowId`）之后增加 `int agentflowRevision` 与 `AgentflowRevisionSnapshot snapshot`。嵌套的节点与成员用 `snapshot.GetTargetRevision(...)` 得到目标 revision，再用三个 `List*Async` 读取，编译时把 revision 与成员传给 `Compile`。
    - `AgentflowSummaryContext` 的 model provider 取 Output 节点的 `SummaryModelProviderId`。
    - `GetMermaidAsync(Guid agentflowId, int agentflowRevision, CancellationToken cancellationToken = default)`，`IAgentflowMermaidProvider` 同步修改；图中嵌套的 Agentflow 读取它的 `PublishedRevision`。
  - `IExtensionExecutionSelectionResolver.ResolveAsync(ExecutionTarget target, AgentflowRevisionSnapshot? snapshot, ProjectRuntimeSnapshot project, CancellationToken cancellationToken)`：Agentflow 目标按快照读取根与各依赖目标的节点和成员，成员中的 Agent 与节点中的 Agent 同样处理。
  - 这个任务中的过渡写法，由任务 11 删除：`PrepareTurnAsync` 在 `snapshot` 为 null 时调用 `CreateRevisionSnapshotAsync(runtime.AgentflowId, 0, 当前用户)`；`TurnAcceptanceService` 在调用 resolver 之前为 Agentflow 目标调用同一个方法；`AgentflowsController` 的 Mermaid action 传入 `PublishedRevision`。

- [ ] **步骤 1：编写失败的测试**

  `AgentflowRevisionSnapshotReaderTests`（SQLite，直接写入 `agentflow`、`agentflow_revision`、节点与成员的行）：

  - `CreateRevisionSnapshotAsync_RepeatedReferencesToSameTarget_UsesOneTargetRevisionAndRecordsEachReference`：根的两个节点与一个成员都引用 B（已发布 2），结果有三项依赖，`TargetRevision` 都是 2，`ParentNodeId` 与 `BlockMemberId` 各不相同。
  - `CreateRevisionSnapshotAsync_TransitiveWorkflowReferences_RecordsFullClosure`：根 → B（已发布 2）→ C（已发布 4），结果包含 `(根, B, 2)` 与 `(B 的 revision 2, C, 4)` 两条边。
  - `CreateRevisionSnapshotAsync_RootRevisionRules_ThrowExpectedErrors`：`[Theory]`，`-1` → `InvalidParam`；`0` 并且未发布 → `4090039`；`7` 不存在 → `4040018`；他人的 id → `ResourceNotFound`；嵌套目标未发布 → `4090039`。
  - `CreateRevisionSnapshotAsync_ExplicitRevision_UsesItForRoot`：已发布 1、请求 2，`RootRevision` 为 2，读取的是 revision 2 的节点。
  - `CreateRevisionSnapshotAsync_NestedReferenceBackToRoot_ThrowsInvalidParam`：根 R 的 revision 2 引用 B，B 已发布的 revision 引用 R，请求 R 的 revision 2 时抛出 `InvalidParam`。

  `AgentflowRuntimeFactoryTests`：

  - `CreateAiWorkflow_NestedAgentflow_ReadsRevisionFromSnapshot`：生成快照之后发布嵌套 Agentflow 的 revision 2，编译得到的嵌套节点来自 revision 1。
  - `CreateAiWorkflow_OutputSummary_UsesOutputNodeModelProvider`。

  `AgentflowMermaidProviderTests.GetMermaidAsync_ExplicitRevision_RendersThatRevision`：revision 1 与 2 的节点名称不同，请求 1 得到 revision 1 的名称。

- [ ] **步骤 2：运行测试，确认失败**

  运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*AgentflowRevisionSnapshotReaderTests"`
  预期：编译失败，类型不存在。

- [ ] **步骤 3：实现**

  现有执行测试的数据改为 revision 1：节点、连线、成员的 `AgentflowRevision` 为 1，`Agentflow.LatestRevision` 与 `PublishedRevision` 为 1，并增加对应的 `AgentflowRevision` 行；`TestAgentflowDefinitionReader` 实现新的三个方法并按 revision 过滤。

- [ ] **步骤 4：运行测试，确认通过**

  运行：`dotnet test --project tests/Agw.Agents.Tests`
  预期：全部通过。

- [ ] **步骤 5：格式化并提交（用户要求时）**

  ```bash
  git add src/server/Agw.Agents src/server/Agw.Agents.Execution tests/Agw.Agents.Tests
  git commit -m "feat(agents): read Agentflow definitions by pinned revisions"
  ```

---

### 任务 7：revision 的保存、发布与读取接口

**文件**（前缀 `src/server/`）：

- 修改：`Agw.Agents/Definitions/Contracts/AgentflowRequests.cs`、`Controllers/AgentflowsController.cs`、`Agents/AgentflowAppService.cs`、`Domain/Behaviors/AgentflowBehavior.cs`、`Domain/Services/AgentflowDefinitionDomainService.cs`、`Facades/AgentCatalogFacade.cs`、`Agw.Infrastructure/Agents/AgentDeletionCoordinator.cs`
- 删除：`Agw.Agents/Definitions/Agents/AgentflowConfigurationParser.cs`、`Domain/ValueObjects/AgentflowConfigurationFacts.cs`、`tests/Agw.Agents.Tests/AgentflowBehaviorGraphTests.cs`
- 测试：`tests/Agw.Agents.Tests/AgentflowAppServiceTests.cs`、`AgentflowConcurrentDefinitionTests.cs`、`AgentflowBehaviorTests.cs`、`AgentflowDefinitionDomainServiceTests.cs`

**接口：**

- 使用（任务 2、3、6）：`AgentflowDefinition`、`AgentflowDefinitionMapper`、`ValidateAsync`、`GetMermaidAsync(agentflowId, agentflowRevision, …)`。
- 产出（客户端任务 19 使用接口；任务 9、12 使用 AppService）：

  ```csharp
  // AgentflowRequests.cs
  public sealed record AgentflowDto(Guid Id, string Name, string? Description, bool Enable, int LatestRevision, int PublishedRevision, DateTimeOffset CreateTime, DateTimeOffset UpdateTime);
  public sealed record AgentflowCreateRequest(string Name, string? Description, AgentflowDefinitionDto Definition);
  public sealed record AgentflowSaveRevisionRequest(Guid AgentflowId, int BaseRevision, string Name, string? Description, AgentflowDefinitionDto Definition);
  public sealed record AgentflowPublishRequest(Guid AgentflowId, int Revision);
  public sealed record AgentflowValidateRequest(Guid? AgentflowId, string Name, AgentflowDefinitionDto Definition);
  public sealed record AgentflowValidateResponse(IReadOnlyList<AgentflowProblem> Problems);
  public sealed record AgentflowDetailResponse(AgentflowDto Agentflow, int Revision, AgentflowDefinitionDto Definition);
  public sealed record AgentflowRevisionDto(int Revision, DateTimeOffset CreateTime);

  // AgentflowBehavior
  public void StartRevisions();                       // LatestRevision = 1，PublishedRevision = 0
  public bool TryAdvanceRevision(int baseRevision);   // baseRevision 等于 LatestRevision 时加 1
  public void Publish(int revision);                  // PublishedRevision = revision

  // AgentflowAppService
  public sealed record AgentflowDetail(Agentflow Agentflow, int Revision, AgentflowDefinition Definition);
  Task<IReadOnlyList<Agentflow>> ListAsync();
  Task<AgentflowDetail> GetDetailAsync(Guid id, int revision, CancellationToken cancellationToken = default);
  Task<IReadOnlyList<AgentflowRevision>> ListRevisionsAsync(Guid id, CancellationToken cancellationToken = default);
  Task<Agentflow> CreateAsync(string name, string? description, AgentflowDefinition definition, CancellationToken cancellationToken = default);
  Task<Agentflow> SaveRevisionAsync(Guid agentflowId, int baseRevision, string name, string? description, AgentflowDefinition definition, CancellationToken cancellationToken = default);
  Task<Agentflow> PublishAsync(Guid agentflowId, int revision, CancellationToken cancellationToken = default);
  Task<IReadOnlyList<AgentflowProblem>> ValidateAsync(Guid? agentflowId, string name, AgentflowDefinition definition, CancellationToken cancellationToken = default);
  ```

  `ListPageAsync`、`GetAsync`、`UpdateEnabledAsync`、`DeleteAsync` 的签名保持现状。`ListNodesAsync`、`ListEdgesAsync`、`UpdateAsync` 与旧的 `CreateAsync` 删除。

- 路由（`AgentflowsController`）：`GET /api/agentflows`、`GET /api/agentflows/paged`、`GET /api/agentflows/detail?id=&revision=`、`GET /api/agentflows/revisions?id=`、`POST /api/agentflows`、`POST /api/agentflows/revisions`、`PUT /api/agentflows/published`、`POST /api/agentflows/validate`、`PUT /api/agentflows/enabled`、`DELETE /api/agentflows?id=`、`GET /api/agentflows/mermaid?id=&revision=`。请求、响应与失败的错误码见设计文档"接口列表"。带有 `{id}` 的六个 action 删除。
- 行为：
  - 三个写入口先取得 `AgentDefinitionLock`（现有的取锁写法），在锁内用 `AsNoTracking` 重新读取 `agentflow` 行。
  - `CreateAsync`：校验有 Problem 时抛出 `4000080`；写入 `agentflow`（`StartRevisions`）、revision 1、节点、连线、成员，一次 `SaveChangesAsync`。
  - `SaveRevisionAsync`：行不存在或属于他人时 `ResourceNotFound`；`TryAdvanceRevision` 为 false 时 `4090038`；校验有 Problem 时 `4000080`。写入在一个事务内：先用带条件的 `ExecuteUpdateAsync`（`Id`、`CreateBy` 与 `LatestRevision == baseRevision`）写入 `LatestRevision`、`Name`、`Description`、`UpdateBy`、`UpdateTime`，影响 0 行时抛出 `4090038`；再写入新的 revision 行及其节点、连线、成员并提交。已有 revision 的行不被读取或修改。
  - `PublishAsync`：`revision` 小于 1 时 `InvalidParam`；`agentflow_revision` 中没有这一行时 `4040018`；读取该 revision 的定义并调用 `ValidateAsync`，有 Problem 时 `4000080`；通过后写入 `PublishedRevision`。
  - `GetDetailAsync`：`revision` 小于 1 时 `InvalidParam`；Agentflow 不可见时 `ResourceNotFound`；revision 不存在时 `4040018`。
  - `ListAsync` 只返回 `Enable` 为 true 并且 `PublishedRevision` 大于 0 的 Agentflow。
  - `DeleteAsync` 在锁内删除该 Agentflow 的成员、连线、节点、revision 与 `agentflow` 行。
  - `AgentDeletionCoordinator`：Agent 被任一 Agentflow 的 `LatestRevision` 或 `PublishedRevision` 中的节点或成员引用时抛出 `AgentInUse`。
  - `AgentCatalogFacade`：`SupportsNoToolsAgentflowAsync` 读取已发布 revision 的节点与成员；`UsesAnyModelProviderAsync` 检查各 Agentflow 的 `LatestRevision` 与 `PublishedRevision` 中 Output 节点的 `SummaryModelProviderId`。
  - `git grep -n -E "AgentflowNodes|AgentflowEdges" -- src/server` 列出的其余查询都带上 revision 条件：计算能力的查询使用已发布的 revision，引用检查使用 `LatestRevision` 与 `PublishedRevision`。
  - `AgentflowBehavior` 的 `TryReplaceGraph`、`IsValidGraph` 及其私有方法删除；`AgentflowDefinitionDomainService.TryDefineGraphAsync` 删除。

- [ ] **步骤 1：编写失败的测试**

  `AgentflowAppServiceTests`（现有的 `CreateDbContextAsync` 与 `CreateService`）：

  - `CreateAsync_ValidDefinition_CreatesRevisionOneUnpublished`：`LatestRevision == 1`、`PublishedRevision == 0`，节点、连线、成员的 `AgentflowRevision` 都是 1。
  - `SaveRevisionAsync_BaseRevisionIsLatest_AddsNextRevisionAndKeepsEarlierRows`：保存后 revision 2 的行存在，revision 1 的节点、连线、成员逐行与保存之前相同。
  - `SaveRevisionAsync_BaseRevisionIsStale_ThrowsRevisionConflict`：`Code == 409_0038`。
  - `SaveRevisionAsync_DefinitionHasProblems_ThrowsDefinitionInvalid`：`Code == 400_0080`，`LatestRevision` 与行数都没有变化。
  - `PublishAsync_ExistingRevision_SetsPublishedRevision`。
  - `PublishAsync_RevisionZeroOrMissing_Throws`：`0` → `InvalidParam`，`9` → `404_0018`。
  - `PublishAsync_ReferencedAgentDeleted_ThrowsDefinitionInvalid`：revision 1 引用 Agent X，revision 2 不引用；删除 X 之后发布 revision 1 得到 `400_0080`。
  - `ListAsync_UnpublishedOrDisabled_IsExcluded`。
  - `GetDetailAsync_ForeignAgentflow_ReturnsNotFound`：他人的 id 与随机 id 都是 `ResourceNotFound`。
  - `DeleteAsync_Agentflow_RemovesEveryRevisionRow`。
  - `DeleteAgent_ReferencedByLatestPublishedOrPinnedRevision_IsRejected`：这个任务覆盖节点引用与成员引用、`LatestRevision` 与 `PublishedRevision` 四种情况，以及只被更早的 revision 引用时可以删除；被快照固定的情况在任务 12 加入。

  `AgentflowConcurrentDefinitionTests.SaveRevisionAsync_SecondSaveCommitsFirst_FirstSaveSeesNewLatestInsideLock`：两个 `AgwDbContext` 都已读取 `LatestRevision == 1`；第二个先保存并提交；第一个随后以 `baseRevision` 1 保存，得到 `409_0038`，数据库中只有 revision 1 与 2。

- [ ] **步骤 2：运行测试，确认失败**

  运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*AgentflowAppServiceTests"`
  预期：编译失败，方法不存在。

- [ ] **步骤 3：实现**

  `git grep -n -w "AgentflowAppService" -- src/server tests` 列出的调用方全部改为新的方法。`AgentflowsController` 用 `AgentflowDefinitionMapper` 在 DTO 与 `AgentflowDefinition` 之间转换。

- [ ] **步骤 4：运行测试，确认通过**

  运行：`dotnet test --project tests/Agw.Agents.Tests`、`dotnet test --project tests/Agw.Architecture.Tests`
  预期：全部通过。`git grep -n -E "AgentflowConfigurationParser|AgentflowConfigurationFacts|TryReplaceGraph" -- src/server tests` 没有结果。

- [ ] **步骤 5：格式化并提交（用户要求时）**

  ```bash
  git add src/server/Agw.Agents src/server/Agw.Infrastructure tests/Agw.Agents.Tests
  git commit -m "feat(agents): save, publish and read Agentflow revisions"
  ```

---

### 任务 8：checkpoint 记录 revision 与 runtime version

**文件**（前缀 `src/server/Agw.Agents.Execution/Agentflows/`）：

- 创建：`Checkpoints/AgentflowCheckpointRuntime.cs`
- 修改：`Checkpoints/AgentflowCheckpointStore.cs`、`AgentflowCheckpointContracts.cs`、`AgentflowCheckpointSupport.cs`、`Turns/AgentflowTurnExecutor.cs`
- 删除：`Checkpoints/AgentflowDefinitionFingerprint.cs`
- 测试：`tests/Agw.Agents.Tests/AgentflowCheckpointStoreTests.cs`、`AgentflowCheckpointPersistenceTests.cs`、`AgentflowRuntimeCheckpointTests.cs`、`AgentflowResumeValidationTests.cs`

**接口：**

- 使用（任务 6）：`AgentflowTurnResources.Snapshot.RootRevision`。
- 产出（任务 12 使用）：

  ```csharp
  internal static class AgentflowCheckpointRuntime
  {
      public const int Version = 3;
  }

  internal sealed record AgentflowCheckpointSnapshot(
      Guid OccurrenceId,
      Guid? SourceTurnId,
      Guid AgentflowId,
      long BoundarySequence,
      int AgentflowRevision,
      int RuntimeVersion,
      IReadOnlyList<AgentflowCheckpointMarker> Markers,
      DurableAgentflowCheckpoint Checkpoint
  );
  ```

  - `AgentflowCheckpointStore.RecordAsync` 的 `string definitionFingerprint` 参数改为 `int agentflowRevision`；记录的 `RuntimeVersion` 为 `AgentflowCheckpointRuntime.Version`。`GetDefinitionFingerprintAsync` 删除。
  - `ListAsync` 的 `Available`：`record.RuntimeVersion == AgentflowCheckpointRuntime.Version`、`HasReadableCheckpoint(record)`、`record.IsDurable` 或在 `inProcessOccurrences` 中，三者同时成立。
  - `LoadValidatedResumeCheckpointAsync` 中对 fingerprint 的检查改为：`RuntimeVersion` 与常量不同时抛出 `AgwException(ErrorCodes.InvalidParam, "Agentflow checkpoint was created by another runtime version.")`。
  - `AgentflowCheckpointRecord.DefinitionFingerprint` 属性在任务 10 删除，这个任务之后没有代码读取或写入它。

- [ ] **步骤 1：编写失败的测试**

  - `AgentflowCheckpointStoreTests.RecordAsync_AgentflowTurn_StoresRevisionAndRuntimeVersion`：记录的 `AgentflowRevision` 为传入的值，`RuntimeVersion` 为 3。
  - `AgentflowCheckpointStoreTests.GetCheckpoints_RuntimeVersionDiffers_ReportsUnavailable`：把一条记录的 `RuntimeVersion` 改为 2，`ListAsync` 中它的 `Available` 为 false，另一条为 true；之后保存并发布新的 revision，两条的 `Available` 保持原值。
  - `AgentflowResumeValidationTests.PrepareResume_RuntimeVersionDiffers_ThrowsBeforeHistoryMutation`：抛出 `InvalidParam`，对话历史的行数没有变化。

- [ ] **步骤 2：运行测试，确认失败**

  运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*AgentflowCheckpointStoreTests"`
  预期：编译失败。

- [ ] **步骤 3：实现**

  `AgentflowTurnExecutor` 记录 checkpoint 时传入 `resources.Snapshot.RootRevision`。测试中构造 `AgentflowCheckpointRecord` 与 `AgentflowCheckpointSnapshot` 的位置改为新的属性。

- [ ] **步骤 4：运行测试，确认通过**

  运行：`dotnet test --project tests/Agw.Agents.Tests`
  预期：全部通过。`git grep -n "AgentflowDefinitionFingerprint" -- src/server tests` 没有结果。

- [ ] **步骤 5：格式化并提交（用户要求时）**

  ```bash
  git add src/server/Agw.Agents.Execution tests/Agw.Agents.Tests
  git commit -m "feat(agents): record revision and runtime version on checkpoints"
  ```

---

### 任务 9：内置 Agentflow 与启动时的系统数据维护

**文件**（前缀 `src/server/`）：

- 创建：`Agw.Host/HostStartupInitialization.cs`
- 修改：`Agw.Infrastructure/Data/DbSeeder.cs`、`Agw.Host/Program.cs`
- 测试：`tests/Agw.Setup.Tests/DbSeederTests.cs`、`tests/Agw.Setup.Tests/HostStartupInitializationTests.cs`

**接口：**

- 产出：

  ```csharp
  // DbSeeder
  internal Task EnsureBuiltInAgentflowAsync(CancellationToken cancellationToken = default);

  // HostStartupInitialization.cs
  public static class HostStartupInitialization
  {
      /// <summary>
      /// Control Plane 启动时的初始化与系统数据维护，在应用开始接收请求之前完成。
      /// Initialization and system data maintenance at Control Plane startup, completed before the application serves requests.
      /// </summary>
      public static Task RunAsync(IServiceProvider services, CancellationToken cancellationToken = default);
  }
  ```

  - `HostStartupInitialization.RunAsync` 的内容是 `Program.cs` 中 `if (hasControlPlane)` 代码块现有的语句，`Program.cs` 改为在 `hasControlPlane` 时调用它，位置保持在应用开始接收请求之前。
  - `SeedAsync`（经 `SeedDefaultAgentflowAsync`）与 `MaintainSystemDataAsync` 都调用 `EnsureBuiltInAgentflowAsync`；`MaintainSystemDataAsync` 把它与现有的两项维护放在同一个事务中。
  - `EnsureBuiltInAgentflowAsync` 的规则见设计文档"Migration"一节：
    - 按 `XiaohongshuAgentflowId` 查找。存在并且 `CreateBy` 为 `Constants.AdminUserId`、revision 1 的行与四个节点、三条连线齐全时不做任何修改。
    - 不存在时，按 `GeneralAgentId`、`LocationExtractorAgentId`、`AmapPoiSearchAgentId` 与管理员所有权读取三个 Agent，然后创建 Agentflow（`LatestRevision` 与 `PublishedRevision` 为 1）、revision 1、节点与连线。节点的 id、名称、`Instructions` 与位置沿用现有的值；`input` 的 `RoutingStrategy` 为 `FanOut`，其余为 `Direct`；`ConfigJson` 为 `{}`，`ConditionJson` 为 `[]`。
    - id 属于其他所有者、行不齐全或任一 Agent 不存在时抛出 `InvalidOperationException`，消息中带有对应的 id。这是启动时的系统数据不一致，不经过接口返回。

- [ ] **步骤 1：编写失败的测试**

  `DbSeederTests`（文件型 SQLite 与 `EnsureCreatedAsync`，现有的构造方式）：

  - 现有的 Agentflow 断言改为：`LatestRevision == 1`、`PublishedRevision == 1`，revision 1 有四个节点与三条连线，`input` 节点的 `RoutingStrategy` 为 `FanOut`。
  - `MaintainSystemDataAsync_BuiltinMissing_CreatesPublishedRevisionOne`：`SeedAsync` 之后删除内置 Agentflow 的全部行并保留一条引用它的 Job，维护之后 id 相同的 Agentflow 存在，`PublishedRevision == 1`，Job 的目标 id 没有变化。
  - `MaintainSystemDataAsync_RepeatedStartup_DoesNotDuplicateOrResetBuiltinRevisions`：先保存并发布内置 Agentflow 的 revision 2、把 `Enable` 改为 false，再调用两次维护；节点、连线、revision 的行数与两个 revision 指针、`Enable` 都没有变化。
  - `MaintainSystemDataAsync_BuiltinIdentityConflict_FailsBeforeServing`：把内置 id 的 `CreateBy` 改为另一个用户，维护抛出异常，数据库没有新增行。
  - `MaintainSystemDataAsync_BuiltinAgentMissing_FailsBeforePublishing`：删除内置 Agentflow 与 `LocationExtractorAgentId` 的 Agent，维护抛出异常，没有 Agentflow 行。

  `HostStartupInitializationTests.ControlPlaneStartup_CompletesSystemDataMaintenanceBeforeServing`：用 `ModuleCompositionTestHost` 组装已初始化的 SQLite 数据库（`SeedAsync` 之后删除内置 Agentflow 的行），`HostStartupInitialization.RunAsync` 返回时内置 Agentflow 已经存在；把内置 id 改为他人所有后再次调用，异常从 `RunAsync` 抛出。`Agw.Setup.Tests.csproj` 没有链接 `tests/ModuleCompositionTestHost.cs` 时，按 `tests/Agw.Jobs.Tests` 的写法链接它。

- [ ] **步骤 2：运行测试，确认失败**

  运行：`dotnet test --project tests/Agw.Setup.Tests --filter-class "*DbSeederTests"`
  预期：新增用例失败，内置 Agentflow 的 `PublishedRevision` 为 0。

- [ ] **步骤 3：实现**

- [ ] **步骤 4：运行测试，确认通过**

  运行：`dotnet test --project tests/Agw.Setup.Tests`
  预期：全部通过。

- [ ] **步骤 5：格式化并提交（用户要求时）**

  ```bash
  git add src/server/Agw.Infrastructure src/server/Agw.Host tests/Agw.Setup.Tests
  git commit -m "feat(setup): maintain the built-in Agentflow at startup"
  ```

---

### 任务 10：删除旧的列与类型，生成 migration

**文件**（前缀 `src/server/`）：

- 删除：`Agw.Data/Entities/Agentflows/AgentflowEdgeKind.cs`
- 修改：`Agw.Data/Entities/Agentflows/Agentflow.cs`、`AgentflowNode.cs`、`AgentflowEdge.cs` 及三个 Configuration、`Agw.Data/Entities/Executions/AgentflowCheckpointRecord.cs`、`AgentflowCheckpointRecordConfiguration.cs`
- 创建：`Agw.Migrations.Sqlite` 与 `Agw.Migrations.Postgres` 中的 `AddAgentflowRevisions`
- 测试：`tests/Agw.Projects.Tests/Infrastructure/InitialMigrationTests.cs`、`tests/Agw.Setup.Tests/DbSeederTests.cs`，以及仍然使用被删除成员的测试（`git grep -n -E "DefinitionFingerprint|PositionJson|AgentflowEdgeKind" -- tests` 列出）

**接口：**

- 删除的成员：`Agentflow.SummaryModelProviderId`、`AgentflowNode.PositionJson`、`AgentflowEdge.Kind`、`AgentflowEdge.ConfigJson`、`AgentflowCheckpointRecord.DefinitionFingerprint`、`AgentflowEdgeKind`。
- `AgentflowNode.ConfigJson` 改为非空，默认 `{}`（长度 16000）；`AgentflowEdge.ConditionJson` 改为非空，默认 `[]`（长度 8000）。
- migration 的 `Up` 先执行 `DELETE FROM agentflow_trace`、`agentflow_checkpoint`、`agentflow_edge`、`agentflow_node`、`agentflow` 五条语句，再执行生成的表结构修改。

- [ ] **步骤 1：编写失败的测试**

  - `InitialMigrationTests.MigrateAsync_FromPreviousMigration_DeletesAgentflowRowsAndKeepsTurns`（SQLite；PostgreSQL 版本带有 `SkipUnless`）：把数据库迁移到 `AddExecutionSteer`，用原始 SQL 写入一行 `agentflow`、一行 `agentflow_node`、一行 `agentflow_edge` 与一行 `project_conversation_turn`；迁移到最新之后，三张 Agentflow 表为空，Turn 行存在并且 `extra` 为 `{}`。
  - `DbSeederTests.MaintainSystemDataAsync_InitializedSqliteAfterMigration_SeedsBuiltinPublishedRevisionOne`：数据库用 `MigrateAsync` 建立，`SeedAsync` 之后删除内置 Agentflow 的行并保留引用它的 Job 与事件订阅，维护之后内置 id 的 Agentflow 存在、属于管理员、`PublishedRevision == 1`，Job 与事件订阅的目标 id 没有变化。
  - `DbSeederTests.MaintainSystemDataAsync_InitializedPostgresAfterMigration_SeedsBuiltinPublishedRevisionOne`：同样的步骤，使用 `AGW_TEST_POSTGRES_CONNECTION_STRING` 中的 PostgreSQL，带有 `SkipUnless`。

- [ ] **步骤 2：删除旧的成员并修正引用**

  删除"接口"中列出的成员，按编译错误修正剩余的引用。运行 `dotnet build Agw.slnx`，预期通过；`git grep -n -w -E "AgentflowEdgeKind|PositionJson|DefinitionFingerprint" -- src/server tests ':!src/server/Agw.Migrations.*'` 没有结果。

- [ ] **步骤 3：生成 migration（执行之前向用户确认）**

  向用户说明将要执行 `docs/1.Development.md` 中的两条命令，得到确认后在仓库根目录运行：

  ```bash
  dotnet ef migrations add AddAgentflowRevisions -p src/server/Agw.Migrations.Sqlite -s src/server/Agw.Standalone.Host -- --provider sqlite
  dotnet ef migrations add AddAgentflowRevisions -p src/server/Agw.Migrations.Postgres -s src/server/Agw.Standalone.Host -- --provider postgres
  ```

  用文件编辑工具在两个 migration 的 `Up` 开头加入五条 `migrationBuilder.Sql("DELETE FROM …;")`，顺序与"接口"相同。确认生成的内容中没有外键。

- [ ] **步骤 4：运行测试，确认通过**

  运行：`dotnet test --project tests/Agw.Projects.Tests`、`dotnet test --project tests/Agw.Setup.Tests`、`dotnet test --project tests/Agw.Agents.Tests`、`dotnet test --project tests/Agw.Architecture.Tests`
  预期：全部通过，包括 `InitialMigrationTests` 的 `HasPendingModelChanges` 断言。本机配置了 PostgreSQL 时，带有 `SkipUnless` 的用例也运行并通过。

- [ ] **步骤 5：格式化并提交（用户要求时）**

  ```bash
  git add src/server/Agw.Data src/server/Agw.Migrations.Sqlite src/server/Agw.Migrations.Postgres tests
  git commit -m "feat(agents): drop the legacy Agentflow columns and add the revision migration"
  ```

---

### 任务 11：Turn 受理时固定 revision 快照

**文件**（前缀 `src/server/`）：

- 创建：`Agw.Projects.Contracts/History/AgentflowDependencyRevisionDto.cs`
- 修改：`Agw.Projects.Contracts/History/IConversationTurnStore.cs`、`Agw.Projects/Infrastructure/ConversationTurnStore.cs`
- 修改：`Agw.Agents/Application/Persistence/ITurnAcceptanceWriter.cs`、`DurableExecutionManifest.cs`、`Agw.Infrastructure/Agents/TurnAcceptanceWriter.cs`、`DurableTurnUpgrade.cs`
- 修改：`Agw.Agents.Execution/Commands/Exec/ExecCommand.cs`、`Inbound/Connections/ExecutionTarget.cs`、`ExecutionConnectionContext.cs`、`Inbound/Facades/AgentExecutionFacade.cs`、`Runtimes/Contracts/ExecutionRequest.cs`、`Turns/TurnAcceptanceService.cs`、`Runtimes/InProcess/InProcessExecutionCoordinator.cs`、`Runtimes/Durable/DurableExecutionCoordinator.cs`、`Persistence/Durable/DurableExecutionStore.cs`、`Agentflows/Runtime/AgentflowRuntimeFactory.cs`
- 测试：`tests/Agw.Agents.Tests/TurnAcceptanceTests.cs`、`TurnAcceptanceWriterSnapshotTests.cs`、`DurableTurnUpgradeTests.cs`、`DurableAgentflowRevisionTests.cs`、`TurnPersistenceTestKit.cs`；`tests/Agw.Projects.Tests/ConversationTurnStoreTests.cs`

**接口：**

- 使用（任务 1、6）：`ProjectConversationTurnExtra`、`AgentflowRevisionSnapshot`、`IAgentflowRevisionSnapshotReader`、`ResolveAsync(target, snapshot, project, …)`。
- 产出（任务 12 与客户端任务 17 使用）：

  ```csharp
  // ExecCommand
  public int AgentflowRevision { get; set; }

  // ExecutionTarget.cs
  public readonly record struct ExecutionTarget(Guid AgentId, AgentRuntimeType AgentType, int AgentflowRevision = 0);

  // TurnAcceptanceRequest 增加
  public int RequestedAgentflowRevision { get; init; }

  // ExecutionRequest 增加
  public AgentflowRevisionSnapshot? AgentflowSnapshot { get; init; }

  // DurableExecutionManifest 增加
  public int AgentflowRevision { get; init; }
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public IReadOnlyList<AgentflowDependencyRevision>? AgentflowDependencies { get; init; }

  // Agw.Projects.Contracts/History/AgentflowDependencyRevisionDto.cs
  public sealed record AgentflowDependencyRevisionDto(
      Guid ParentAgentflowId,
      int ParentRevision,
      string ParentNodeId,
      string? BlockMemberId,
      Guid TargetAgentflowId,
      int TargetRevision
  );

  // AcceptConversationTurnRequest 增加
  public int AgentflowRevision { get; init; }
  public IReadOnlyList<AgentflowDependencyRevisionDto>? AgentflowDependencies { get; init; }
  // ConversationTurnSnapshot 在末尾增加同名、同类型的两个参数

  // ITurnAcceptanceWriter.cs
  public sealed record TurnAcceptanceWrite
  {
      public required AcceptConversationTurnRequest Turn { get; init; }

      /// <summary>
      /// 请求的根 revision；0 表示已发布的 revision，目标是 Agent 时为 0。
      /// The requested root revision; 0 means the published revision, and it is 0 for an Agent target.
      /// </summary>
      public int RequestedAgentflowRevision { get; init; }

      /// <summary>
      /// 快照确定之后、写入 Turn 之前，在受理事务内调用，返回 extension 与 Durable 登记。
      /// Called inside the acceptance transaction after the snapshot is fixed and before the turn is written; returns the extensions and the Durable registration.
      /// </summary>
      public required Func<AgentflowRevisionSnapshot?, CancellationToken, Task<TurnAcceptanceRegistration>> Register { get; init; }
  }

  public sealed record TurnAcceptanceRegistration(IReadOnlyList<Guid> ExtensionIds, DurableTurnRegistration? Durable);
  ```

  `TurnAcceptanceWrite` 现有的 `ExtensionIds` 与 `Durable` 属性删除。

- `TurnAcceptanceWriter.AcceptAsync` 的顺序：
  1. 取得两个锁（现状），用 `BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)` 开始事务。
  2. `turns.GetAsync(turn.TurnId)` 有结果时：对话不同抛出 `ResourceNotFound`；相同则返回 `Created: false`、已保存的 Turn 与现有 Durable 记录的状态（没有 Durable 记录时为 null），不读取任何 published pointer，不调用 `Register`。
  3. 目标是 Agentflow 时，从同一个 scope 取得 `IAgentflowRevisionSnapshotReader` 并调用 `CreateRevisionSnapshotAsync(turn.TargetId, write.RequestedAgentflowRevision, UserInfoUtil.RequiredUserId, …)`；目标是 Agent 时快照为 null。
  4. 调用 `write.Register(snapshot, …)`，校验返回的 `ExtensionIds`（现有的 `ValidateEnabledAsync`）。
  5. `turns.AcceptAsync`：传入带有 `AgentflowRevision` 与 `AgentflowDependencies` 的 Turn（Agentflow 目标没有嵌套引用时是空数组，Agent 目标是 0 与 null）。
  6. 有 `Durable` 登记时写入执行记录与开始事件（现状），提交。
  - 现有的 `catch (DbUpdateException)` 增加条件 `when (exception.InnerException is not PostgresException { SqlState: PostgresErrorCodes.SerializationFailure })`。序列化冲突的异常不被捕获。
- `TurnAcceptanceService.AcceptAsync`：
  - `RequestedAgentflowRevision` 不为 0 并且目标是 Agent 时抛出 `AgwException(ErrorCodes.InvalidParam, "ExecCommand.agentflowRevision applies to Agentflow targets only.")`。
  - 已有 Turn 的分支：`ExecutionTarget` 带有 `existing.AgentflowRevision`，`AgentflowSnapshot` 由 `existing` 的两个值还原。
  - 新的 Turn：extension 的选择、`AcquireCurrentAsync`、`ExecutionRequest`（`Target` 带有 `snapshot.RootRevision`，`AgentflowSnapshot` 为快照）、开始消息与 `CreateRegistration` 都移入 `Register` 回调。writer 返回 `Created: false` 时按已有 Turn 的分支组装结果，并释放回调中取得的 extension 能力。
  - 任务 6 中在 resolver 之前生成快照的过渡写法删除。
- `DurableExecutionStore.CreateManifestJson` 写入 `AgentflowRevision = request.Target.AgentflowRevision` 与 `AgentflowDependencies = request.AgentflowSnapshot?.Dependencies`。
- `TurnInput.AgentflowSnapshot`：`InProcessExecutionCoordinator` 填入 `request.AgentflowSnapshot`；`DurableExecutionCoordinator.CreateTurnInput` 由 manifest 还原，Agentflow 目标的 manifest 没有 `AgentflowDependencies` 时抛出 `AgwException(ErrorCodes.DurableExecutionConflict, "The execution manifest has no Agentflow revision snapshot.")`。
- `AgentflowRuntimeFactory.PrepareTurnAsync` 的 `snapshot` 参数改为非空，任务 6 中读取 published pointer 的过渡写法与构造函数中的 `IAgentflowRevisionSnapshotReader` 删除。
- `ExecutionConnectionContext` 把 `command.AgentflowRevision` 放入 `TurnAcceptanceRequest.RequestedAgentflowRevision`；`AgentExecutionFacade` 的请求使用 0。
- `DurableTurnUpgrade` 补齐 Turn 行时写入 manifest 中的 `AgentflowRevision` 与 `AgentflowDependencies`（manifest 中没有时保持 null）。
- `ConversationTurnStore` 在 `AgentflowDependencyRevisionDto` 与 `ProjectConversationTurnAgentflowDependency` 之间逐字段转换。

- [ ] **步骤 1：编写失败的测试**

  `TurnAcceptanceTests`（`TurnPersistenceTestKit`）：

  - `ExecCommand_RevisionZero_UsesPublishedRevision`、`ExecCommand_Unpublished_ThrowsNotPublished`、`ExecCommand_ExplicitRevision_UsesIt`：断言 `AcceptedTurn.Request.Target.AgentflowRevision` 与 Turn 行的 `extra`；未发布时 `Code == 409_0039` 并且没有 Turn 行。
  - `AcceptAsync_AgentflowRevisionOnAgentTarget_ThrowsInvalidParam`。
  - `AcceptAsync_ResentTurnId_ReturnsTargetWithStoredRevision`：受理之后发布新的 revision，再次受理同一个 `turnId`，`Target.AgentflowRevision` 与第一次相同。
  - `AcceptAsync_AgentflowTurn_ResolvesExtensionsFromAcceptedSnapshot`：revision 1 的 Agent 使用 extension Tool A，revision 2 的 Agent 使用 Tool B，已发布的是 1。请求 revision 2 时 `ExtensionPins` 只有 B；请求 0 时只有 A。

  `TurnAcceptanceWriterSnapshotTests`：

  - `TurnAcceptanceWriter_AcceptAsync_UsesOneSnapshotForRootAndDependencies`（SQLite 与 PostgreSQL 各一个用例）：根引用 B，`Register` 回调中读取 `dbContext.Database.CurrentTransaction` 的隔离级别为 `Serializable`；提交后 Turn 的 `extra` 与 manifest 中的 `agentflowRevision`、`agentflowDependencies` 相同。
  - `TurnAcceptanceWriter_ExistingTurnId_ReusesStoredRevisionSnapshot`：第二次受理之前发布根与 B 的新 revision；结果 `Created == false`，快照与第一次相同，`Register` 没有被调用。
  - `CreateRevisionSnapshotAsync_PublishDuringSnapshot_ProducesConsistentDependencyClosure`（PostgreSQL）：事务 A 以 `Serializable` 开始并读取根的 `agentflow` 行；另一个连接发布 B 的 revision 2 并提交；A 随后生成快照并写入 Turn。结果是下面两种之一：提交成功并且依赖中 B 的 revision 为 1；或以 `40001` 结束并且没有 Turn 行。
  - `TurnAcceptanceWriter_SerializationFailure_PropagatesAndWritesNoTurn`（PostgreSQL）：在 `Register` 回调中，用另一个连接的 `Serializable` 事务读取该对话的 Turn 行数、修改根 Agentflow 的 `published_revision` 并提交。受理抛出的异常是 `PostgresException` 或其内层为 `PostgresException`，`SqlState` 为 `40001`；没有 Turn 行与 Durable 记录；之后用同一个 `turnId` 再次受理成功，使用新的 published revision。

  `DurableAgentflowRevisionTests`（PostgreSQL，`SkipUnless`）：

  - `RunAsync_PublishDuringDurableTurn_LaterSegmentUsesManifestRevision`
  - `RunAsync_NestedAgentflow_SegmentRetryUsesAcceptedDependencySnapshot`
  - `RunAsync_NestedAgentflow_PublishDuringDistributedWait_ResumeUsesAcceptedDependencySnapshot`：Turn 在 Human Gate 等待时发布嵌套 Agentflow 的新 revision，重新创建协调层后回答，后续节点来自受理时的 revision。
  - `RunSegment_ManifestWithoutRevisionSnapshot_FailsWithConflict`：把一条排队中的 Agentflow 执行的 manifest 改为没有 `agentflowDependencies`，领取之后 Turn 以失败结束，错误码为 `DurableExecutionConflict`。

  `DurableTurnUpgradeTests.UpgradeAsync_ManifestWithSnapshot_CopiesRevisionAndDependencies`。

  `ConversationTurnStoreTests`（`Agw.Projects.Tests`）：

  - `AcceptAsync_AgentflowTurn_StoresRevisionSnapshotInExtra`：`extra` 的文本为 `{"agentflowRevision":3,"agentflowDependencies":[{…}]}`，`GetAsync` 返回的两个值与写入的相同。
  - `AcceptAsync_AgentTurn_StoresRevisionZero`：`extra` 为 `{"agentflowRevision":0}`。
  - `ConversationTurnStore_AgentflowDependencies_MapsPersistenceToHistoryDto`：六个字段逐一对比。

- [ ] **步骤 2：运行测试，确认失败**

  运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*TurnAcceptanceTests"`
  预期：编译失败。

- [ ] **步骤 3：实现**

  `git grep -n "new ExecutionTarget(" -- src/server` 列出的五处中，受理之前创建的目标不带 revision；`InProcessExecutionCoordinator` 中 `host.Target != request.Target` 的对比保持原样，它在受理之后执行。

- [ ] **步骤 4：运行测试，确认通过**

  运行：`dotnet test --project tests/Agw.Agents.Tests`、`dotnet test --project tests/Agw.Projects.Tests`、`dotnet test --project tests/Agw.Architecture.Tests`
  预期：全部通过。PostgreSQL 用例在本机没有配置时显示为跳过，这时在提交之前向用户说明这些用例尚未运行。

- [ ] **步骤 5：格式化并提交（用户要求时）**

  ```bash
  git add src/server tests
  git commit -m "feat(agents): pin Agentflow revisions when a turn is accepted"
  ```

---

### 任务 12：checkpoint 恢复沿用快照，被固定的 revision 不可删除

**文件**（前缀 `src/server/`）：

- 创建：`Agw.Agents/Application/Persistence/IAgentflowRevisionUsageReader.cs`、`Agw.Agents.Execution/Agentflows/Checkpoints/AgentflowRevisionUsageReader.cs`
- 修改：`Agw.Agents.Execution/Agentflows/Checkpoints/AgentflowCheckpointStore.cs`、`AgentflowCheckpointContracts.cs`、`Inbound/Connections/ExecutionConnectionContext.cs`、`Turns/TurnAcceptanceService.cs`、`DependencyInjection.cs`
- 修改：`Agw.Agents/Application/Persistence/IAgentflowCheckpointPersistence.cs`、`ITurnAcceptanceWriter.cs`、`Agw.Agents/Definitions/Agents/AgentflowAppService.cs`、`Agw.Infrastructure/Agents/AgentflowCheckpointPersistence.cs`、`TurnAcceptanceWriter.cs`、`AgentDeletionCoordinator.cs`
- 测试：`tests/Agw.Agents.Tests/AgentflowCheckpointStoreTests.cs`、`AgentflowCheckpointPersistenceTests.cs`、`AgentflowResumeValidationTests.cs`、`AgentflowRuntimeCheckpointTests.cs`、`AgentflowAppServiceTests.cs`

**接口：**

- 产出：

  ```csharp
  // AgentflowCheckpointSnapshot 在 RuntimeVersion 之后增加
  AgentflowRevisionSnapshot RevisionSnapshot

  // TurnAcceptanceWrite 增加
  /// <summary>
  /// 恢复 checkpoint 时沿用的快照；有值时不读取 published pointer。
  /// The snapshot kept when resuming a checkpoint; published pointers are not read when it is set.
  /// </summary>
  public AgentflowRevisionSnapshot? PinnedSnapshot { get; init; }

  // IAgentflowCheckpointPersistenceSession
  Task AcceptResumeTurnAsync(Guid turnId, AgentflowCheckpointRecord checkpoint, AgentflowRevisionSnapshot snapshot, CancellationToken cancellationToken = default);

  // IAgentflowRevisionUsageReader.cs
  public interface IAgentflowRevisionUsageReader
  {
      /// <summary>
      /// 当前用户的活跃 Durable 执行与可恢复 checkpoint 固定的全部 revision。
      /// Every revision pinned by the current user's active Durable executions and resumable checkpoints.
      /// </summary>
      Task<IReadOnlySet<(Guid AgentflowId, int Revision)>> ListPinnedRevisionsAsync(CancellationToken cancellationToken = default);
  }
  ```

- 恢复之前的校验（`LoadValidatedResumeCheckpointAsync`，在修改历史与登记恢复执行之前）：
  - `SourceTurnId` 为空、来源 Turn 不存在或它的 `AgentflowDependencies` 为 null：`AgwException(ErrorCodes.InvalidParam, "The checkpoint's source turn has no Agentflow revision snapshot.")`。
  - Durable checkpoint 的来源执行不存在、manifest 没有 `AgentflowDependencies`，或 manifest 与 Turn 的根 revision、依赖列表不相同：`AgwException(ErrorCodes.InvalidParam, "The checkpoint's revision snapshots do not match.")`。依赖列表按六个字段对比，不考虑顺序。
  - 快照中某个 `(AgentflowId, Revision)` 没有 `agentflow_revision` 行，或它的节点、成员引用的 Agent 不存在：`AgwException(ErrorCodes.InvalidParam, "A definition pinned by this checkpoint is no longer available.")`。
  - 通过后 `ValidatedResumeCheckpoint.Snapshot.RevisionSnapshot` 为来源 Turn 的快照。
- `ListAsync` 的 `Available` 在任务 8 的条件之外增加上面三项校验都通过。
- Durable 恢复：`RegisterResumeExecutionAsync` 复制的 manifest 保留 `AgentflowRevision` 与 `AgentflowDependencies`；`AcceptResumeTurnAsync` 把快照写入恢复 Turn；`ExecutionConnectionContext` 恢复后的 `ExecutionTarget` 带有快照的根 revision。
- InProcess 恢复：`TurnAcceptanceService` 在 `request.ResumeCheckpoint` 有值时把 `PinnedSnapshot` 设为 `ResumeCheckpoint.RevisionSnapshot`；`TurnAcceptanceWriter` 在 `PinnedSnapshot` 有值时直接使用它。
- `ListPinnedRevisionsAsync`：读取当前用户状态不是 `Completed`、`Failed`、`Interrupted` 的 Durable 执行的 manifest，以及 `RuntimeVersion` 等于当前常量并且来源 Turn 有快照的 checkpoint；每份快照贡献 `(根 id, 根 revision)` 与各依赖的 `(TargetAgentflowId, TargetRevision)`。
- 删除：
  - `AgentflowAppService.DeleteAsync`：结果中有该 Agentflow 的任一 revision 时抛出 `4090040`，定义保持原状。
  - `AgentDeletionCoordinator`：Agent 被结果中任一 revision 的节点或成员引用时抛出 `AgentInUse`。

- [ ] **步骤 1：编写失败的测试**

  - `AgentflowRuntimeCheckpointTests.ResumeCheckpoint_AfterNewRevisionPublished_RunsCheckpointRevision`：InProcess，根与嵌套的 Agentflow 在 checkpoint 之后都发布了新的 revision，恢复后的节点输出来自原来的 revision，恢复 Turn 的 `extra` 与来源 Turn 相同。
  - `AgentflowCheckpointStoreTests.GetCheckpoints_DurableSnapshotMissingOrMismatched_ReportsUnavailable`：`[Theory]`，来源 Turn 不存在、来源执行不存在、Turn 的 `extra` 为 `{}`、manifest 没有依赖属性、两份依赖不同，五种情况的 `Available` 都是 false；Turn 与 manifest 都是空数组时为 true。
  - `AgentflowResumeValidationTests.ResumeCheckpoint_MissingSourceTurnOrDependencySnapshot_FailsBeforeHistoryMutation`、`ResumeCheckpoint_TurnAndManifestSnapshotDisagree_FailsBeforeHistoryMutation`：抛出 `InvalidParam`，对话历史、checkpoint 与 Durable 执行的行数都没有变化。
  - `AgentflowCheckpointPersistenceTests.AcceptResumeTurnAsync_Checkpoint_CopiesEntireDependencySnapshot`：恢复 Turn 的 `extra` 与新 manifest 中的根 revision、依赖列表都与来源相同。
  - `AgentflowAppServiceTests.DeleteAgentflow_ReferencedByActiveManifestOrResumableCheckpoint_IsRejected`：`[Theory]`，被活跃执行作为根、被活跃执行作为依赖目标、被可恢复 checkpoint 作为依赖目标，三种情况都是 `409_0040` 并且行没有变化；执行结束并且没有 checkpoint 之后可以删除。
  - `AgentflowAppServiceTests.DeleteAgent_ReferencedByLatestPublishedOrPinnedRevision_IsRejected` 增加：Agent 只被一个更早的 revision 引用，该 revision 被活跃执行固定时得到 `409_0018`。

- [ ] **步骤 2：运行测试，确认失败**

  运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*AgentflowResumeValidationTests"`
  预期：编译失败或断言失败。

- [ ] **步骤 3：实现**

  `AgentflowRevisionUsageReader` 用 `DurableExecutionJson` 读取 manifest，用 `IConversationTurnStore.GetAsync` 读取来源 Turn，由 `Agw.Agents.Execution` 的 `DependencyInjection` 注册为 scoped。

- [ ] **步骤 4：运行测试，确认通过**

  运行：`dotnet test --project tests/Agw.Agents.Tests`、`dotnet test --project tests/Agw.Architecture.Tests`
  预期：全部通过。

- [ ] **步骤 5：格式化并提交（用户要求时）**

  ```bash
  git add src/server tests/Agw.Agents.Tests
  git commit -m "feat(agents): resume checkpoints from the accepted revision snapshot"
  ```

---

### 任务 13：trace 的 `extra` 与 `GET /api/traces`

**文件**（前缀 `src/server/`）：

- 修改：`Agw.Agents.Execution/Agentflows/Observability/AgentflowNodeExecutionActivity.cs`、`Agentflows/Context/AgentflowNodeScopedAgent.cs`、`Agentflows/Workflows/AgentflowWorkflowCompiler.cs`、`Agentflows/Workflows/Builders/AgentflowBlockBuildSupport.cs`、`Agentflows/Turns/AgentflowTurnExecutor.cs`
- 修改：`Agw.Agents/Definitions/Contracts/AgentflowTraceDto.cs`、`Agw.Agents/Definitions/Agents/AgentflowTraceAppService.cs`
- 测试：`tests/Agw.Agents.Tests/AgentflowTraceTests.cs`、`AgentflowTraceAppServiceTests.cs`

**接口：**

- 产出：
  - `AgentflowNodeExecutionActivity.StartAgent` 与 `StartHumanGate` 在 `Guid agentflowId` 之后增加 `int agentflowRevision`；`TryCreateTrace` 写入 `AgentflowTrace.Extra = new AgentflowTraceExtra { AgentflowRevision = … }`。
  - `AgentflowNodeScopedAgent` 的构造函数在 `Guid? agentflowId` 之后增加 `int agentflowRevision = 0`。`Compile` 把自己的 `agentflowRevision` 传给节点的 scoped agent，Block 成员经 `AgentflowBlockBuildContext` 得到它。
  - Human Gate 的 trace 由 `AgentflowTurnExecutor` 传入 `resources.Snapshot.RootRevision`。
  - `AgentflowTraceDto` 增加 `public AgentflowTraceExtra Extra { get; init; } = new();`，`CreatePagedResult` 填入 `trace.Extra`。

- [ ] **步骤 1：编写失败的测试**

  - `AgentflowTraceTests.RunAsync_AgentflowNodes_TraceExtraRecordsAgentflowRevision`：执行 revision 2（已发布的是 1，请求 2），Agent 节点、Block 成员与 Human Gate 的 trace 的 `Extra.AgentflowRevision` 都是 2。
  - `AgentflowTraceTests.RunAsync_NestedAgentflow_TraceRecordsPinnedDependencyRevision`：嵌套 Agentflow 在快照中是 revision 3，它内部节点的 trace 的 `AgentflowId` 是嵌套 Agentflow 的 id，`Extra.AgentflowRevision` 是 3。
  - `AgentflowTraceAppServiceTests.ListAsync_MapsEntityToDto`：`BuildTrace` 设置 `Extra`，增加 `Assert.Equal(trace.Extra, dto.Extra)`。

- [ ] **步骤 2：运行测试，确认失败**

  运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*AgentflowTraceTests" --filter-class "*AgentflowTraceAppServiceTests"`
  预期：编译失败。

- [ ] **步骤 3：实现**

- [ ] **步骤 4：运行测试，确认通过**

  运行步骤 2 的命令与 `dotnet test --project tests/Agw.Host.Tests --filter-class "*AgentflowNodeExecutionTraceModelTests"`。预期：全部通过。

- [ ] **步骤 5：格式化并提交（用户要求时）**

  ```bash
  git add src/server/Agw.Agents src/server/Agw.Agents.Execution tests
  git commit -m "feat(agents): record the Agentflow revision on traces"
  ```

---

### 任务 14：节点标识 `nodeId` 与 `nodePath`

**文件**（前缀 `src/server/`）：

- 修改：`Agw.Agents.Contracts/Execution/HumanInteraction.cs`、`Agw.Agents.Execution/Agentflows/Context/AgentflowNodeScopedAgent.cs`、`Agentflows/Runtime/AgentflowRuntimeFactory.cs`、`Agentflows/Messaging/AgentflowMessageTransforms.cs`、`AgentflowMessageMapper.cs`、`Agentflows/Checkpoints/AgentflowCheckpointStore.cs`、`Summaries/AgentTurnSummaryService.cs`
- 测试：`tests/Agw.Agents.Tests/AgentflowResultAttributionTests.cs`、`AgentflowRuntimeCheckpointTests.cs`、`AgentflowNodeScopedAgentTests.cs`、`AgentflowTurnExecutorTests.cs`

**接口：**

- 产出（客户端任务 18、24 使用）：
  - 节点产生的消息的 `additionalProperties`：`nodeId` 是当前 Turn 执行的 Agentflow 中的顶层节点 id；`nodePath` 是从顶层节点到产生消息的节点的 id，用 `/` 连接。顶层节点的 `nodePath` 等于 `nodeId`；嵌套 Agentflow 中的节点是 `{顶层节点 id}/{嵌套节点 id}`；Block 成员是 `{Block 节点 id}/{Block 节点 id}.{memberId}`，Manager 保留现有的后缀。`nodeName` 保持现状。
  - `agentflow-checkpoint` 消息使用 `nodeId`。
  - `InteractionSource` 增加 `public string? NodePath { get; init; }`，`NodeId` 是顶层节点 id。
  - `AgentflowNodeScopedAgent` 的构造函数增加 `string? topLevelNodeId = null` 与 `string? nodePath = null`；`AgentflowRuntimeFactory` 在创建嵌套 Workflow 与成员时传入上一级的路径。
  - `interactionNodeId` 与 `checkpointNodeId` 这两个属性名删除。

- [ ] **步骤 1：编写失败的测试**

  - `AgentflowTurnExecutorTests.RunAsync_NodeOutput_CarriesNodeIdAndNodePath`：顶层 Agent 节点 `a`：`nodeId` 与 `nodePath` 都是 `a`。Workflow as Agent 节点 `w` 中的节点 `n`：`nodeId` 为 `w`，`nodePath` 为 `w/n`。Block `b` 的成员 `m1`：`nodeId` 为 `b`，`nodePath` 为 `b/b.m1`。Human Gate 的 `interaction-request` 中 `source.nodeId` 与 `source.nodePath` 按同样的规则。
  - `AgentflowRuntimeCheckpointTests`：现有读取 `checkpointNodeId` 的断言改为 `nodeId`。
  - `AgentflowResultAttributionTests`：现有读取 `interactionNodeId` 的断言改为 `nodePath`，并增加对 `nodeId` 的断言。

- [ ] **步骤 2：运行测试，确认失败**

  运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*AgentflowResultAttributionTests" --filter-class "*AgentflowRuntimeCheckpointTests"`
  预期：断言失败，属性不存在。

- [ ] **步骤 3：实现**

- [ ] **步骤 4：运行测试，确认通过**

  运行：`dotnet test --project tests/Agw.Agents.Tests`
  预期：全部通过。`git grep -n -i -E "interactionNodeId|checkpointNodeId" -- src/server tests` 只剩下 C# 中名为 `CheckpointNodeIds` 的成员（恢复时放行的 CheckpointMarker 节点集合）。

- [ ] **步骤 5：格式化并提交（用户要求时）**

  ```bash
  git add src/server tests/Agw.Agents.Tests
  git commit -m "feat(agents): name node identity nodeId and nodePath on messages"
  ```

---

### 任务 15：节点状态消息

**文件**（前缀 `src/server/`）：

- 创建：`Agw.Agents.Contracts/Messages/AgwAgentflowNodeStatus.cs`
- 修改：`Agw.Agents.Contracts/Messages/AgwMessageTypes.cs`、`Agw.Agents.Execution/Agentflows/Messaging/AgentflowMessageMapper.cs`、`Agentflows/Turns/AgentflowTurnExecutor.cs`
- 测试：`tests/Agw.Agents.Tests/AgentflowTurnExecutorTests.cs`、`AgentflowMessageMapperTests.cs`、`tests/Agw.A2A.Tests/AgentHandlerFactoryTests.cs`

**接口：**

- 使用（任务 4）：`AgentflowWorkflowMetadata.NodeIdsByExecutorId`。
- 产出（客户端任务 17 使用）：
  - `AgwMessageTypes.AgentflowNodeStatus = "agentflow-node-status"`；`AgwAgentflowNodeStatus` 的内容是设计文档第 6.4 节的代码。
  - `internal static AgwMessage CreateNodeStatusMessage(string nodeId, string status, string? interactionId, string messageId)`：角色 `AiRole.System`，`Contents` 为空，`additionalProperties` 为 `type`、`nodeId`、`status`，`interactionId` 有值时加入。
  - `WorkflowInteractionResult(AgwMessage? Rejection, InteractionRequest? Waiting, InteractionRequest? Answered)`。
  - 写入的时机是设计文档第 6.4 节的表格。`ExecutorFailedEvent` 属于 `Microsoft.Agents.AI.Workflows`，实现之前在已引用的包版本中确认它的类型名与属性。
  - `AgwMessageClassifier.IsControl` 保持现状。

- [ ] **步骤 1：编写失败的测试**

  `AgentflowTurnExecutorTests`：

  - `RunAsync_LinearAgentflow_WritesRunningAndCompletedForEveryNode`：Input → Prompt Adapter → Agent → Output，四个节点各自至少有一条 `running` 与一条 `completed`，每个节点最后一条是 `completed`；全部状态消息位于 `agw-turn-start` 之后、`agw-turn-finished` 之前；辅助 executor 没有状态消息。
  - `RunAsync_HumanGateAnswered_WritesWaitingThenCompletedWithInteractionId`：`waiting` 与随后的 `completed` 带有同一个 `interactionId`，`waiting` 位于 `interaction-request` 之前。
  - `RunAsync_ToolApprovalAnswered_WritesWaitingThenRunningWithInteractionId`：Agent 节点内的 Tool approval，回答之后是带有同一个 `interactionId` 的 `running`。
  - `RunAsync_NodeFails_WritesFailedBeforeWorkflowError`。
  - `RunAsync_NodeStatusMessages_AreNotWrittenToHistory`：Turn 结束后对话历史中没有 `type` 为 `agentflow-node-status` 的消息。
  - `RunAsync_ResultOnly_OmitsNodeStatusMessages`：`ResultOnly` 的执行输出中没有状态消息（放在现有 `ResultOnly` 用例所在的测试类中，`git grep -n "ResultOnly" -- tests/Agw.Agents.Tests` 列出）。

  `AgentflowMessageMapperTests.CreateNodeStatusMessage_WithoutInteraction_OmitsInteractionId`。

  `AgentHandlerFactoryTests.PublishArtifactsAsync_NodeStatusMessage_AddsNoArtifact`：流式执行的输出中有一条状态消息，任务的 artifact 数量与没有这条消息时相同。

- [ ] **步骤 2：运行测试，确认失败**

  运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*AgentflowTurnExecutorTests"`
  预期：新增用例失败，输出中没有状态消息。

- [ ] **步骤 3：实现**

  交互对应的节点是 `request.Source.NodeId`，它不在 `NodeIdsByExecutorId` 的值中时不写入。Durable 恢复时，已保存的回答经 `SendResponseAsync` 交回之后写入同样的消息。

- [ ] **步骤 4：运行测试，确认通过**

  运行：`dotnet test --project tests/Agw.Agents.Tests`、`dotnet test --project tests/Agw.A2A.Tests`
  预期：全部通过。

- [ ] **步骤 5：格式化并提交（用户要求时）**

  ```bash
  git add src/server/Agw.Agents.Contracts src/server/Agw.Agents.Execution tests
  git commit -m "feat(agents): emit agentflow-node-status messages"
  ```

---

### 任务 16：Server 文档

**文件：**

- 修改：`docs/ws-flow.md`、`src/server/Agw.Agents.Execution/README.md`、`src/server/Agw.Agents.Execution/HumanInteraction/README.md`

- [ ] **步骤 1：更新文档**

  - `docs/ws-flow.md`：`ExecCommand.agentflowRevision`、`workflow-gate` 的请求与回答、`agentflow-node-status`、消息属性 `nodeId` 与 `nodePath`、checkpoint 的可恢复判断，内容与设计文档"执行协议"一节相同。
  - `Agw.Agents.Execution/README.md`：Turn 受理事务中的 revision 快照、Durable manifest 中的快照、checkpoint 的 revision 与 runtime version、节点状态消息。
  - `HumanInteraction/README.md`：`WorkflowGateInteraction` 与 `WorkflowGateDecision` 的属性。

- [ ] **步骤 2：检查**

  `git grep -n -i -E "interactionNodeId|checkpointNodeId|definition fingerprint|humanMode" -- docs/ws-flow.md src/server` 没有结果。

- [ ] **步骤 3：提交（用户要求时）**

  ```bash
  git add docs/ws-flow.md src/server/Agw.Agents.Execution
  git commit -m "docs: describe Agentflow revisions and node status messages"
  ```

---

### 任务 17：执行协议的客户端类型与状态分发

**文件**（前缀 `src/clients/packages/`）：

- 创建：`execution-core/src/execution-status.ts`、`execution-core/src/execution-status.test.ts`
- 修改：`execution-types/src/protocol.ts`、`execution-types/type-tests/public-api.test.ts`、`execution-core/src/protocol.ts`、`execution-core/src/index.ts`、`execution-core/src/protocol.test.ts`
- 修改：`chat-runtime/src/execution-session.ts`、`execution-session-manager.ts`、`conversation-controller.ts` 及测试 `message-delivery.test.ts`、`execution-session-manager.test.ts`、`conversation-controller.test.ts`
- 修改：`chat/src/ui-web/components/message/chat.tsx`、`chat/src/ui-web/pages/chat/chat-workspace-hydration.test.ts`
- 修改：`chat-native/src/native-execution-session.ts`、`native-workspace-provider.tsx`、`native-execution-session.test.ts`；`src/clients/mobile/tests/execution-ws.test.ts`

**接口：**

- 使用（任务 11、15）：`ExecCommand.agentflowRevision`、消息 `agentflow-node-status`。
- 产出（任务 24 使用）：
  - `execution-status.ts` 的内容是设计文档第 6.5 节的代码（`AgentflowNodeStatus`、`ExecutionStatusEvent`、`getExecutionStatus`），由 `execution-core/src/index.ts` 导出。`mode-status` 的 `agentId` 读取 `additionalProperties.agentId`，实现之前用 `git grep -n "ModeStatus" -- src/server` 确认 Server 写入的属性名。`tool-mode-status` 返回 null。
  - `ExecutionCommandRequest` 增加 `agentflowRevision?: number`；`buildExecCommand` 在它大于 0 时写入 `agentflowRevision`。`chat-runtime` 的 `ExecutionTarget` 增加 `agentflowRevision?: number`。
  - `ExecutionHubHandlers` 增加 `onStatus?: (status: ExecutionStatusEvent) => void;`。`receiveMessage` 在调用处理方的位置改为：`getExecutionStatus(scopedMessage)` 有结果时调用 `handlers.onStatus`，否则调用 `handlers.onMessage`；Turn 位置的确认与 `observer.message` 保持原样。
  - `ActiveTurnSnapshot` 增加 `statuses: ExecutionStatusEvent[]`；状态事件不进入 `messages`。
  - `ConversationController`、`Chat`、`native-workspace-provider.tsx` 对 `permission-status`、`mode-status`、`mode-change-failed` 的处理移到各自的 `onStatus`；`Chat` 恢复快照时依次处理 `statuses`。`NativeExecutionSessionOptions` 增加 `onStatus?(status: ExecutionStatusEvent): void`。
  - `ChatProps`：`target` 的类型为 `(Pick<ChatTargetOption, "id" | "type"> & { agentflowRevision?: number }) | null`，并增加设计文档第 6.5 节的 `onExecutionStatus` 与 `onExecutionMessage`（含注释）。
  - Desktop renderer 的 `runtime/extensions/execution-service.ts` 不注册 `onStatus`，extension 的 `onMessage` 只收到对话消息。

- [ ] **步骤 1：编写失败的测试**

  - `execution-status.test.ts`：`getExecutionStatus reads the four status types and returns null for conversation messages`（`agentflow-node-status` 带有与不带 `interactionId`、`permission-status`、`mode-status`、`mode-change-failed` 各一条；`tool-mode-status`、普通消息、`agw-turn-start` 返回 null）。
  - `protocol.test.ts`：`buildExecCommand carries agentflowRevision`（值为 3 时写入，缺省与 0 时没有这个属性）。
  - `message-delivery.test.ts`：
    - `ExecutionSession sends status messages to onStatus and never to onMessage`
    - `a status message with a turn position advances the cursor`
    - `a session without onStatus still confirms the turn position of a status message`
  - `execution-session-manager.test.ts`：`the active turn snapshot keeps statuses apart from messages`。
  - `conversation-controller.test.ts`：现有的 `permission-status` 用例改为经状态事件到达，断言保持原样。
  - `chat-workspace-hydration.test.ts` 与 `mobile/tests/execution-ws.test.ts` 中发送 `mode-status` 的用例按新的分发方式修改。

- [ ] **步骤 2：运行测试，确认失败**

  运行：`pnpm --filter @agw/execution-core test`、`pnpm --filter @agw/chat-runtime test`
  预期：新增用例失败。

- [ ] **步骤 3：实现**

- [ ] **步骤 4：运行测试，确认通过**

  运行：`pnpm --filter @agw/execution-types test`、`pnpm --filter @agw/execution-core test`、`pnpm --filter @agw/chat-runtime test`、`pnpm --filter @agw/chat test`、`pnpm --filter @agw/chat-native test`、`pnpm --filter @agw/mobile test`、`pnpm typecheck`
  预期：全部通过。

- [ ] **步骤 5：格式化并提交（用户要求时）**

  ```bash
  git add src/clients/packages/execution-types src/clients/packages/execution-core src/clients/packages/chat-runtime src/clients/packages/chat src/clients/packages/chat-native src/clients/mobile
  git commit -m "feat(chat): dispatch execution status events apart from messages"
  ```

---

### 任务 18：对话中的 workflow-gate 与节点标识

**文件**（前缀 `src/clients/packages/`）：

- 创建：`chat/src/ui-web/components/message/workflow-gate-card.tsx`、`workflow-gate-card.test.ts`
- 修改：`execution-types/src/protocol.ts`、`chat-core/src/human-interaction.ts`、`chat/src/ui-web/components/message/human-gate-approval.tsx`、`conversation.tsx`、`chat-native/src/conversation-history.tsx`
- 测试：`chat-core/src/interaction-request.test.ts`、`human-interaction-call.test.ts`、`human-interaction.test.ts`、`chat/src/ui-web/components/message/human-gate-approval.test.ts`，以及发送带有 `mode` 的 `workflow-gate` 请求的测试（`git grep -n "workflow-gate" -- src/clients` 列出，包括 `desktop/src/main/extensions.integration.ts`）

**接口：**

- 使用（任务 5、14）：`workflow-gate` 的 `choices`、`allowResponseText`、`choiceId`，消息属性 `nodeId` 与 `nodePath`。
- 产出：
  - `execution-types`：`WorkflowGateChoice = { choiceId: string; label: string }`；`workflow-gate` 请求的字段为 `choices: WorkflowGateChoice[]`、`allowResponseText: boolean`、可选的 `inputPreview`，`mode` 删除；`source` 增加 `nodePath?: string`；回答增加 `choiceId?: string`。
  - `getPendingInteraction` 的 `workflow-gate` 分支要求 `choices` 是数组并且 `allowResponseText` 是布尔值。
  - `getAgentflowCheckpointMessage` 读取 `properties.nodeId`；`matchesHumanInteractionCall` 用 `message.additionalProperties.nodePath` 与 `target.source.nodePath` 对比。
  - `WorkflowGateCard`：

    ```ts
    type WorkflowGateCardProps = {
      request: Extract<PendingInteraction, { kind: "workflow-gate" }>;
      onChoose: (choiceId: string, responseText?: string) => void;
      onStop: () => void;
    };
    ```

    显示节点名称、`prompt`、`inputPreview`；`allowResponseText` 为 true 时显示文本框；每个 choice 一个按钮，第一个为主要按钮；"Stop workflow" 按钮调用 `onStop`。
  - `conversation.tsx` 对 `workflow-gate` 渲染 `WorkflowGateCard`，回答为 `{ kind, interactionId, approved: true, choiceId, responseText }` 或 `{ kind, interactionId, approved: false }`。`HumanGateApproval` 的 `request` 类型改为 `tool-approval`。
  - `chat-native` 的 `NativeHumanInteraction` 对 `workflow-gate` 显示同样的内容。

- [ ] **步骤 1：编写失败的测试**

  - `interaction-request.test.ts`：`workflow-gate requests expose choices and allowResponseText`；`a workflow-gate request without choices is ignored`。
  - `human-interaction.test.ts`：`checkpoint messages read nodeId`。
  - `human-interaction-call.test.ts`：现有三处 `interactionNodeId` 改为 `nodePath`，断言保持原样。
  - `workflow-gate-card.test.ts`：`WorkflowGateCard sends the chosen choiceId and the reply text`；`Stop workflow sends approved false`；`the text box is hidden when allowResponseText is false`。
  - `human-gate-approval.test.ts`：`workflow-gate` 的用例移到 `workflow-gate-card.test.ts`。

- [ ] **步骤 2：运行测试，确认失败**

  运行：`pnpm --filter @agw/chat-core test`、`pnpm --filter @agw/chat test`
  预期：新增用例失败。

- [ ] **步骤 3：实现**

- [ ] **步骤 4：运行测试，确认通过**

  运行：`pnpm --filter @agw/chat-core test`、`pnpm --filter @agw/chat-runtime test`、`pnpm --filter @agw/chat test`、`pnpm --filter @agw/chat-native test`、`pnpm --filter @agw/desktop test`、`pnpm typecheck`
  预期：全部通过。`git grep -n -E "interactionNodeId|checkpointNodeId" -- src/clients ':!src/clients/packages/api'` 没有结果。

- [ ] **步骤 5：格式化并提交（用户要求时）**

  ```bash
  git add src/clients
  git commit -m "feat(chat): answer workflow gates by choice"
  ```

---

### 任务 19：API 类型、文档 store、Save 与列表页

这个任务之后，编辑器对话框有顶栏、名称、Undo、Redo、Save 与一个只显示节点的画布；节点库、连线、表单与底部面板由任务 20 到任务 27 加入。

**文件**（前缀 `src/clients/packages/`）：

- 生成：`api/openapi.json`、`api/src/openapi.d.ts`
- 创建：`editor/agentflow-document.ts`、`agentflow-document.test.ts`、`agentflow-transfer.ts`、`agentflow-transfer.test.ts`
- 修改：`agents/src/types/agentflow.ts`、`agents/src/ui-web/pages/copy-requests.ts`、`copy-requests.test.ts`、`agents/src/ui-web/pages/agentflows/page.tsx`
- 修改：`editor/agentflow-editor-store.tsx`、`agentflow-editor-store.test.ts`、`agentflow-input-node.ts`、`agentflow-input-node.test.ts`、`visual-agentflow-dialog.tsx`、`visual-agentflow-builder.tsx`、`utils.ts`、`agentflows-table.tsx`、`execute-agentflow-drawer.tsx`
- 删除：`editor/config-json.ts`、`block-membership.ts`、`agentflow-cycle-validation.ts`、`agentflow-edge-routing.ts` 及四个测试文件（`agentflow-edge-routing.ts` 由任务 22 按新的内容创建）
- 测试：`src/clients/web/e2e/client-behavior.spec.ts`

**接口：**

- 使用（任务 7）：全部 `/api/agentflows` 接口。
- 产出（任务 20 到任务 27 使用）：

  ```ts
  // types/agentflow.ts：全部来自 components["schemas"]
  export type AgentflowDto = components["schemas"]["AgentflowDto"];
  export type AgentflowDefinition = components["schemas"]["AgentflowDefinitionDto"];
  export type AgentflowNode = components["schemas"]["AgentflowNodeDto"];
  export type AgentflowEdge = components["schemas"]["AgentflowEdgeDto"];
  export type AgentflowBlockMember = components["schemas"]["AgentflowBlockMemberDto"];
  export type AgentflowConditionRule = components["schemas"]["AgentflowConditionRuleDto"];
  export type AgentflowProblem = components["schemas"]["AgentflowProblem"];
  export type AgentflowDetailResponse = components["schemas"]["AgentflowDetailResponse"];
  export type AgentflowValidateRequest = components["schemas"]["AgentflowValidateRequest"];
  export type AgentflowRevisionDto = components["schemas"]["AgentflowRevisionDto"];
  export type AgentflowNodeKind = AgentflowNode["kind"];
  export type AgentflowRoutingStrategy = AgentflowNode["routingStrategy"];

  // agentflow-document.ts
  export type AgentflowEditorDocument = {
    name: string;
    description: string;
    nodes: AgentflowNode[];
    edges: AgentflowEdge[];
  };
  /** 节点、连线、成员与 choice 的 id。Ids for nodes, edges, members and choices. */
  export function createId(prefix: string): string;
  export function createEmptyDocument(): AgentflowEditorDocument;
  export function createEditorDocument(
    agentflow: Pick<AgentflowDto, "name" | "description">,
    definition: AgentflowDefinition,
  ): AgentflowEditorDocument;

  // agentflow-transfer.ts
  export function createAgentflowDefinition(document: AgentflowEditorDocument): AgentflowDefinition;

  // agentflow-editor-store.tsx：AgentflowEditorState 中
  agentflowId: string | null;
  baseRevision: number;
  publishedRevision: number;
  selectedNodeIds: string[];
  selectedEdgeId: string | null;
  selectedMember: { nodeId: string; memberId: string } | null;
  selectNodes(nodeIds: string[]): void;
  selectEdge(edgeId: string | null): void;
  selectMember(member: { nodeId: string; memberId: string } | null): void;
  clearSelection(): void;
  markSaved(saved: { agentflowId: string; latestRevision: number }): void;
  setPublishedRevision(revision: number): void;

  export function createAgentflowEditorStore(initial: {
    document: AgentflowEditorDocument;
    agentflowId: string | null;
    baseRevision: number;
    publishedRevision: number;
  }): AgentflowEditorStore;

  // utils.ts
  export const AGENTFLOW_RUN_PROJECT_ID = "11111111-1111-1111-1111-000000000001";
  export async function fetchAgentflowDetails(id: string, revision: number): Promise<AgentflowDetailResponse>;
  ```

  - 三个 UI 状态的注释使用设计文档第 7 节的中英文。`canvasScope`、`pendingFocusNodeId`、`selectedNodeId` 及其 action 删除。三项选择互相排除。历史（`updateDocument`、`{ group }`、`undo`、`redo`、最多 100 步）的行为保持现状。
  - `agentflow-input-node.ts` 保留 `INPUT_NODE_ID`、`createInputNode`（返回类型化的 `input` 节点，`routingStrategy` 为 `direct`）与 `isInputNode`；`ensureInputGraph`、`validateInputGraph` 删除。
  - `VisualAgentflowDialog` 的 prop `onAgentflowCreated` 改名为 `onAgentflowSaved`。打开编辑器时没有选中对象。
  - `visual-agentflow-builder.tsx` 的现有内容（`DagNode`、各个 Inspector、`ScopeBar`、两个 Palette、`validateAgentflowGraph`、`createAgentflowEditorDocument` 等）全部删除，文件只导出 `VisualAgentflowBuilder`：一个用 React Flow 默认节点显示文档中各节点名称的画布，节点可以拖动。
  - `editor/pattern-configs/` 保持原状。

- 行为（设计文档第 7、15 节中属于这个任务的部分）：
  - 顶栏：名称输入框（placeholder "Untitled Agentflow"）、版本标记（`v{latestRevision}`、"Published v{n}" 或 "Not published"、"Unsaved changes"）、Undo、Redo、Save、关闭按钮。
  - Save：`agentflowId` 为 null 时 `POST /api/agentflows`，否则 `POST /api/agentflows/revisions` 并携带 `baseRevision`；成功后 `markSaved`、toast "Saved v{n}"，对话框保持打开，调用 `onAgentflowSaved`。`4090038` 显示冲突对话框及 "Load v{n}" 与 "Save as v{n+1}"。其他错误显示 Server 的错误信息，文档保持为未保存。
  - 列表页：Version 列、Execute 的禁用条件、Edit 读取 `latestRevision` 的定义、Copy、View Mermaid chart 的 revision 选择、`DELETE /api/agentflows?id=`，文字见设计文档第 15 节"列表页"。

- [ ] **步骤 1：导出 OpenAPI 并生成类型**

  用 `--no-launch-profile --environment Development`、显式的 SQLite 连接（`.scratch/agentflow-openapi/agw.db`）、`--AgwDataDir=.scratch/agentflow-openapi/data` 与一个空闲端口启动 `src/server/Agw.Standalone.Host`，把 `/openapi/v1.json` 下载到 `src/clients/packages/api/openapi.json`，停止这个进程，再在 `src` 目录运行 `pnpm gen:api`。确认 `openapi.d.ts` 中 `AgentflowNodeDto` 是带有 `kind` 鉴别值的联合类型，`paths` 中没有带 `{id}` 的 Agentflow 路径。

- [ ] **步骤 2：编写失败的测试**

  - `agentflow-editor-store.test.ts`：现有 12 个用例改为类型化的文档并保持断言；`invalid block scope and selection are reconciled after edits` 改为 `a selection of removed nodes, edges or members is cleared after edits`。新增 `selecting a node clears edge and member selection`、`saving records the new base revision`。
  - `agentflow-document.test.ts`：`an empty document has only the Input node`；`createEditorDocument keeps node order, positions and members`。
  - `agentflow-transfer.test.ts`：`createAgentflowDefinition returns a definition that createEditorDocument reads back unchanged`。
  - `agentflow-input-node.test.ts`：保留 `createInputNode initializes a new graph`，其余四个用例删除。
  - `copy-requests.test.ts`：两个 Agentflow 用例改为类型化的请求 `{ name, description, definition }`，名称与长度的断言保持原样。
  - `client-behavior.spec.ts` 的 Agentflow 用例改名为 `Agentflow saving freezes edits, a failed write keeps the draft, and a retry shows the new version`：mock `GET /api/agentflows/detail`、`POST /api/agentflows/validate`（没有问题）、`POST /api/agentflows/revisions`（第一次 503，第二次返回 `latestRevision` 为 2）；断言保存中名称输入框不可编辑、失败后显示 "Unsaved changes"、重试后对话框保持打开并显示 `v2`。

- [ ] **步骤 3：运行测试，确认失败**

  运行：`pnpm --filter @agw/agents test`
  预期：新增与修改的用例失败。

- [ ] **步骤 4：实现**

- [ ] **步骤 5：运行测试，确认通过**

  运行：`pnpm --filter @agw/agents test`、`pnpm --filter @agw/agents typecheck`、`pnpm --filter @agw/agents lint`、`pnpm typecheck`；在仓库根目录运行 `dotnet build src/server/Agw.Standalone.Host`，再在 `src` 目录运行 `pnpm --filter @agw/web test:e2e`
  预期：全部通过。

- [ ] **步骤 6：格式化并提交（用户要求时）**

  ```bash
  git add src/clients/packages/api src/clients/packages/agents src/clients/web/e2e
  git commit -m "feat(agentflows): save typed Agentflow revisions from the editor"
  ```

---

### 任务 20：节点库、节点卡片与画布

**文件：**

- 创建：`editor/agentflow-node-meta.ts`、`agentflow-node-meta.test.ts`、`agentflow-node-library.tsx`、`agentflow-node-menu.tsx`、`agentflow-node-card.tsx`
- 修改：`editor/agentflow-document.ts`、`agentflow-document.test.ts`、`visual-agentflow-builder.tsx`

**接口：**

- 产出（任务 21 到任务 27 使用）：

  ```ts
  // agentflow-node-meta.ts
  export type AgentflowNodeMeta = {
    kind: AgentflowNodeKind;
    label: string;
    description: string;
    icon: LucideIcon;
    group: "steps" | "blocks" | null;
  };
  export const AGENTFLOW_NODE_META: Record<AgentflowNodeKind, AgentflowNodeMeta>;
  export type AgentflowNodeLookups = { agents: AgentDto[]; agentflows: AgentflowDto[]; modelProviders: ModelProviderDto[] };
  /** 节点卡片第二行的文字。The second line of a node card. */
  export function describeNode(node: AgentflowNode, lookups: AgentflowNodeLookups): string;

  // agentflow-document.ts 增加
  export type NodeReference = { kind: "agent" | "workflowAsAgent"; relateId: string; name: string };
  export function createNode(kind: AgentflowNodeKind, position: { x: number; y: number }, reference?: NodeReference): AgentflowNode;
  export function createEdge(document: AgentflowEditorDocument, sourceNodeId: string, targetNodeId: string): AgentflowEdge;
  export function isValidConnection(document: AgentflowEditorDocument, sourceNodeId: string, targetNodeId: string): boolean;
  export function addMember(document: AgentflowEditorDocument, nodeId: string, reference: NodeReference): AgentflowEditorDocument;
  export function removeMember(document: AgentflowEditorDocument, nodeId: string, memberId: string): AgentflowEditorDocument;
  ```

  - `label` 与图标见设计文档第 9 节的表格（Input、Agent、Workflow as Agent、Prompt Adapter、Clear Messages、Human Gate、Checkpoint、Output、Concurrent Block、Handoff Group、GroupChat Room、Magentic Team）。节点库条目的 `description`：

  | 种类 | `description` |
  | --- | --- |
  | Prompt Adapter | `Rewrites the input with instructions.` |
  | Clear Messages | `Continues with empty input.` |
  | Human Gate | `Waits for a person to choose a path.` |
  | Checkpoint | `Marks a point the run can resume from.` |
  | Output | `Returns the result of the flow.` |
  | Concurrent Block | `Runs every member at the same time.` |
  | Handoff Group | `Members hand the task to each other.` |
  | GroupChat Room | `Members take turns in one conversation.` |
  | Magentic Team | `A manager plans and assigns work to members.` |

  - 新节点与新连线的默认值见设计文档第 8 节最后一段与第 10 节最后一段。
  - 画布：`VisualAgentflowBuilder` 由文档用 `useMemo` 得到 React Flow 的节点（类型 `dagNode`，组件 `AgentflowNodeCard`）与连线；`snapToGrid`、`snapGrid` 为 `[20, 20]`、背景点阵间距 20px、`fitViewOptions` 为 `{ padding: 0.2, maxZoom: 1 }`、`proOptions={{ hideAttribution: true }}`。
  - 这个任务完成设计文档第 8 节的前三种增加节点的方式、第 9 节的卡片（状态标记由任务 23、24 加入，`NodeToolbar` 由任务 25 加入）与 Block 成员行、第 10 节的"连接时的即时判断"。左侧 `ResizablePanel` 默认 280px。

- [ ] **步骤 1：编写失败的测试**

  - `agentflow-node-meta.test.ts`：`every node kind has a label, an icon and a description`；`describeNode gives the second line for every kind`（设计文档第 9 节表格中的每一种文字各一条数据，包括 "No agent selected"、"External agent"、`Summary by {模型名称}`、`{n} members` 与 `max {n} rounds`）。
  - `agentflow-document.test.ts`：
    - `a new Human Gate has the choice strategy and one choice named Continue`
    - `the first member of a Magentic Team is the manager`
    - `removing the manager makes the first member the manager`
    - `a connection to Input, to the same node or in an existing direction is rejected`
    - `a new edge from a switch node takes the next order and has no conditions`
    - `a new edge from a Human Gate binds the first choice without an edge`

- [ ] **步骤 2：运行测试，确认失败**

  运行：`pnpm --filter @agw/agents exec tsx --test src/ui-web/pages/agentflows/components/agentflow-node-meta.test.ts src/ui-web/pages/agentflows/components/agentflow-document.test.ts`
  预期：失败，模块或函数不存在。

- [ ] **步骤 3：实现**

- [ ] **步骤 4：运行测试，确认通过**

  运行：`pnpm --filter @agw/agents test`、`pnpm --filter @agw/agents typecheck`、`pnpm --filter @agw/agents lint`
  预期：全部通过。

- [ ] **步骤 5：格式化并提交（用户要求时）**

  ```bash
  git add src/clients/packages/agents
  git commit -m "feat(agentflows): add the node library, node cards and member rows"
  ```

---

### 任务 21：连线的路径、标签、插入与重新连接

**文件：**

- 创建：`editor/agentflow-edge-path.ts`、`agentflow-edge-path.test.ts`、`agentflow-condition.ts`、`agentflow-condition.test.ts`、`agentflow-edge.tsx`
- 修改：`editor/agentflow-document.ts`、`agentflow-document.test.ts`、`visual-agentflow-builder.tsx`

**接口：**

- 产出：

  ```ts
  // agentflow-edge-path.ts
  export type EdgeRouteNode = { id: string; x: number; y: number; width: number; height: number };
  export type EdgeRoute = { points: { x: number; y: number }[]; label: { x: number; y: number }; path: string };
  export function routeAgentflowEdges(
    nodes: EdgeRouteNode[],
    edges: Pick<AgentflowEdge, "edgeId" | "sourceNodeId" | "targetNodeId">[],
  ): Record<string, EdgeRoute>;

  // agentflow-condition.ts
  export function describeConditions(rules: AgentflowConditionRule[]): string;
  export function describeAgentflowEdge(edge: AgentflowEdge, nodes: AgentflowNode[], edges: AgentflowEdge[]): string;

  // agentflow-document.ts 增加
  export function insertNodeOnEdge(document: AgentflowEditorDocument, edgeId: string, node: AgentflowNode): AgentflowEditorDocument;
  export function retargetEdge(document: AgentflowEditorDocument, edgeId: string, targetNodeId: string): AgentflowEditorDocument;
  ```

  - 路径的五条规则、标签位置、标签文字、颜色与线宽、插入节点与重新连接见设计文档第 10 节。`path` 是带有 10px 圆角的 SVG path。尚未测量的节点按 240×64 计算。
  - `describeConditions` 的每种 `field` 的文字见设计文档第 12 节表格的"标签文字"一列，多条规则用 ` and ` 连接。
  - `AgentflowEdge` 是 React Flow 的 edge 类型 `agentflow`，读取 `data.route`，用 `EdgeLabelRenderer` 渲染标签与 "+" 按钮。运行状态与 Problem 的颜色由任务 23、24 加入。

- [ ] **步骤 1：编写失败的测试**

  - `agentflow-edge-path.test.ts`：`same-row neighbours get a straight line`；`different rows get a two-segment route`；`a back edge on the same row passes below`；`a blocked forward edge passes above`；`edges sharing a corridor are offset`；`no segment crosses a node other than its endpoints`；`label anchors sit outside every node`。
  - `agentflow-condition.test.ts`：`descriptions for every field`；`rows join with and`；`result rules show the path`；`edge labels follow the source strategy`（等待边 `wait`、`if`、`else if`、`else`、choice 的 label、`when`、没有标签各一条）。
  - `agentflow-document.test.ts`：`inserting a node keeps the edge fields on the first edge`；`inserting on a waiting edge keeps the wait on the second edge`。

- [ ] **步骤 2：运行测试，确认失败**

  运行：`pnpm --filter @agw/agents exec tsx --test src/ui-web/pages/agentflows/components/agentflow-edge-path.test.ts src/ui-web/pages/agentflows/components/agentflow-condition.test.ts`
  预期：失败，模块不存在。

- [ ] **步骤 3：实现**

- [ ] **步骤 4：运行测试，确认通过**

  运行：`pnpm --filter @agw/agents test`、`pnpm --filter @agw/agents typecheck`、`pnpm --filter @agw/agents lint`
  预期：全部通过。

- [ ] **步骤 5：格式化并提交（用户要求时）**

  ```bash
  git add src/clients/packages/agents
  git commit -m "feat(agentflows): route edges at right angles with text labels"
  ```

---

### 任务 22：分支方式、条件表单与右侧表单

**文件：**

- 创建：`editor/agentflow-edge-routing.ts`、`agentflow-edge-routing.test.ts`、`agentflow-condition-editor.tsx`、`agentflow-inspector.tsx`
- 修改：`editor/agentflow-condition.ts`、`agentflow-condition.test.ts`、`visual-agentflow-builder.tsx`

**接口：**

- 产出：
  - `agentflow-edge-routing.ts` 导出设计文档第 11 节的六个函数（含注释），参数 `document` 与返回值的类型是 `AgentflowEditorDocument`，`strategy` 的类型是 `AgentflowRoutingStrategy`。
  - `agentflow-condition.ts` 增加：

    ```ts
    export type AgentflowConditionField = AgentflowConditionRule["field"];
    export function addConditionRule(rules: AgentflowConditionRule[], field: AgentflowConditionField): AgentflowConditionRule[];
    export function updateConditionRule(rules: AgentflowConditionRule[], index: number, patch: Partial<AgentflowConditionRule>): AgentflowConditionRule[];
    export function removeConditionRule(rules: AgentflowConditionRule[], index: number): AgentflowConditionRule[];
    /** 还可以增加的字段；result 可以有多行。Fields that can still be added; result may appear more than once. */
    export function getAvailableConditionFields(rules: AgentflowConditionRule[], sourceProducesResult: boolean): AgentflowConditionField[];
    ```

  - `AgentflowInspector` 的内容是设计文档第 13 节（四种内容）与第 11 节的 Outgoing、Incoming 区域；Last run 区域由任务 24 加入。右侧 `ResizablePanel` 默认 340px。
  - `@agw/components` 没有 segmented control：三个分支方式选项用 `ButtonGroup` 中的三个 `Button` 表示，选中项 `variant="default"`、其余 `variant="outline"`，每个按钮带有 `aria-pressed`。
  - `sourceProducesResult`：源节点是 Agent，它引用的 Agent 的 `enableSummary` 为 true 并且 `responseSchema` 不为空。路径输入框的候选项是 `responseSchema` 顶层 `properties` 的名称。

- [ ] **步骤 1：编写失败的测试**

  - `agentflow-edge-routing.test.ts`：`switching to Branch numbers the branches in list order and keeps conditions`；`leaving Branch clears Else and order`；`making a branch the Else clears its conditions`；`turning Wait on clears conditions, choice and branch fields`；`removing a choice removes its edges`；`moving a branch renumbers the branches`；`the previous Else becomes the last conditional branch`。
  - `agentflow-condition.test.ts` 增加：`result is offered only when the source produces a structured Result`；`fields other than result appear once`。

- [ ] **步骤 2：运行测试，确认失败**

  运行：`pnpm --filter @agw/agents exec tsx --test src/ui-web/pages/agentflows/components/agentflow-edge-routing.test.ts`
  预期：失败，函数不存在。

- [ ] **步骤 3：实现**

  文本输入使用现有的 `{ group }` 历史模式，同一个字段的连续输入是一步 Undo。

- [ ] **步骤 4：运行测试，确认通过**

  运行：`pnpm --filter @agw/agents test`、`pnpm --filter @agw/agents typecheck`、`pnpm --filter @agw/agents lint`
  预期：全部通过。

- [ ] **步骤 5：格式化并提交（用户要求时）**

  ```bash
  git add src/clients/packages/agents
  git commit -m "feat(agentflows): edit routing, conditions and node settings in the inspector"
  ```

---

### 任务 23：Problems、底部面板、版本与发布

**文件：**

- 创建：`editor/agentflow-problems.ts`、`agentflow-problems.test.ts`、`agentflow-bottom-panel.tsx`
- 修改：`editor/agentflow-editor-store.tsx`、`visual-agentflow-dialog.tsx`、`visual-agentflow-builder.tsx`、`agentflow-node-card.tsx`、`agentflow-edge.tsx`

**接口：**

- 产出：

  ```ts
  // agentflow-problems.ts：AgentflowProblemsState 是设计文档第 14 节的类型（含注释）
  export const EMPTY_PROBLEMS_STATE: AgentflowProblemsState;
  export type ProblemsChecker = {
    /** 文档变化后调用；immediate 为 true 时不等待。Call after the document changes; immediate skips the delay. */
    check(request: { agentflowId: string | null; document: AgentflowEditorDocument; fingerprint: string }, immediate?: boolean): void;
    dispose(): void;
  };
  export function createProblemsChecker(options: {
    validate: (request: AgentflowValidateRequest, signal: AbortSignal) => Promise<AgentflowProblem[]>;
    onChange: (state: AgentflowProblemsState) => void;
    delayMs?: number; // 缺省 400
  }): ProblemsChecker;
  /** 结果属于当前文档并且没有问题。The result belongs to the current document and has no problems. */
  export function hasPassedValidation(state: AgentflowProblemsState, currentFingerprint: string): boolean;
  ```

  - store 增加 `problems: AgentflowProblemsState` 与 `setProblems(state: AgentflowProblemsState): void`。
  - 底部面板：纵向 `ResizablePanelGroup` 的下方，收起时是 36px 的标签行，展开后默认 260px，三个标签页 Problems、Run、Versions；Run 标签页的内容由任务 24 加入，这个任务中显示空状态 "Save the Agentflow to run it."。
  - 这个任务完成设计文档第 14 节（Problems 的显示）、第 7 节顶栏的 Problems 按钮、第 15 节的 Save 禁用条件与 `title`、`4000080` 的处理、Publish 按钮、Versions 标签页，以及第 9、10 节中 "有 Problem" 的标记与颜色。

- [ ] **步骤 1：编写失败的测试**

  `agentflow-problems.test.ts`（`validate` 是测试中传入的函数，记录调用并由测试决定何时完成）：

  - `a newer request cancels the older one`：第二次 `check` 之后第一个请求的 `signal.aborted` 为 true，只有第二个结果进入 `onChange`。
  - `a stale result does not enable Save`：结果的 `fingerprint` 与当前文档不同时 `hasPassedValidation` 为 false。
  - `a failed request reports the error`：`status` 为 `failed`，`error` 为错误信息，`problems` 保持上一次的值。
  - `checks wait for the delay and the first check runs at once`。

- [ ] **步骤 2：运行测试，确认失败**

  运行：`pnpm --filter @agw/agents exec tsx --test src/ui-web/pages/agentflows/components/agentflow-problems.test.ts`
  预期：失败，模块不存在。

- [ ] **步骤 3：实现**

- [ ] **步骤 4：运行测试，确认通过**

  运行：`pnpm --filter @agw/agents test`、`pnpm --filter @agw/agents typecheck`、`pnpm --filter @agw/agents lint`、`pnpm --filter @agw/web test:e2e`
  预期：全部通过。

- [ ] **步骤 5：格式化并提交（用户要求时）**

  ```bash
  git add src/clients/packages/agents
  git commit -m "feat(agentflows): show server problems, versions and publishing in the editor"
  ```

---

### 任务 24：Run 标签页与节点运行状态

**文件：**

- 创建：`editor/agentflow-run-state.ts`、`agentflow-run-state.test.ts`
- 修改：`editor/agentflow-editor-store.tsx`、`agentflow-bottom-panel.tsx`、`agentflow-node-card.tsx`、`agentflow-edge.tsx`、`agentflow-inspector.tsx`、`visual-agentflow-dialog.tsx`、`execute-agentflow-drawer.tsx`

**接口：**

- 使用（任务 17）：`ExecutionStatusEvent`、`ChatProps` 的 `onExecutionStatus`、`onExecutionMessage` 与 `target.agentflowRevision`；`mergeStreamingMessage`（`@agw/execution-core`）。
- 产出：
  - `agentflow-run-state.ts` 的类型与三个函数是设计文档第 16 节的代码，另有 `export const EMPTY_RUN_STATE: AgentflowRunState;`。处理规则是同一节的表格。
  - store 增加 `runState: AgentflowRunState`、`applyRunStatus(status: ExecutionStatusEvent): void`、`applyRunMessage(message: AiMessage): void`、`resetRunState(): void`。
  - 这个任务完成设计文档第 15 节的 Run 标签页与顶栏的 Run、第 16 节的画布显示与 Last run 区域、第 9 节与第 10 节中 running、waiting、failed、completed 的标记与颜色。`ExecuteAgentflowDrawer` 使用 `AGENTFLOW_RUN_PROJECT_ID`。

- [ ] **步骤 1：编写失败的测试**

  `agentflow-run-state.test.ts`：`turn start clears the state`；`the last status of a node wins`；`waiting holds until a status with the same interactionId arrives`；`a late completed does not hide waiting`；`turn finish removes running and waiting`；`output is attributed by nodeId`；`member output marks the active member`（`nodePath` 为 `b/b.m1` 与 `b/b.m1.manager` 各一条）。

- [ ] **步骤 2：运行测试，确认失败**

  运行：`pnpm --filter @agw/agents exec tsx --test src/ui-web/pages/agentflows/components/agentflow-run-state.test.ts`
  预期：失败，模块不存在。

- [ ] **步骤 3：实现**

  `Chat` 首次显示后保持挂载。"New run" 更新 `sessionSeed.revision` 并调用 `resetRunState`。`messageActions` 的 "Show node on canvas" 用 `runState.nodeIdByMessageId` 查找节点。

- [ ] **步骤 4：运行测试，确认通过**

  运行：`pnpm --filter @agw/agents test`、`pnpm --filter @agw/agents typecheck`、`pnpm --filter @agw/agents lint`
  预期：全部通过。

- [ ] **步骤 5：格式化并提交（用户要求时）**

  ```bash
  git add src/clients/packages/agents
  git commit -m "feat(agentflows): run a revision in the editor and show node status on the canvas"
  ```

---

### 任务 25：选择、剪贴板、键盘与右键菜单

**文件：**

- 创建：`editor/agentflow-clipboard.ts`、`agentflow-clipboard.test.ts`
- 修改：`editor/agentflow-editor-store.tsx`、`agentflow-editor-store.test.ts`、`visual-agentflow-builder.tsx`、`visual-agentflow-dialog.tsx`、`agentflow-node-card.tsx`、`agentflow-inspector.tsx`

**接口：**

- 产出：

  ```ts
  // agentflow-clipboard.ts
  export type AgentflowClipboard = { nodes: AgentflowNode[]; edges: AgentflowEdge[] };
  /** 选中的节点（不包含 Input）与它们之间的连线；没有可以复制的节点时为 null。 */
  /** The selected nodes without Input and the edges between them; null when nothing can be copied. */
  export function copyNodes(document: AgentflowEditorDocument, nodeIds: string[]): AgentflowClipboard | null;
  export function pasteNodes(
    document: AgentflowEditorDocument,
    clipboard: AgentflowClipboard,
    placement: { offset: number } | { position: { x: number; y: number } },
  ): { document: AgentflowEditorDocument; nodeIds: string[] };
  export function deleteNodes(document: AgentflowEditorDocument, nodeIds: string[]): AgentflowEditorDocument;
  export function deleteEdge(document: AgentflowEditorDocument, edgeId: string): AgentflowEditorDocument;
  ```

  - store 增加 `clipboard: AgentflowClipboard | null`（注释使用设计文档第 7 节的中英文）与 `setClipboard(clipboard: AgentflowClipboard | null): void`。
  - 这个任务完成设计文档第 17 节的全部内容与第 9 节的 `NodeToolbar`。键盘处理沿用 `visual-agentflow-dialog.tsx` 中的 `isEditableKeyboardTarget`。右键菜单在这个任务中有 Duplicate、Copy、Delete、Insert a node、Delete connection、Add a node here 与 Paste；"Tidy up" 与 "Fit view" 两项由任务 26 加入。

- [ ] **步骤 1：编写失败的测试**

  - `agentflow-clipboard.test.ts`：`pasted nodes, edges, members and choices get new ids and consistent references`；`Input is never copied or deleted`；`deleting a branch renumbers the rest`；`deleting a node removes its edges`；`a keyboard paste offsets by 40px and a context-menu paste starts at the pointer`。
  - `agentflow-editor-store.test.ts`：`undo restores a multi-node move in one step`。

- [ ] **步骤 2：运行测试，确认失败**

  运行：`pnpm --filter @agw/agents exec tsx --test src/ui-web/pages/agentflows/components/agentflow-clipboard.test.ts`
  预期：失败，模块不存在。

- [ ] **步骤 3：实现**

- [ ] **步骤 4：运行测试，确认通过**

  运行：`pnpm --filter @agw/agents test`、`pnpm --filter @agw/agents typecheck`、`pnpm --filter @agw/agents lint`
  预期：全部通过。

- [ ] **步骤 5：格式化并提交（用户要求时）**

  ```bash
  git add src/clients/packages/agents
  git commit -m "feat(agentflows): add multi-selection, clipboard, shortcuts and context menus"
  ```

---

### 任务 26：整理布局与画布导航

**文件：**

- 创建：`editor/autoLayout.test.ts`
- 修改：`editor/autoLayout.ts`、`agentflow-document.ts`、`agentflow-document.test.ts`、`visual-agentflow-builder.tsx`

**接口：**

- 产出：

  ```ts
  // autoLayout.ts
  export function createGraphLayout(
    document: AgentflowEditorDocument,
    sizes: Record<string, { width: number; height: number }>,
    canvas: { width: number; height: number },
  ): Promise<Record<string, { x: number; y: number }>>;

  // agentflow-document.ts 增加
  export function applyNodePositions(document: AgentflowEditorDocument, positions: Record<string, { x: number; y: number }>): AgentflowEditorDocument;
  /** 标题、种类名称或成员名称匹配的节点，最多 8 个。Nodes whose title, kind label or member name matches; at most 8. */
  export function searchNodes(document: AgentflowEditorDocument, query: string): AgentflowNode[];
  ```

  - ELK 的选项、label 的估算宽高与网格吸附见设计文档第 18 节。有标签的连线是 `describeAgentflowEdge` 结果不为空的连线。
  - 这个任务完成设计文档第 18 节的画布工具条、`MiniMap`、节点查找，并把任务 21 的插入节点、任务 25 的右键菜单接到 "Tidy up" 与 "Fit view"。

- [ ] **步骤 1：编写失败的测试**

  - `autoLayout.test.ts`：`positions are snapped to the 20px grid`；`nodes flow from left to right in edge order`；`a labelled edge leaves room between its layers`（有标签时两层之间的距离不小于标签的估算宽度）。
  - `agentflow-document.test.ts`：`search matches title, kind label and member name and returns at most eight`。

- [ ] **步骤 2：运行测试，确认失败**

  运行：`pnpm --filter @agw/agents exec tsx --test src/ui-web/pages/agentflows/components/autoLayout.test.ts`
  预期：失败，签名不匹配。

- [ ] **步骤 3：实现**

- [ ] **步骤 4：运行测试，确认通过**

  运行：`pnpm --filter @agw/agents test`、`pnpm --filter @agw/agents typecheck`、`pnpm --filter @agw/agents lint`
  预期：全部通过。

- [ ] **步骤 5：格式化并提交（用户要求时）**

  ```bash
  git add src/clients/packages/agents
  git commit -m "feat(agentflows): tidy the layout and add canvas navigation"
  ```

---

### 任务 27：模板、导入与导出

**文件：**

- 创建：`editor/agentflow-templates.ts`、`agentflow-templates.test.ts`
- 修改：`editor/agentflow-transfer.ts`、`agentflow-transfer.test.ts`、`visual-agentflow-dialog.tsx`、`visual-agentflow-builder.tsx`

**接口：**

- 产出：

  ```ts
  // agentflow-templates.ts
  export type AgentflowTemplate = {
    id: string;
    name: string;
    create(): Pick<AgentflowEditorDocument, "nodes" | "edges">;
  };
  export const AGENTFLOW_TEMPLATES: AgentflowTemplate[];

  // agentflow-transfer.ts 增加
  export type AgentflowTransfer = { name: string; description: string | null; definition: AgentflowDefinition };
  export function createAgentflowTransfer(document: AgentflowEditorDocument): AgentflowTransfer;
  export type AgentflowTransferError = {
    field: "json" | "definition.nodes" | "definition.edges" | "nodeId" | "edgeId" | "kind";
    value: string;
  };
  export function parseAgentflowTransfer(
    text: string,
  ): { ok: true; transfer: AgentflowTransfer } | { ok: false; errors: AgentflowTransferError[] };
  ```

  - 五个模板的名称与结构见设计文档第 19 节的表格；Agent 节点没有 `relateId`。
  - `parseAgentflowTransfer` 的检查：`JSON.parse` 失败（`json`）；`definition.nodes` 或 `definition.edges` 不是数组；节点的 `kind` 不属于 `AGENTFLOW_NODE_META` 的键（`kind`，`value` 为 `{nodeId}: {kind}`）；`nodeId` 重复；`edgeId` 重复。id 按原始字符串区分大小写对比。
  - 这个任务完成设计文档第 19 节（更多菜单的 Templates、Import、Export，空画布的模板列表，导入与导出对话框）。

- [ ] **步骤 1：编写失败的测试**

  - `agentflow-templates.test.ts`：`every template round-trips through createAgentflowDefinition`；`every template has exactly one Input and no agent selected`。
  - `agentflow-transfer.test.ts`：
    - `An exported document can be imported.`
    - `Missing node or edge arrays produce an error.`
    - `Duplicate non-Input nodeId values and duplicate edgeId values are each reported with their field and value.`
    - `The same ID can appear once in each group.`
    - `A node with an unknown kind is reported with its nodeId and kind.`
    - `Text that is not JSON produces an error.`

- [ ] **步骤 2：运行测试，确认失败**

  运行：`pnpm --filter @agw/agents exec tsx --test src/ui-web/pages/agentflows/components/agentflow-templates.test.ts src/ui-web/pages/agentflows/components/agentflow-transfer.test.ts`
  预期：失败，模块或函数不存在。

- [ ] **步骤 3：实现**

  导入检查失败时不调用 `updateDocument`。全部检查通过后用一次 `updateDocument` 替换文档；有节点缺少 `position` 时先得到整理布局的结果，再写入。

- [ ] **步骤 4：运行测试，确认通过**

  运行：`pnpm --filter @agw/agents test`、`pnpm --filter @agw/agents typecheck`、`pnpm --filter @agw/agents lint`
  预期：全部通过。

- [ ] **步骤 5：格式化并提交（用户要求时）**

  ```bash
  git add src/clients/packages/agents
  git commit -m "feat(agentflows): add templates, import and export"
  ```

---

### 任务 28：颜色与主题变量

**文件：**

- 修改：`src/clients/desktop/src/shared/theme-tokens.ts`、`src/clients/packages/components/src/ui-tokens/tokens.css`、`src/clients/packages/extension-sdk/src/api.ts`、`src/extensions/warm-minimalism-theme/themes/warm-minimalism.json`、`src/clients/desktop/test-fixtures/extensions-src/01a173b5-526c-74d8-864c-047bc35e649f/themes/contrast.json`
- 测试：`src/clients/desktop/renderer/src/runtime/extensions/extension-host-provider.test.ts`、`src/clients/desktop/src/main/extensions/extension-theme-file.test.ts`

**接口：**

- `HOST_COLOR_TOKENS` 删除 `--agw-agentflow-edge-fan-out`、`--agw-agentflow-edge-fan-in`、`--agw-agentflow-edge-switch-case`、`--agw-agentflow-edge-switch-default`。
- `PALETTE_STEPS` 的结果：

  ```ts
  slate: [400],
  zinc: [200, 800, 900],
  red: [50, 100, 400, 700, 900, 950],
  orange: [100, 200, 400, 600, 900, 950],
  amber: [50, 100, 200, 300, 400, 500, 600, 700, 800, 900, 950],
  yellow: [100, 400, 700, 900],
  green: [50, 100, 300, 400, 600, 700, 900, 950],
  emerald: [300, 500, 600, 700],
  blue: [100, 200, 300, 400, 500, 600, 700, 900, 950],
  rose: [300, 500],
  ```

- `tokens.css`：`:root` 中删除四个连线种类变量，`--agw-agentflow-edge` 保持 `#475569`；`.dark` 中增加 `--agw-agentflow-edge: #94a3b8;`。
- `extension-sdk/src/api.ts` 中主题变量的说明与上面两项一致。
- `warm-minimalism.json`：删除 light 与 dark 中的四个连线种类变量，以及 common 中的 `--color-emerald-50`、`--color-emerald-200`、`--color-emerald-800`。`contrast.json`：删除四个连线种类变量。
- 编辑器中的颜色只使用设计文档第 20 节的表格；"Unsaved changes" 保持 `text-amber-700 dark:text-amber-400`。

- [ ] **步骤 1：编写失败的测试**

  - `extension-theme-file.test.ts`：`every color variable, including the palette, accepts a color` 改为使用 `--agw-agentflow-edge`；新增 `a removed Agentflow edge variable is an unknown variable`：主题设置 `--agw-agentflow-edge-fan-out` 时报告 `UNKNOWN_THEME_VARIABLE`。
  - `extension-host-provider.test.ts`：`the theme offered to extensions includes every variable a theme may set` 中的 `--agw-agentflow-edge-switch-default` 改为 `--agw-agentflow-edge`。

- [ ] **步骤 2：运行测试，确认失败**

  运行：`pnpm --filter @agw/desktop test`
  预期：新增用例失败。

- [ ] **步骤 3：实现**

- [ ] **步骤 4：运行测试，确认通过**

  运行：`pnpm --filter @agw/desktop test`、`pnpm --filter @agw/desktop build:renderer`（其中运行 `check-theme-tokens`）、`pnpm typecheck`
  预期：全部通过。`git grep -n -E "(zinc-50|orange-50|emerald-(50|200|800)|teal-[0-9]|sky-[0-9]|blue-50[^0-9]|indigo-[0-9]|violet-[0-9]|rose-(50|200|800))" -- src/clients/packages src/clients/web/src src/clients/desktop/renderer/src` 没有结果；`git grep -n "agw-agentflow-edge-" -- src` 没有结果。构建更新了 `desktop/renderer/next-env.d.ts` 时，把其中的引用改回构建之前的内容。

- [ ] **步骤 5：格式化并提交（用户要求时）**

  ```bash
  git add src/clients/desktop src/clients/packages/components src/clients/packages/extension-sdk src/extensions/warm-minimalism-theme
  git commit -m "feat(theme): keep one Agentflow edge variable and trim the palette list"
  ```

---

### 任务 29：Web e2e 与 Desktop 集成测试

**文件：**

- 创建：`src/clients/web/e2e/agentflow-editor.spec.ts`
- 修改：`src/clients/desktop/src/main/extensions.integration.ts`（`hostSend`、`themeEdgesAgentflow`、`AGENTFLOW_GRAPH`、`agentflowEdgeThemes`）

**接口：**

- 使用：`src/clients/web/e2e/standalone-host.ts` 的 `standaloneHostTest`；Agent 的准备方式与 `jobs-interval.spec.ts` 相同（先取 `/api/auth/antiforgery`，再带 `X-CSRF-TOKEN` 调用 `POST /api/agents`）。

- [ ] **步骤 1：编写 Web e2e 用例**

  `agentflow-editor.spec.ts`，每个用例使用隔离的 Standalone Host 与两个通过接口创建的 Agent：

  - `AgentflowEditor_SequentialTemplate_ListsProblemsUntilAgentsAreSelected`：选择 Sequential pipeline 模板，Problems 列出两个没有 Agent 的节点；选择 Agent 后显示 "No problems"。
  - `AgentflowEditor_BranchWithoutCondition_ShowsProblemThenLabel`：把一个 Agent 的分支方式改为 Branch，Problems 出现缺少条件的连线；填写条件后连线标签显示 `if contains "…"`。
  - `AgentflowEditor_HumanGateChoices_LabelsEdgesByChoice`：增加 Human Gate，新增一个 choice，两个 choice 分别连接到两个节点，连线标签是 choice 的 label。
  - `AgentflowEditor_SaveAndPublish_ShowsVersions`：Save 后对话框保持打开并显示 `v1` 与 "Not published"；修改并保存后显示 `v2`；Publish 后显示 "Published v2"，列表页的 Version 列一致。
  - `AgentflowEditor_OpenEarlierVersion_SavesAsNewRevision`：在 Versions 标签页 Open `v1`，保存后成为 `v3`，画布的节点名称与 `v1` 相同。
  - `AgentflowEditor_ConcurrentSave_ShowsConflictDialog`：两个页面打开同一个 Agentflow，先后保存，后保存的页面显示 "This Agentflow was saved somewhere else."。
  - `AgentflowEditor_ExportThenImport_ProducesSameDefinition`：导出的 JSON 导入到另一个新建的 Agentflow，保存后两者 `GET /api/agentflows/detail` 的 `definition` 相同。
  - `AgentflowEditor_ImportDuplicateIds_KeepsCanvasAndHistory`：分别导入包含重复 `nodeId` 与重复 `edgeId` 的文件，对话框显示字段名与重复值，画布内容不变，Undo 按钮保持原来的状态。

- [ ] **步骤 2：运行 Web e2e**

  在仓库根目录运行 `dotnet build src/server/Agw.Standalone.Host`，在 `src` 目录运行 `pnpm --filter @agw/web test:e2e`。
  预期：新增的 8 个用例与现有用例通过；手机宽度下不适用的用例按现有文件的写法使用 `test.skip`。

- [ ] **步骤 3：修改 Desktop 集成测试的 Agentflow 主题场景**

  - `hostSend` 的 DELETE 使用 `/api/agentflows?id=${id}`。
  - `themeEdgesAgentflow` 生成类型化的创建请求：Input（`fanOut`）、一个 `switch` 的 Agent（一条带条件的分支与一条 Else）、一个 Human Gate（`choice`）、一条等待边、Output。
  - `agentflowEdgeThemes`：默认主题下全部连线与箭头的颜色为 `rgb(71, 85, 105)`；切换到 `CONTRAST_THEME` 后全部为 `rgb(1, 2, 3)`；拖动节点后它的选中状态（`.border-primary`）与 "Unsaved changes" 在切换主题前后保持；最后按 Escape 并放弃修改。

- [ ] **步骤 4：类型检查与 lint**

  运行：`pnpm --filter @agw/desktop typecheck`、`pnpm --filter @agw/desktop lint`、`pnpm --filter @agw/web lint`
  预期：通过。

- [ ] **步骤 5：运行 Desktop 集成测试（运行前向用户确认）**

  向用户说明这次运行要回答的问题：Agentflow 主题场景在新的编辑器与新的接口下通过，其余场景的结果与基线相同。得到确认后运行 `pnpm --filter @agw/desktop test:extensions`。

- [ ] **步骤 6：格式化并提交（用户要求时）**

  ```bash
  git add src/clients/web/e2e/agentflow-editor.spec.ts src/clients/desktop/src/main/extensions.integration.ts
  git commit -m "test: cover the Agentflow editor in Web e2e and Desktop integration"
  ```

---

## 最终验证

- [ ] 在仓库根目录运行 `dotnet build Agw.slnx` 与 `dotnet test Agw.slnx`。本机配置了 PostgreSQL 时确认带有 `SkipUnless` 的用例已经运行；没有配置时向用户说明哪些用例尚未运行。
- [ ] 运行 `dotnet csharpier check .`。
- [ ] 在 `src` 目录运行 `pnpm test`、`pnpm typecheck`、`pnpm lint`、`pnpm fmt:check`。
- [ ] 运行 `pnpm --filter @agw/web test:e2e` 与 `pnpm --filter @agw/desktop build:renderer`。
- [ ] 按任务 29 步骤 5 运行 Desktop 集成测试（运行前向用户确认）。
- [ ] 启动 Standalone Host 与 Web，逐项检查设计文档"回归测试"一节：
  - 列表页的 Execute、Copy、View Mermaid chart、启用开关与删除。
  - 对话、Job、事件订阅与 A2A 执行已发布的 Agentflow；未发布的 Agentflow 不出现在目标选择中。
  - Web、Desktop、Mobile 中运行 Agentflow：对话没有空白消息，Tool approval 与 Checkpoint 的交互保持原样，Human Gate 显示 choices。
  - permission mode 与 Agent mode 的切换在 Web、Desktop、Mobile 中的显示保持原样。
  - Default 与 warm-minimalism 主题下，编辑器在浅色与深色 Mode 中的节点、连线、标签、菜单的计算颜色符合设计文档第 20 节。
- [ ] 在仓库根目录确认下列查找都没有结果：`git grep -n -w -E "AgentflowEdgeKind|AgentflowConfigurationParser|AgentflowDefinitionFingerprint" -- src tests`；`git grep -n -E "interactionNodeId|checkpointNodeId|humanMode" -- src tests docs/ws-flow.md ':!src/clients/packages/api'`。
