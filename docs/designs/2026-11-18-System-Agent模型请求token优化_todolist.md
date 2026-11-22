# System Agent 模型请求 token 优化：实施计划

> **给执行计划的 agent：** 必须使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 按任务逐个实施。步骤使用 checkbox（`- [ ]`）记录进度。

**目标：** System Agent 的相邻模型请求只追加：system 文本静态部分在前，Tool 定义按名称排序，Mode 不改变 `tools` 和 system，User Memory 与 Project Memory 索引作为会话上下文，Plan 模式与 Todo 的提示作为写入历史的尾部上下文消息，Compaction 成批执行，并精简固定部分。

**架构：** 全部修改位于 `AsAgwAgent` 组装的公共管道和 Tool Block 中，对四种 `ProviderType` 相同。`AgentCapabilityComposer` 决定 Provider 的顺序；`ToolOrderingChatClient` 排序 Tool 定义；新增的 `ConversationContextChatClient` 位于 Compaction 之后，负责会话上下文和尾部上下文消息；`AgwChatHistoryProvider` 把尾部上下文消息写入历史。

**技术栈：** .NET 10、Microsoft.Agents.AI 1.21.0（`CompactionProvider`、`ProviderSessionState<T>`、`AIContextProvider`、`LoopAgent`）、Microsoft.Extensions.AI、EF Core（SQLite）、xUnit v3。

**设计文档：** `docs/designs/2026-11-18-System-Agent模型请求token优化.md`。执行每个任务之前阅读设计文档的对应小节。

**范围：** 设计文档功能目标的第 1 至 6 项和第 13 项。第 7 至 12 项（各 Provider 的缓存请求、文件分页、Ollama 窗口、模型选择）已经在 commit 2f4f6db9 和 ae9f52ed 中。

## 全局约束

- 没有兼容性要求，代码中不保留过渡字段与过渡行为。
- 不增加 NuGet 包，不修改项目之间的引用，不修改 entity，不生成 migration，不增加错误码。
- 数值取自设计文档：清空阈值 T 为 `min(输入预算 × 50%, 64,000)`，清空目标值为 T 的 60%，截断触发阈值为输入预算的 80%，截断目标值为 T 的 80%；Plan 模式提示的间隔为 5 条 assistant 消息，第 1、6、11 条为完整说明；Todo 提醒的两个条件都是 10 条 assistant 消息。
- 会话上下文和尾部上下文消息的正文包裹在 `<system-reminder>` 与 `</system-reminder>` 中，以 user 角色发送。
- 测试：xUnit v3，命名为 `Method_Condition_ExpectedResult`，使用真正运行的管道和 SQLite，不增加 mock 库。模型端点使用记录请求的 `IChatClient`：任务 2 新建的 `RecordingModelClient`，以及测试类中已有的 `IllegalShellCallChatClient` 等。需要写入历史的测试使用 `tests/Agw.Agents.Tests/HistoryTestFixture.cs`（SQLite、`HistoryRecordingAgent`、Turn 的执行作用域）。测试运行时产生的文件保存在测试项目的输出目录或者 `.scratch/` 下。
- 本计划的测试代码中，`Ct` 表示 `TestContext.Current.CancellationToken`。
- 注释使用中文和英文两种语言，数量保持克制。新增的类使用显式构造函数。
- 格式化：`dotnet csharpier format <修改过的文件>`。
- 测试命令：`dotnet test --project tests/<项目> --filter-method "*<方法名>"`。过滤参数在本机无效时，先执行 `dotnet build tests/<项目>`，再运行 `dotnet tests/<项目>/bin/Debug/net10.0/<项目>.dll -class "<完整类名>"`。
- 修改持久化访问之后运行 `dotnet test --project tests/Agw.Architecture.Tests`。
- 每个任务最后的提交步骤只在用户要求提交时执行，提交信息使用 Conventional Commits。不使用 Git 回退代码。
- 创建子代理时，提示词要求它使用 Rider MCP 工具查找和阅读代码。
- 不修改 `docs/human/` 下的文档。不读写 `/tmp`。

## 复核重点

1. 配置了尾部上下文来源，但历史 Provider 没有实现 `IContextAttachmentWriter`：`AsAgwAgent` 在创建时抛出 `InvalidOperationException`，不发送没有写入历史的提示。测试在任务 8（`AsAgwAgent_AttachmentSourcesWithoutWriter_Throws`）。
2. 模型调用失败后用相同的输入重新运行：尾部上下文消息已经以输入的 Step 序号写入，重新运行时规则在历史中找到它，历史中只有一行。测试在任务 8（`GetResponseAsync_RerunAfterFailedModelCall_DoesNotWriteAttachmentAgain`）。
3. User Memory 和 Project Memory 索引都为空：请求中没有会话上下文消息，之后的请求也不再调用来源，直到第一条消息改变。测试在任务 7（`GetResponseAsync_AllSourcesEmpty_SendsNoContextAndDoesNotRecompute`）。
4. Plan 模式下带有 `ApprovalRequiredAIFunction` 的受限 Tool，在自动批准规则存在时被调用：直接返回 `PlanModeToolNotAllowed`，不产生审批请求，也不执行。测试在任务 5（`AsAgwAgent_PlanModeModelRequestsPnpmFmt_DoesNotInvokeShellOrRequestApproval`）。
5. Agentflow 的节点指令以 `AIContextProvider` 来源进入请求：移除 Mode 切换提示时它保持不变，仍然发送给模型。测试在任务 8（`InvokingCoreAsync_ModeChangeNotice_IsRemoved`）。

## 文件结构

| 文件 | 职责 |
| --- | --- |
| `src/server/Agw.Agents.Execution/Agents/History/ContextWindowCompactionPipeline.cs`（修改） | 两个阶段的目标值 |
| `src/server/Agw.Agents.Execution/Agents/History/AgentMessageProjection.cs`（修改） | 流式 user 消息与已写入的行合并 |
| `src/server/Agw.Tools/Runtime/ToolContribution.cs`（修改） | `DynamicContextProviders`、`ConversationContextSources`、`ContextAttachmentSources` |
| `src/server/Agw.Tools/Runtime/ConversationContextSource.cs`（新建） | `IConversationContextSource` |
| `src/server/Agw.Tools/Runtime/ContextAttachmentSource.cs`（新建） | `IContextAttachmentSource`、`ContextAttachment`、`ContextAttachmentTypes`、`ContextAttachmentScan` |
| `src/server/Agw.Tools/ToolBlocks/ToolBlockRegistry.cs`、`src/server/Agw.Tools/ToolRegistryService.cs`（修改） | 合并三个新列表，包装动态 Provider |
| `src/server/Agw.Tools/Impl/ToolBlocks/UserMemory/UserMemoryConversationContextSource.cs`（新建） | User Memory 正文 |
| `src/server/Agw.Tools/Impl/ToolBlocks/ProjectMemory/ProjectMemoryConversationContextSource.cs`（新建） | Project Memory 索引 |
| `src/server/Agw.Tools/Impl/ToolBlocks/Todo/TodoReminderAttachmentSource.cs`（新建） | `todo_reminder` 的规则和正文 |
| `src/server/Agw.Tools/Impl/ToolBlocks/Mode/PlanModeAttachmentSource.cs`（新建） | `plan_mode`、`plan_mode_exit` 的规则和正文 |
| `src/server/Agw.Tools/Infrastructure/HostManagedPropertiesAIFunction.cs`（新建） | 从发送给模型的 schema 中移除由 Host 写入的参数 |
| `src/server/Agw.Agents.Contracts/Execution/ConversationHistoryMetadata.cs`（修改） | `contextAttachmentType` 的读写 |
| `src/server/Agw.Agents.Execution/Agents/Composition/AgentCapabilityComposer.cs`、`AgentCapabilityComposition.cs`（修改） | Provider 的顺序，携带两类来源 |
| `src/server/Agw.Agents.Execution/Agents/Middleware/ModelInput/ToolOrderingChatClient.cs`（新建） | Tool 定义按名称排序 |
| `src/server/Agw.Agents.Execution/Agents/Context/Conversation/ConversationContextChatClient.cs`（新建） | 会话上下文的计算、保存和发送；尾部上下文消息的判断和追加 |
| `src/server/Agw.Agents.Execution/Agents/History/IContextAttachmentWriter.cs`（新建） | 写入尾部上下文消息的接口 |
| `src/server/Agw.Agents.Execution/Agents/History/AgwChatHistoryProvider.cs`、`HistoryRecording.cs`（修改） | 写入尾部上下文消息，移除 Mode 切换提示 |
| `src/server/Agw.Agents.Execution/Agents/Tools/AgwAgentExtensions.Tools.cs`（修改） | 管道中各 ChatClient 的位置 |
| `src/server/Agw.Agents.Execution/Agents/Context/PlanMode/PlanModeToolGuardProvider.cs`（修改）、`PlanModeToolVisibilityChatClient.cs`（删除） | Plan 模式不改变 Tool 列表 |
| `src/server/Agw.Projects/Infrastructure/ConversationHandoffProvider.cs`（修改） | 交接内容跳过尾部上下文消息 |
| `tests/Agw.Agents.Tests/AgwAgentTestHost.cs`（新建） | 测试共用：`RecordingModelClient`、`ModelRequest`、`AgwAgentTestHost` |

---

### 任务 1：Compaction 成批执行

**文件：**
- 修改：`src/server/Agw.Agents.Execution/Agents/History/ContextWindowCompactionPipeline.cs`
- 测试：`tests/Agw.Agents.Tests/ToolResultEvictionCompactionStrategyTests.cs`

**接口：**
- 产出：`ContextWindowCompactionPipeline.ToolEvictionTargetRatio = 0.6`、`ContextWindowCompactionPipeline.TruncationTargetRatio = 0.8`（`public const double`）。`Create(int, int)` 的签名不变。

- [ ] **步骤 1：编写失败的测试**

测试类中的 `CreateConversation(groups)` 每组的 Tool result 为 20,000 个字符，约 5,000 token。`Create(1_000_000, 128_000)` 的 T 为 64,000，清空目标值为 38,400。

```csharp
[Fact]
public async Task Create_TokensExceedEvictionTrigger_EvictsToTargetInOneRequest()
{
    var strategy = ContextWindowCompactionPipeline.Create(1_000_000, 128_000);

    var first = (await CompactionProvider.CompactAsync(strategy, CreateConversation(13), cancellationToken: Ct)).ToList();
    Assert.Equal(6, CountEvicted(first));

    // 再增加一组 Tool call，没有再次超过 T，之前的消息保持不变。
    // One more tool-call group stays under T, so earlier messages are unchanged.
    var next = new List<ChatMessage>(first)
    {
        new(ChatRole.Assistant, [new FunctionCallContent("call-14", "lookup")]) { MessageId = "assistant-14" },
        new(ChatRole.Tool, [new FunctionResultContent("call-14", new string('x', ResultLength))]) { MessageId = "tool-14" },
    };
    var second = (await CompactionProvider.CompactAsync(strategy, next, cancellationToken: Ct)).ToList();
    Assert.Equal(6, CountEvicted(second));
}

[Fact]
public async Task Create_TokensExceedTruncationTrigger_TruncatesToTarget()
{
    // 输入预算 80,000：T 为 40,000，截断触发阈值 64,000，截断目标值 32,000。
    // Input budget 80,000: T is 40,000, truncation triggers above 64,000 and targets 32,000.
    var strategy = ContextWindowCompactionPipeline.Create(100_000, 20_000);
    var messages = Enumerable
        .Range(1, 16)
        .Select(index => new ChatMessage(ChatRole.User, $"u{index}:" + new string('x', ResultLength)) { MessageId = $"user-{index}" })
        .ToList();

    var compacted = (await CompactionProvider.CompactAsync(strategy, messages, cancellationToken: Ct)).ToList();

    Assert.Equal(6, compacted.Count);
    Assert.StartsWith("u11:", compacted[0].Text);
}

private static int CountEvicted(IEnumerable<ChatMessage> messages) =>
    messages
        .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
        .Count(result => Equals(result.Result, ToolResultEvictionCompactionStrategy.EvictedResultText));
```

同一个文件中 `Create_LargeContextWindow_EvictsOldToolResultsAbove64000Tokens` 的 `[InlineData(13, 1)]` 改为 `[InlineData(13, 6)]`。

- [ ] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-method "*Create_TokensExceed*"`
预期：两个测试失败，清空数量为 1，截断后的消息数量大于 6。

- [ ] **步骤 3：在 `ContextWindowCompactionPipeline.Create` 中为两个阶段传入 `target`**

`ToolResultEvictionCompactionStrategy` 的第三个参数为 `CompactionTriggers.TokensBelow((int)(toolEvictionTokens * ToolEvictionTargetRatio) + 1)`。`TruncationCompactionStrategy` 的第三个参数为 `CompactionTriggers.TokensBelow((int)(toolEvictionTokens * TruncationTargetRatio) + 1)`。`TokensBelow(n)` 的含义是小于 n，加 1 之后表示不超过目标值。同时更新类的 `<remarks>`，写明两个目标值。

- [ ] **步骤 4：运行测试，确认通过**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-method "*Create_*"`
预期：`ToolResultEvictionCompactionStrategyTests` 中以 `Create_` 开头的测试全部通过。

- [ ] **步骤 5：格式化并提交**

```bash
dotnet csharpier format src/server/Agw.Agents.Execution/Agents/History/ContextWindowCompactionPipeline.cs tests/Agw.Agents.Tests/ToolResultEvictionCompactionStrategyTests.cs
git commit -m "perf(agents): compact history in batches"
```

---

### 任务 2：LoopAgent 的继续执行提示只保存一行

**文件：**
- 修改：`src/server/Agw.Agents.Execution/Agents/History/AgentMessageProjection.cs`（`Append` 中查找已完成 entry 的条件，约 82 至 88 行）
- 新建：`tests/Agw.Agents.Tests/AgwAgentTestHost.cs`
- 测试：`tests/Agw.Agents.Tests/AgentMessageProjectionTests.cs`、`tests/Agw.Agents.Tests/HistoryRecordingAgentTests.cs`

**接口：**
- `Append(ChatMessage message, bool newMessage = false, int? stepIndex = null)` 和 `Put(ChatMessage message, int? stepIndex = null, bool replaceStreamed = true)` 的签名不变。
- 产出（测试共用，之后的任务使用）：

```csharp
namespace Agw.Agents.Tests;

internal sealed record ModelRequest(IReadOnlyList<ChatMessage> Messages, string? Instructions, IReadOnlyList<string> ToolNames);

// 记录每次模型请求的消息、Instructions 和 Tool 名称，并按 reply 返回 assistant 的内容。
// Records each model request's messages, instructions and tool names, and answers with the contents reply returns.
internal sealed class RecordingModelClient : IChatClient
{
    // reply 接收请求序号（从 0 开始）和请求消息；为 null 时每次返回文本 "done"；抛出异常表示模型调用失败。
    // reply receives the zero-based request index and the request messages; null answers "done"; throwing fails the model call.
    public RecordingModelClient(Func<int, IReadOnlyList<ChatMessage>, IList<AIContent>>? reply = null);

    public List<ModelRequest> Requests { get; }
}

internal static class AgwAgentTestHost
{
    public static ResolvedAgentDefinition Definition(ChatHistoryProvider history, AIContextProvider? compactionProvider = null);

    public static AgentCapabilityComposition Capabilities(
        IReadOnlyList<AITool>? tools = null,
        IReadOnlyList<AIContextProvider>? contextProviders = null,
        IReadOnlyList<LoopEvaluator>? loopEvaluators = null,
        IReadOnlyList<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>>? autoApprovalRules = null,
        IReadOnlySet<string>? planModeAllowedToolNames = null);

    // 在 fixture.BeginTurn() 的作用域中以流式方式运行一次并读完输出。
    // Runs one streaming turn inside fixture.BeginTurn() and drains the output.
    public static Task RunTurnAsync(HistoryTestFixture fixture, AIAgent agent, AgentSession session, params ChatMessage[] input);
}
```

`RecordingModelClient` 的普通请求和流式请求都记录消息的副本、`ChatOptions.Instructions` 和 `ChatOptions.Tools` 的名称，再返回一条 assistant 消息，每次请求使用新的 `ResponseId` 和 `MessageId`。`Definition` 和 `Capabilities` 的写法与 `AgwAgentExtensionsTests.CreateDefinition`、`CreateCapabilities` 相同。

背景：`LoopAgent` 把继续执行提示作为下一次迭代的输入，`MessageId` 为 `Guid.NewGuid().ToString("N")`。`AgwChatHistoryProvider` 在 Step N 用 `Put` 写入它，行 Id 是这个 GUID，随后 Step 序号增加。`LoopAgent` 在这次迭代的第一段输出到达时才把提示输出到流中，`HistoryRecording.RecordAsync` 在 Step N+1 用 `Append` 记录它；`Append` 查找已完成的 entry 时要求 Step 序号相同，因此新建了一行。

- [ ] **步骤 1：新建 `AgwAgentTestHost.cs`**

按“接口”编写三个类型。

- [ ] **步骤 2：编写失败的测试**

`AgentMessageProjectionTests`：

```csharp
[Fact]
public void Append_UserMessagePutInEarlierStep_ReusesExistingRow()
{
    var projection = CreateProjection();
    var id = Guid.NewGuid().ToString("N");
    var put = projection.Put(new ChatMessage(ChatRole.User, "continue") { MessageId = id }, stepIndex: 4);

    var appended = projection.Append(new ChatMessage(ChatRole.User, "continue") { MessageId = id }, stepIndex: 5);

    var row = Assert.Single(projection.ReadMessages());
    Assert.Equal(put.MessageId, row.MessageId);
    Assert.Equal(put.MessageId, appended.MessageId);
}

[Fact]
public void Append_AssistantMessageWithSameSourceInLaterStep_CreatesNewRow()
{
    var projection = CreateProjection();
    projection.Put(new ChatMessage(ChatRole.Assistant, "a") { MessageId = "chatcmpl-1" }, stepIndex: 1);

    projection.Append(new ChatMessage(ChatRole.Assistant, "b") { MessageId = "chatcmpl-1" }, stepIndex: 2);

    Assert.Equal(2, projection.ReadMessages().Count);
}
```

`HistoryRecordingAgentTests`（`ContinueOnceEvaluator` 是测试文件内的 `LoopEvaluator`：第一次判断返回 `LoopEvaluation.Continue("keep going")`，之后返回 `LoopEvaluation.Stop()`）：

```csharp
[Fact]
public async Task RunStreamingAsync_LoopAgentFeedback_PersistsOneRow()
{
    using var owner = HistoryTestFixture.EnterUser();
    await using var fixture = await HistoryTestFixture.CreateAsync();
    var model = new RecordingModelClient();
    var history = fixture.CreateProvider();
    await using var capabilities = AgwAgentTestHost.Capabilities(loopEvaluators: [new ContinueOnceEvaluator()]);
    var agent = HistoryTestFixture.Record(
        model.AsAgwAgent(AgwAgentTestHost.Definition(history), capabilities, NullLoggerFactory.Instance, fixture.Services),
        history
    );
    var session = await fixture.CreateSessionAsync(agent);

    await AgwAgentTestHost.RunTurnAsync(fixture, agent, session, new ChatMessage(ChatRole.User, "question"));
    await AgwAgentTestHost.RunTurnAsync(fixture, agent, session, new ChatMessage(ChatRole.User, "next"));

    Assert.Equal(3, model.Requests.Count);
    Assert.Single(await fixture.ReadMessagesAsync(), message => message.Text == "keep going");
    Assert.Single(model.Requests[2].Messages, message => message.Text == "keep going");
}
```

- [ ] **步骤 3：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-method "*Append_UserMessagePutInEarlierStep*"` 和 `--filter-method "*RunStreamingAsync_LoopAgentFeedback*"`
预期：两个测试失败，`keep going` 出现两次。

- [ ] **步骤 4：修改 `Append` 的查找条件**

查找已完成 entry 的条件中，Step 序号的判断改为：`value.StepIndex == null || value.StepIndex == stepIndex || message.Role == ChatRole.User`。assistant 和 tool 消息仍然要求 Step 序号相同。

- [ ] **步骤 5：运行测试，确认通过**

运行 `Agw.Agents.Tests.AgentMessageProjectionTests` 和 `Agw.Agents.Tests.HistoryRecordingAgentTests` 两个类。
预期：全部通过。

- [ ] **步骤 6：格式化并提交**

```bash
git commit -m "fix(agents): keep one history row for loop continuation prompts"
```

---

### 任务 3：动态 Provider 与 Instructions 的顺序

**文件：**
- 修改：`src/server/Agw.Tools/Runtime/ToolContribution.cs`
- 修改：`src/server/Agw.Tools/ToolBlocks/ToolBlockRegistry.cs`（包装 Provider 的位置约 246 至 253 行，`AddContribution` 约 280 行）
- 修改：`src/server/Agw.Tools/ToolRegistryService.cs`（`AddContribution` 约 776 行）
- 修改：`src/server/Agw.Tools/Impl/ContextualTools/Shell/ShellContextualTool.cs:88`、`:91`
- 修改：`src/server/Agw.Tools/Impl/ToolBlocks/BackgroundAgents/BackgroundAgentsToolBlock.cs:63`
- 修改：`src/server/Agw.Agents.Execution/Agents/Composition/AgentCapabilityComposer.cs`
- 测试：`tests/Agw.Tools.Tests/ToolBlockRegistryTests.cs`、`tests/Agw.Agents.Tests/AgentCapabilityComposerTests.cs`

**接口：**
- 产出：`ToolContribution.DynamicContextProviders`（`public List<AIContextProvider> DynamicContextProviders { get; } = [];`），存放 Instructions 属于 system 动态部分的 Provider。
- 产出：`AgentCapabilityComposition.ContextProviders` 的顺序为：Tool 的静态 Provider → Tool Block 的静态 Provider → `AgwWorkspaceProvider` → Tool 的动态 Provider → Tool Block 的动态 Provider → `DeferredHumanInteractionProvider`。`AgentSkillsProvider` 仍由 `CreateDefinitionAgentAsync` 追加在最后。

- [ ] **步骤 1：编写失败的测试**

`ToolBlockRegistryTests`：`RecordingToolBlock` 的构造函数增加可选参数 `Action<ToolContribution>? contribute`，在 `MaterializeAsync` 中对返回的 `ToolContribution` 调用。`EmptyContextProvider` 是测试文件内返回空 `AIContext`、`StateKeys` 为空的 `AIContextProvider`。

```csharp
[Theory]
[InlineData(false)]
[InlineData(true)]
public async Task MaterializeAsync_BlockProviders_WrapsDynamicContributionAsDynamic(bool dynamic)
{
    var block = new RecordingToolBlock(
        "mixed",
        ToolBlockScope.Agent,
        contribute: contribution =>
        {
            contribution.ContextProviders.Add(new EmptyContextProvider());
            if (dynamic)
                contribution.DynamicContextProviders.Add(new EmptyContextProvider());
        }
    );

    await using var contribution = await new ToolBlockRegistry([block]).MaterializeAsync(
        [new TestToolBlockDefinition("mixed")],
        ToolBlockScope.Agent,
        CreateContext(),
        Ct
    );

    var wrapped = Assert.Single(dynamic ? contribution.DynamicContextProviders : contribution.ContextProviders);
    Assert.IsType<AgwToolMetadataContextProvider>(wrapped);
    Assert.Empty(dynamic ? contribution.ContextProviders : contribution.DynamicContextProviders);
}
```

`AgentCapabilityComposerTests`。测试文件内增加三个类型：`NamedContextProvider(string name)` 是带有 `Name` 属性、返回空 `AIContext` 的 `AIContextProvider`；`ProviderToolBlock(string id, bool dynamic)` 是 `IToolBlock`，Descriptor 的 id 为参数值、scope 为 `ToolBlockScope.Agent | ToolBlockScope.Project`、没有成员，`MaterializeAsync` 把 `new NamedContextProvider(id)` 加入 `DynamicContextProviders`（`dynamic` 为真）或者 `ContextProviders`；`NamedToolBlockDefinition(string name)` 是 `GetDefinitionName()` 返回参数值的 `ToolBlockDefinition<EmptyToolOptions>`。

```csharp
[Theory]
[InlineData("file-access,notification,todo")]
[InlineData("notification,todo,file-access")]
public async Task ComposeAsync_StaticAndDynamicProviders_OrdersStaticInstructionsBeforeDynamic(string configuredOrder)
{
    await using var database = await TestDatabase.CreateAsync(Ct);
    var composer = CreateComposer(
        database.Context,
        database.ExtensionCapabilities,
        CreateToolRegistry(database.Services),
        new StubConnectionCapabilityResolver((_, _, _) => CreateResolution()),
        new StubMcpToolMaterializer((_, _, _) => CreateToolLease([], [])),
        [
            new ProviderToolBlock("todo", dynamic: false),
            new ProviderToolBlock("file-access", dynamic: false),
            new ProviderToolBlock("notification", dynamic: true),
        ]
    );
    var agent = new Agent
    {
        Type = AgentType.System,
        Tools =
        [
            .. configuredOrder.Split(',').Select(name => new ToolBlockValue { Definition = new NamedToolBlockDefinition(name) }),
        ],
    };

    await using var composition = await composer.ComposeAsync(
        agent,
        new Project { Id = Guid.CreateVersion7() },
        new Dictionary<string, string>(),
        Ct
    );

    Assert.Equal(
        ["todo", "file-access", "workspace", "notification"],
        composition.ContextProviders.Select(provider =>
            provider is AgwWorkspaceProvider
                ? "workspace"
                : provider.GetService<NamedContextProvider>()?.Name ?? provider.GetType().Name
        )
    );
}
```

- [ ] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-method "*ComposeAsync_StaticAndDynamic*"`
预期：编译失败，`DynamicContextProviders` 不存在。

- [ ] **步骤 3：`ToolContribution` 增加 `DynamicContextProviders`，两个 `AddContribution` 合并它**

`ToolBlockRegistry.AddContribution` 和 `ToolRegistryService` 的 `AddContribution` 各增加一行 `destination.DynamicContextProviders.AddRange(contribution.DynamicContextProviders);`。

- [ ] **步骤 4：`ToolBlockRegistry` 包装 Provider 时处理动态 Provider**

一个 Tool Block 的 `ContextProviders` 与 `DynamicContextProviders` 合并为一个 `AgwToolMetadataContextProvider`（静态的排在前面）。这个 Tool Block 提供了动态 Provider 时，包装结果放入 `DynamicContextProviders`，否则放入 `ContextProviders`。两个列表都为空并且存在动态 metadata 时，保持现有的 `AgwException`。

- [ ] **步骤 5：Tool 和 Tool Block 标出动态 Provider**

`ShellContextualTool` 的 `ShellEnvironmentProvider` 和 `DockerDirectoryProvider`、`BackgroundAgentsToolBlock` 的 `BackgroundAgentsProvider` 改为加入 `DynamicContextProviders`。

- [ ] **步骤 6：`AgentCapabilityComposer` 排列 Provider**

- `executableToolDefinitions` 按 `GetDefinitionName()` 在 `ToolDefinitionNames.All` 中的位置排序，`toolBlockDefinitions` 按它在 `ToolBlockDefinitionNames.All` 中的位置排序，之后再交给两个 registry。不在列表中的名称排在最后，保持配置中的相对顺序（`OrderBy` 是稳定排序）。
- `AddContribution` 把静态和动态 Provider 分别收集到两个列表。`ComposeAsync` 结束前按“接口”中的顺序组成 `contextProviders`。
- `toolPolicy == BackgroundToolPolicy.None` 的分支只包含 `AgwWorkspaceProvider`。External Agent 的分支保持不变。

- [ ] **步骤 7：运行测试，确认通过**

运行 `Agw.Agents.Tests.AgentCapabilityComposerTests` 与 `Agw.Tools.Tests.ToolBlockRegistryTests` 两个类，以及 `dotnet test --project tests/Agw.Tools.Tests`。
预期：全部通过。

- [ ] **步骤 8：格式化并提交**

```bash
git commit -m "perf(agents): place static instructions before dynamic ones"
```

---

### 任务 4：Tool 定义按名称排序

**文件：**
- 新建：`src/server/Agw.Agents.Execution/Agents/Middleware/ModelInput/ToolOrderingChatClient.cs`
- 修改：`src/server/Agw.Agents.Execution/Agents/Tools/AgwAgentExtensions.Tools.cs`（`BuildChatClient`，`PlanModeToolVisibilityChatClient` 之后）
- 测试：`tests/Agw.Agents.Tests/ToolOrderingChatClientTests.cs`（新建）

**接口：**
- 使用：`RecordingModelClient`（任务 2）。
- 产出：`internal sealed class ToolOrderingChatClient : DelegatingChatClient`，构造函数 `ToolOrderingChatClient(IChatClient innerClient)`。`ChatOptions.Tools` 多于 1 个时，在 `ChatOptions` 的副本上把 Tool 按 `StringComparer.Ordinal` 的名称顺序排列，与 `OpenAiChatCompletionsPromptCachingChatClient` 的规则相同。

- [ ] **步骤 1：编写失败的测试**

```csharp
[Theory]
[InlineData(false)]
[InlineData(true)]
public async Task GetResponseAsync_ToolsFromAllSources_SortsToolsByName(bool streaming)
{
    var inner = new RecordingModelClient();
    using var client = new ToolOrderingChatClient(inner);
    var options = new ChatOptions
    {
        Tools = [Function("b_tool"), new HostedWebSearchTool(), Function("a_tool"), Function("A_tool")],
    };
    var original = options.Tools.Select(tool => tool.Name).ToArray();
    ChatMessage[] messages = [new(ChatRole.User, "hi")];

    if (streaming)
        await foreach (var _ in client.GetStreamingResponseAsync(messages, options, Ct)) { }
    else
        await client.GetResponseAsync(messages, options, Ct);

    Assert.Equal(original.Order(StringComparer.Ordinal), inner.Requests[0].ToolNames);
    Assert.Equal("A_tool", inner.Requests[0].ToolNames[0]);
    // 调用方传入的 ChatOptions 保持原来的顺序。The caller's ChatOptions keeps its order.
    Assert.Equal(original, options.Tools.Select(tool => tool.Name));
}

private static AIFunction Function(string name) =>
    AIFunctionFactory.Create((Func<string>)(() => name), new AIFunctionFactoryOptions { Name = name });
```

- [ ] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-method "*SortsToolsByName*"`
预期：编译失败，`ToolOrderingChatClient` 不存在。

- [ ] **步骤 3：编写 `ToolOrderingChatClient`，并加入管道**

`GetResponseAsync` 和 `GetStreamingResponseAsync` 共用一个 `Order(ChatOptions?)`：Tool 不多于 1 个时原样返回；否则 `options.Clone()` 后设置排序过的列表。在 `BuildChatClient` 中 `PlanModeToolVisibilityChatClient` 之后无条件加入：`chatClientBuilder.Use(static innerClient => new ToolOrderingChatClient(innerClient));`。

- [ ] **步骤 4：运行测试，确认通过**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-method "*SortsToolsByName*"`
预期：通过。再运行 `Agw.Agents.Tests.AgwAgentExtensionsTests` 类，确认没有测试依赖 Tool 的原始顺序。

- [ ] **步骤 5：格式化并提交**

```bash
git commit -m "perf(agents): send tool definitions in name order"
```

---

### 任务 5：Plan 模式不改变 Tool 列表

**文件：**
- 修改：`src/server/Agw.Agents.Execution/Agents/Context/PlanMode/PlanModeToolGuardProvider.cs`
- 删除：`src/server/Agw.Agents.Execution/Agents/Context/PlanMode/PlanModeToolVisibilityChatClient.cs`
- 修改：`src/server/Agw.Agents.Execution/Agents/Tools/AgwAgentExtensions.Tools.cs`（删除 `PlanModeToolVisibilityChatClient` 和 `hasModeProvider` 参数）
- 测试：`tests/Agw.Agents.Tests/PlanModeToolGuardProviderTests.cs`、`tests/Agw.Agents.Tests/AgwAgentExtensionsTests.cs`

**接口：**
- 产出：`PlanModeRestrictedAIFunction` 的构造函数最后一个参数改名为 `bool planModeActive`，属性改名为 `internal bool PlanModeActive { get; }`。`PlanModeActive` 为 `true` 时，`GetService(typeof(ApprovalRequiredAIFunction))` 返回 `null`。
- `PlanModeToolGuardProvider.EnforcementInstructions` 在本任务中保留，在任务 8 删除。

- [ ] **步骤 1：编写失败的测试**

`PlanModeToolGuardProviderTests` 中的 `InvokingAsync_PlanMode_ExposesOnlyExplicitlyAllowedTools` 改为：

```csharp
[Fact]
public async Task InvokingAsync_PlanMode_KeepsAllToolsAndWrapsRestrictedOnes()
{
    var guard = new PlanModeToolGuardProvider(
        CreateModeProvider("plan"),
        new HashSet<string>(["project_memory_read"], StringComparer.OrdinalIgnoreCase)
    );
    var hosted = new HostedWebSearchTool();

    var context = await InvokeAsync(
        guard,
        new TestAgentSession(),
        [CreateFunction("project_memory_read"), CreateFunction("run_shell"), hosted]
    );
    var tools = context.Tools!.ToArray();

    Assert.Equal(["project_memory_read", "run_shell", hosted.Name], tools.Select(static tool => tool.Name));
    Assert.IsNotType<PlanModeRestrictedAIFunction>(tools[0]);
    Assert.True(Assert.IsType<PlanModeRestrictedAIFunction>(tools[1]).PlanModeActive);
    Assert.Same(hosted, tools[2]);
    Assert.Contains(PlanModeToolGuardProvider.EnforcementInstructions, context.Instructions);
}
```

`AgwAgentExtensionsTests.AsAgwAgent_PlanModeModelRequestsPnpmFmt_DoesNotInvokeShellOrRequestApproval` 中关于 `run_shell` 和 `run_skill_script` 的两个 `ExposedToolNames` 断言改为下面两行，其余断言（没有执行、没有 `ToolApprovalRequestContent`、结果包含 `403_0003 PlanModeToolNotAllowed`）保持不变：

```csharp
Assert.Contains("run_shell", client.ExposedToolNames);
Assert.Contains("run_skill_script", client.ExposedToolNames);
```

- [ ] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-method "*PlanMode*"`
预期：上面两个测试失败，Plan 模式的请求中没有 `run_shell`。

- [ ] **步骤 3：修改 `PlanModeToolGuardProvider.InvokingCoreAsync`**

两种 Mode 都保留全部 Tool。对不在 `_allowedToolNames` 中、或者名称重复的 `AIFunction`，调用 `WrapRestrictedFunction(tool, session, planModeActive: !executeMode)`。其他类型的 Tool（例如 hosted Tool）在两种 Mode 下原样保留。Plan 模式下仍然追加 `EnforcementInstructions`。

- [ ] **步骤 4：删除 `PlanModeToolVisibilityChatClient`**

删除文件，删除 `BuildChatClient` 中对它的使用和 `hasModeProvider` 参数。`ToolOrderingChatClient` 留在原来的位置。检查 `git grep -n -E "HideFromModel|PlanModeToolVisibilityChatClient" -- src tests`，没有剩余的引用。

- [ ] **步骤 5：更新 `PlanModeToolGuardProviderTests` 中其余的测试**

`InvokingAsync_PlanMode_DuplicateAllowedNameFailsClosed`、`InvokingAsync_WithoutSession_FailsClosedAsPlanMode`、`InvokingAsync_UnknownMode_FailsClosedAsPlanMode`、`InvokingAsync_PlanMode_FullAccessCannotAutoApproveRestrictedTool`、`InvokingAsync_ExecuteMode_PreservesToolsAndWrapsOnlyRestrictedFunctions`、`InvokingAsync_ExecuteMode_DuplicateAllowedNameRemainsGuardedAfterPlanSwitch`：`HideFromModel` 改为 `PlanModeActive`；断言 Plan 模式下 Tool 被移除的地方，改为断言这个 Tool 存在、类型为 `PlanModeRestrictedAIFunction` 并且 `PlanModeActive` 为真。

- [ ] **步骤 6：运行测试，确认通过**

运行 `Agw.Agents.Tests.PlanModeToolGuardProviderTests` 和 `Agw.Agents.Tests.AgwAgentExtensionsTests` 两个类，以及 `dotnet test --project tests/Agw.Tools.Tests`。
预期：全部通过。

- [ ] **步骤 7：格式化并提交**

```bash
git commit -m "perf(agents): keep the tool list unchanged in plan mode"
```

---

### 任务 6：上下文来源的类型与四个 Tool Block 的来源

**文件：**
- 新建：`src/server/Agw.Tools/Runtime/ConversationContextSource.cs`、`src/server/Agw.Tools/Runtime/ContextAttachmentSource.cs`
- 修改：`src/server/Agw.Tools/Runtime/ToolContribution.cs`、`ToolBlockRegistry.cs`、`ToolRegistryService.cs`
- 修改：`src/server/Agw.Agents.Contracts/Execution/ConversationHistoryMetadata.cs`
- 新建：`UserMemoryConversationContextSource.cs`、`ProjectMemoryConversationContextSource.cs`、`TodoReminderAttachmentSource.cs`、`PlanModeAttachmentSource.cs`（位置见“文件结构”）
- 修改：`UserMemoryToolBlock.cs`、`ProjectMemoryToolBlock.cs`、`ProjectMemoryProvider.cs`、`TodoToolBlock.cs`、`ModeToolBlock.cs`
- 修改：`src/server/Agw.Agents.Execution/Agents/Composition/AgentCapabilityComposition.cs`、`AgentCapabilityComposer.cs`
- 修改：`tests/Agw.Agents.Tests/AgwAgentTestHost.cs`
- 测试：`tests/Agw.Tools.Tests/ContextAttachmentSourceTests.cs`（新建）、`tests/Agw.Tools.Tests/UserMemoryProviderTests.cs`、`tests/Agw.Tools.Tests/ProjectMemoryProviderTests.cs`、`tests/Agw.Tools.Tests/ProjectMemoryToolBlockTests.cs`、`tests/Agw.Agents.Tests/AgentCapabilityComposerTests.cs`

**接口：**

```csharp
namespace Agw.Tools.Runtime;

public interface IConversationContextSource
{
    string Title { get; }

    // 正文为空时返回 null。Returns null when there is no content.
    ValueTask<string?> GetContentAsync(CancellationToken cancellationToken);
}

public sealed record ContextAttachment(string Type, string Text);

public interface IContextAttachmentSource
{
    // messages 是 Compaction 之后的消息列表。messages is the list after compaction.
    ValueTask<ContextAttachment?> EvaluateAsync(
        AgentSession session,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken);
}

public static class ContextAttachmentTypes
{
    public const string PlanMode = "plan_mode";
    public const string PlanModeExit = "plan_mode_exit";
    public const string TodoReminder = "todo_reminder";
}

public static class ContextAttachmentScan
{
    // 最后一条满足 predicate 的消息之后的 assistant 消息数量；没有满足的消息时统计全部。
    // Counts assistant messages after the last match, or all of them when nothing matches.
    public static int CountAssistantMessagesAfterLast(IReadOnlyList<ChatMessage> messages, Func<ChatMessage, bool> predicate);
}
```

- `ToolContribution.ConversationContextSources`（`List<IConversationContextSource>`）、`ToolContribution.ContextAttachmentSources`（`List<IContextAttachmentSource>`），两个 `AddContribution` 合并它们。
- `ConversationHistoryMetadata.ContextAttachmentTypeKey = "contextAttachmentType"`、`static string? GetContextAttachmentType(ChatMessage message)`、`static void SetContextAttachmentType(ChatMessage message, string type)`。读取按 `value?.ToString()`，与 `IsModelHistoryExcluded` 的写法相同，从历史读出的 `JsonElement` 也能得到类型。
- `AgentCapabilityComposition.ConversationContextSources`、`AgentCapabilityComposition.ContextAttachmentSources`（`IReadOnlyList<…>`）。构造函数在最后增加两个可选参数 `conversationContextSources`、`contextAttachmentSources`，默认空列表。`AgentCapabilityComposer` 把 Tool Block 的两类来源按 Tool Block 的顺序放入，External Agent 的分支不放入。
- `AgwAgentTestHost.Capabilities` 在最后增加同名的两个可选参数。
- `ProjectMemoryProvider.ReadIndexAsync(CancellationToken)`：`internal ValueTask<string?>`，返回 `memories.md` 的内容，空白时返回 `null`。
- 四个来源：

| 类 | 构造函数 | 行为 |
| --- | --- | --- |
| `UserMemoryConversationContextSource` | `(UserMemoryProvider provider)` | `Title` 为 `User Memory`，正文为 `CreateContextMessageAsync` 返回消息的 `Text`，没有 Memory 时为 `null` |
| `ProjectMemoryConversationContextSource` | `(ProjectMemoryProvider provider)` | `Title` 为 `Project Memory Index`，正文为 `ReadIndexAsync` 的结果 |
| `TodoReminderAttachmentSource` | `(AgwTodoProvider provider)` | 见下面的规则 |
| `PlanModeAttachmentSource` | `(AgentModeProvider provider)` | 见下面的规则 |

`TodoReminderAttachmentSource`（常量 `AssistantMessagesSinceTodoCall = 10`、`AssistantMessagesBetweenReminders = 10`）：
- `sinceCall` 为最后一条包含名称以 `todos_` 开头的 `FunctionCallContent` 的消息之后的 assistant 消息数量。
- `sinceReminder` 为最后一条 `todo_reminder` 之后的 assistant 消息数量。
- 两者都不小于 10 时返回 `todo_reminder`，正文为下面的文本，列表部分由 `AgwTodoProvider.FormatTodoListMessage` 对 `GetAllTodosAsync` 的结果生成：

```text
The todo tools have not been used recently. Use them when the current work benefits from tracking, and ignore this reminder when it does not apply.

{todo list}
```

`PlanModeAttachmentSource`（常量 `AssistantMessagesBetweenAttachments = 5`、`FullReminderEvery = 5`）：
- 当前 Mode 为 `plan`：统计最近一条 `plan_mode_exit` 之后的 `plan_mode` 数量 `count`。`count` 为 0，或者最后一条 `plan_mode` 之后已有 5 条 assistant 消息时，返回 `plan_mode`；`count % 5 == 0` 时使用完整正文，否则使用简短正文。
- 当前 Mode 为 `execute`：消息列表中最近的一条 Mode 提示是 `plan_mode` 时，返回 `plan_mode_exit`。
- 完整正文：

```text
Plan mode is active.
{ModeToolBlock.PlanModeInstructions}
Plan mode is enforced by the server. Tools that run shell commands or change files, project memory, background tasks, Connections, or MCP resources return PlanModeToolNotAllowed while Plan mode is active.
```

- 简短正文：`Plan mode is still active. Follow the Plan mode instructions given earlier in this conversation.`
- 退出正文：`Plan mode has ended. The conversation is in Execute mode.`

- [ ] **步骤 1：编写失败的测试**

`ContextAttachmentSourceTests`（`Agw.Tools.Tests`）。测试文件内的辅助方法：`User(text)` 和 `Assistant(n)` 创建 user 消息和 n 条 assistant 文本消息；`Attachment(type)` 创建调用过 `ConversationHistoryMetadata.SetContextAttachmentType` 的 user 消息；`TodoCall()` 创建包含 `FunctionCallContent("call", "todos_add")` 的 assistant 消息；`CreatePlanModeSource(mode)` 返回 `new PlanModeAttachmentSource(new AgentModeProvider(new AgentModeProviderOptions { DefaultMode = mode }))` 和一个 `TestAgentSession`（写法与 `ModeToolBlockTests.TestAgentSession` 相同）。

```csharp
[Fact]
public async Task EvaluateAsync_PlanModeActive_ReturnsFullThenSparseReminders()
{
    var (source, session) = CreatePlanModeSource("plan");

    var first = await source.EvaluateAsync(session, [User("hi")], Ct);
    Assert.Equal(ContextAttachmentTypes.PlanMode, first!.Type);
    Assert.StartsWith("Plan mode is active.", first.Text);
    Assert.Contains("PlanModeToolNotAllowed", first.Text, StringComparison.Ordinal);

    Assert.Null(await source.EvaluateAsync(session, [User("hi"), Attachment("plan_mode"), .. Assistant(4)], Ct));
    var sparse = await source.EvaluateAsync(session, [User("hi"), Attachment("plan_mode"), .. Assistant(5)], Ct);
    Assert.Equal(
        new ContextAttachment("plan_mode", "Plan mode is still active. Follow the Plan mode instructions given earlier in this conversation."),
        sparse
    );

    // 已有 5 条 plan_mode，第 6 条为完整说明。Five plan_mode messages exist, so the sixth is the full text.
    var fiveRounds = Enumerable.Range(0, 5).SelectMany(_ => (ChatMessage[])[Attachment("plan_mode"), .. Assistant(5)]).ToList();
    Assert.StartsWith("Plan mode is active.", (await source.EvaluateAsync(session, fiveRounds, Ct))!.Text);

    // 退出之后再次进入 Plan 模式，从完整说明开始。Re-entering Plan mode after an exit starts with the full text.
    ChatMessage[] reentered = [Attachment("plan_mode"), .. Assistant(1), Attachment("plan_mode_exit"), .. Assistant(1)];
    Assert.StartsWith("Plan mode is active.", (await source.EvaluateAsync(session, reentered, Ct))!.Text);
}

[Fact]
public async Task EvaluateAsync_ModeBackToExecute_ReturnsPlanModeExitOnce()
{
    var (source, session) = CreatePlanModeSource("execute");

    Assert.Null(await source.EvaluateAsync(session, [User("hi")], Ct));
    Assert.Equal(
        new ContextAttachment("plan_mode_exit", "Plan mode has ended. The conversation is in Execute mode."),
        await source.EvaluateAsync(session, [Attachment("plan_mode"), .. Assistant(1)], Ct)
    );
    Assert.Null(await source.EvaluateAsync(session, [Attachment("plan_mode"), .. Assistant(1), Attachment("plan_mode_exit")], Ct));
}

[Theory]
[InlineData(9, null, false)]
[InlineData(10, null, true)]
[InlineData(12, 9, false)]
[InlineData(12, 10, true)]
public async Task EvaluateAsync_TodoToolsUnusedForTenAssistantMessages_ReturnsTodoReminder(int sinceCall, int? sinceReminder, bool expected)
{
    // 消息列表：一次 todos_add 调用，之后 sinceCall 条 assistant 消息；sinceReminder 不为空时，todo_reminder 位于最后 sinceReminder 条之前。
    // The list: one todos_add call followed by sinceCall assistant messages; when sinceReminder is set, a todo_reminder precedes the last sinceReminder of them.
    List<ChatMessage> messages = [TodoCall(), .. Assistant(sinceCall - (sinceReminder ?? 0))];
    if (sinceReminder is { } count)
        messages.AddRange([Attachment("todo_reminder"), .. Assistant(count)]);
    var source = new TodoReminderAttachmentSource(new AgwTodoProvider());

    var attachment = await source.EvaluateAsync(new TestAgentSession(), messages, Ct);

    Assert.Equal(expected, attachment != null);
    if (expected)
    {
        Assert.Equal(ContextAttachmentTypes.TodoReminder, attachment!.Type);
        Assert.StartsWith("The todo tools have not been used recently.", attachment.Text);
        Assert.Contains("### Current todo list", attachment.Text, StringComparison.Ordinal);
    }
}
```

`UserMemoryProviderTests` 增加 `GetContentAsync_WithAndWithoutMemories_ReturnsContentOrNull`：按 `Tools_CrudCurrentUserAndContextUsesContentNotDescription` 的方式创建 `Fixture` 和 `UserMemoryProvider`；`new UserMemoryConversationContextSource(provider)` 的 `Title` 为 `User Memory`；没有 Memory 时 `GetContentAsync` 返回 `null`；用 `user_memory_write` 写入一条之后，返回值包含这条 Memory 的正文。

`ProjectMemoryProviderTests` 增加 `GetContentAsync_MissingIndex_ReturnsNull`：按 `InvokingAsync_MissingMemory_DoesNotCreateDirectory` 的方式创建 Provider，`new ProjectMemoryConversationContextSource(provider)` 的 `Title` 为 `Project Memory Index`，`GetContentAsync` 返回 `null`。`WriteAsync_NewProviderInstanceReadsSharedMemoryAndIndex` 增加断言：用第二个 Provider 创建的来源，`GetContentAsync` 的返回值包含写入的文件名。

`ProjectMemoryToolBlockTests.MaterializeAsync_PersistedConversation_CreatesProvider` 增加断言 `Assert.Single(contribution.ConversationContextSources)`。

`AgentCapabilityComposerTests`：`TestToolBlock.MaterializeAsync` 向 `ContextAttachmentSources` 加入 `new TodoReminderAttachmentSource(new AgwTodoProvider())`，向 `ConversationContextSources` 加入测试文件内标题为 `Test` 的 `IConversationContextSource`。`ComposeAsync_ToolBlockContribution_MaterializesMemberTool` 增加断言：`composition.ConversationContextSources` 和 `composition.ContextAttachmentSources` 各有一个来源。`ComposeAsync_ExternalAgent_UserMemoryConfigured_ContributesOnlyUserMemoryContext` 增加断言：两个列表都为空。

- [ ] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Tools.Tests --filter-method "*EvaluateAsync_*"`
预期：编译失败，类型不存在。

- [ ] **步骤 3：编写“接口”中的类型，Tool Block 加入来源**

- `UserMemoryToolBlock`：`contribution.ConversationContextSources.Add(new UserMemoryConversationContextSource(provider))`，`provider` 与加入 `ContextProviders` 的是同一个对象。
- `ProjectMemoryToolBlock`、`TodoToolBlock`、`ModeToolBlock`：同样使用各自已经创建的 Provider 创建来源。`ModeToolBlock` 把 `AgentModeProvider` 保存到局部变量，再分别交给 `ContextProviders` 和 `PlanModeAttachmentSource`。
- `PlanModeAttachmentSource` 直接引用 `ModeToolBlock.PlanModeInstructions`（`internal const`）。

- [ ] **步骤 4：`AgentCapabilityComposition`、`AgentCapabilityComposer` 和 `AgwAgentTestHost` 携带来源**

按“接口”修改。

- [ ] **步骤 5：运行测试，确认通过**

运行：`dotnet test --project tests/Agw.Tools.Tests` 和 `Agw.Agents.Tests.AgentCapabilityComposerTests` 类。
预期：全部通过。本任务不改变发送给模型的请求。

- [ ] **步骤 6：格式化并提交**

```bash
git commit -m "feat(tools): add conversation context and attachment sources"
```

---

### 任务 7：会话上下文

**文件：**
- 新建：`src/server/Agw.Agents.Execution/Agents/Context/Conversation/ConversationContextChatClient.cs`
- 新建：`src/server/Agw.Agents.Execution/Agents/History/IContextAttachmentWriter.cs`（定义见任务 8 的“接口”）
- 修改：`src/server/Agw.Agents.Execution/Agents/Tools/AgwAgentExtensions.Tools.cs`（`BuildChatClient`：`ConfigureHistoryPipeline` 之后、`ModelInputFilteringChatClient` 之前）
- 修改：`src/server/Agw.Agents.Execution/Agents/Runtime/AgentRuntimeFactory.CreateDefinitionAgents.cs:181-186`
- 修改：`src/server/Agw.Tools/Impl/ToolBlocks/ProjectMemory/ProjectMemoryProvider.cs`（`ProvideAIContextAsync` 不再返回 `Messages`）
- 测试：`tests/Agw.Agents.Tests/ConversationContextChatClientTests.cs`（新建）、`tests/Agw.Tools.Tests/ProjectMemoryProviderTests.cs`

**接口：**
- 使用：`AgentCapabilityComposition.ConversationContextSources`（任务 6）、`RecordingModelClient` 与 `AgwAgentTestHost`（任务 2、6）。
- 产出：

```csharp
internal sealed class ConversationContextChatClient : DelegatingChatClient
{
    internal const string StateKey = "agw.conversationContext";

    public ConversationContextChatClient(
        IChatClient innerClient,
        IReadOnlyList<IConversationContextSource> contextSources,
        IReadOnlyList<IContextAttachmentSource> attachmentSources,
        IContextAttachmentWriter? attachmentWriter);

    // 没有任何正文时返回 null。Returns null when no section has content.
    internal static string? Format(IReadOnlyList<(string Title, string Content)> sections);
}

internal sealed record ConversationContextState(bool Computed, string? Text, string? AnchorMessageId);
```

本任务只处理 `contextSources`，`attachmentSources` 和 `attachmentWriter` 在任务 8 使用。状态用 `ProviderSessionState<ConversationContextState>` 保存，键为 `StateKey`，序列化选项的写法与 `HistorySessionState` 相同；session 取 `AIAgent.CurrentRunContext?.Session`。

`Format` 的输出（各部分之间空一行，换行为 `\n`）：

```text
<system-reminder>
Context for this conversation. Use it when it is relevant to the request.

# User Memory
{content}

# Project Memory Index
{content}
</system-reminder>
```

- [ ] **步骤 1：编写失败的测试**

`ConversationContextChatClientTests`。`CountingSource(title, content)` 是测试文件内的 `IConversationContextSource`，记录调用次数 `Calls`，`Content` 可以修改。`CreateAgent(RecordingModelClient model, IConversationContextSource source, CompactionStrategy? compactionStrategy = null, IReadOnlyList<AITool>? tools = null)` 返回 `model.AsAgwAgent(AgwAgentTestHost.Definition(new InMemoryChatHistoryProvider(), compactionStrategy == null ? null : new CompactionProvider(compactionStrategy, stateKey: "test-compaction")), AgwAgentTestHost.Capabilities(tools: tools, conversationContextSources: [source]), NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider())`。

```csharp
private const string Header = "<system-reminder>\nContext for this conversation. Use it when it is relevant to the request.\n\n";

[Fact]
public async Task GetResponseAsync_FirstRequestOfConversation_ComputesAndPrependsConversationContext()
{
    var model = new RecordingModelClient();
    var agent = CreateAgent(model, new CountingSource("User Memory", "Reply in Chinese."));
    var session = await agent.CreateSessionAsync(Ct);

    await agent.RunAsync([new ChatMessage(ChatRole.User, "hello") { MessageId = "u1" }], session, cancellationToken: Ct);

    var first = model.Requests[0].Messages[0];
    Assert.Equal(ChatRole.User, first.Role);
    Assert.Equal(Header + "# User Memory\nReply in Chinese.\n</system-reminder>", first.Text);
    Assert.Equal("hello", model.Requests[0].Messages[1].Text);
}

[Fact]
public async Task GetResponseAsync_LaterRequest_ReusesStoredConversationContext()
{
    var model = new RecordingModelClient();
    var source = new CountingSource("User Memory", "v1");
    var agent = CreateAgent(model, source);
    var session = await agent.CreateSessionAsync(Ct);
    await agent.RunAsync([new ChatMessage(ChatRole.User, "one") { MessageId = "u1" }], session, cancellationToken: Ct);
    source.Content = "v2";
    session = await agent.DeserializeSessionAsync(await agent.SerializeSessionAsync(session, cancellationToken: Ct), cancellationToken: Ct);

    await agent.RunAsync([new ChatMessage(ChatRole.User, "two") { MessageId = "u2" }], session, cancellationToken: Ct);

    Assert.Equal(model.Requests[0].Messages[0].Text, model.Requests[1].Messages[0].Text);
    Assert.Equal(1, source.Calls);
    // 会话上下文没有写入历史，请求中只出现一次。The conversation context is not in history and appears once.
    Assert.Single(model.Requests[1].Messages, message => message.Text.StartsWith("<system-reminder>", StringComparison.Ordinal));
}

[Fact]
public async Task GetResponseAsync_AllSourcesEmpty_SendsNoContextAndDoesNotRecompute()
{
    var model = new RecordingModelClient();
    var source = new CountingSource("User Memory", null);
    var agent = CreateAgent(model, source);
    var session = await agent.CreateSessionAsync(Ct);

    await agent.RunAsync([new ChatMessage(ChatRole.User, "one") { MessageId = "u1" }], session, cancellationToken: Ct);
    await agent.RunAsync([new ChatMessage(ChatRole.User, "two") { MessageId = "u2" }], session, cancellationToken: Ct);

    Assert.Equal("one", model.Requests[0].Messages[0].Text);
    Assert.Equal("one", model.Requests[1].Messages[0].Text);
    Assert.Equal(1, source.Calls);
}

[Fact]
public async Task GetResponseAsync_FirstMessageChangedByTruncation_RecomputesConversationContext()
{
    // 窗口 100,000 / 20,000：超过 64,000 token 时截断到 32,000 以内。
    // Window 100,000 / 20,000: truncation starts above 64,000 tokens and stops within 32,000.
    var model = new RecordingModelClient();
    var source = new CountingSource("User Memory", "v1");
    var agent = CreateAgent(model, source, ContextWindowCompactionPipeline.Create(100_000, 20_000));
    var session = await agent.CreateSessionAsync(Ct);
    await agent.RunAsync([new ChatMessage(ChatRole.User, "short") { MessageId = "u0" }], session, cancellationToken: Ct);
    source.Content = "v2";
    var longMessages = Enumerable
        .Range(1, 14)
        .Select(index => new ChatMessage(ChatRole.User, $"u{index}:" + new string('x', 20_000)) { MessageId = $"long-{index}" })
        .ToList();

    await agent.RunAsync(longMessages, session, cancellationToken: Ct);

    Assert.Equal(2, source.Calls);
    Assert.Contains("v2", model.Requests[^1].Messages[0].Text, StringComparison.Ordinal);
    Assert.DoesNotContain(model.Requests[^1].Messages, message => message.Text == "short");
}

[Fact]
public async Task GetResponseAsync_ToolResultsEvicted_KeepsConversationContext()
{
    // 14 次 Tool call，每个结果 20,000 个字符；窗口 1,000,000 / 128,000 的 T 为 64,000，第 13 个结果之后清空。
    // Fourteen tool calls of 20,000 characters each; with T = 64,000 eviction happens after the thirteenth result.
    var model = new RecordingModelClient((index, _) =>
        index < 14 ? [new FunctionCallContent($"call-{index}", "lookup")] : [new TextContent("done")]
    );
    var source = new CountingSource("User Memory", "v1");
    var lookup = AIFunctionFactory.Create((Func<string>)(() => new string('x', 20_000)), new AIFunctionFactoryOptions { Name = "lookup" });
    var agent = CreateAgent(model, source, ContextWindowCompactionPipeline.Create(1_000_000, 128_000), [lookup]);
    var session = await agent.CreateSessionAsync(Ct);

    await agent.RunAsync([new ChatMessage(ChatRole.User, "start") { MessageId = "u1" }], session, cancellationToken: Ct);

    Assert.Equal(1, source.Calls);
    Assert.Contains(
        model.Requests[^1].Messages.SelectMany(message => message.Contents.OfType<FunctionResultContent>()),
        result => Equals(result.Result, ToolResultEvictionCompactionStrategy.EvictedResultText)
    );
    Assert.All(model.Requests, request => Assert.Equal(model.Requests[0].Messages[0].Text, request.Messages[0].Text));
}
```

`ProjectMemoryProviderTests.WriteAsync_NewProviderInstanceReadsSharedMemoryAndIndex` 中 `Assert.Single(secondContext.Messages!)` 以及使用 `indexMessage` 的断言改为 `Assert.Null(secondContext.Messages)`；索引的内容由任务 6 增加的来源断言核对。

- [ ] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-method "*ConversationContext*"`
预期：编译失败，`ConversationContextChatClient` 不存在。

- [ ] **步骤 3：编写 `ConversationContextChatClient` 的会话上下文部分**

每次请求的处理：
1. 把消息读入新的列表，`anchor` 为第一条消息的 `MessageId`。
2. 读取状态。`Computed` 为 `false`，或者 `anchor` 与 `AnchorMessageId` 不同时，按顺序调用全部来源，用 `Format` 生成文本，保存 `new ConversationContextState(true, text, anchor)`。
3. `Text` 不为 `null` 时，在列表最前面插入 `new ChatMessage(ChatRole.User, text)`。
4. 没有 session 时不保存状态，每次请求都调用来源。

- [ ] **步骤 4：加入管道，移除两处旧的注入**

- `BuildChatClient` 增加 `AgentCapabilityComposition capabilities` 参数，在 `ConfigureHistoryPipeline(...)` 之后加入 `ConversationContextChatClient`，参数取 `capabilities.ConversationContextSources`、`capabilities.ContextAttachmentSources` 和 `definition.ChatHistoryProvider as IContextAttachmentWriter`。
- `CreateDefinitionAgentAsync`：创建 `HistoryRecordingAgent` 时第三个参数传入 `null`，删除 `userMemoryProvider` 和 `createMemoryContextAsync` 两个局部变量。External Agent 的创建代码不变。
- `ProjectMemoryProvider.ProvideAIContextAsync`：只返回 `Instructions` 和 `Tools`。

- [ ] **步骤 5：运行测试，确认通过**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-method "*ConversationContext*"`、`Agw.Tools.Tests.ProjectMemoryProviderTests` 类、`Agw.Agents.Tests.HistoryRecordingAgentTests` 类。
预期：全部通过。`HistoryRecordingAgentTests` 中关于 memory 注入的测试针对的是传入了 `createMemoryContextAsync` 的情况，保持不变。

- [ ] **步骤 6：格式化并提交**

```bash
git commit -m "feat(agents): send memory as a conversation context message"
```

---

### 任务 8：尾部上下文消息

**文件：**
- 修改：`src/server/Agw.Agents.Execution/Agents/History/IContextAttachmentWriter.cs`、`AgwChatHistoryProvider.cs`、`HistoryRecording.cs`
- 修改：`src/server/Agw.Agents.Execution/Agents/Context/Conversation/ConversationContextChatClient.cs`
- 修改：`src/server/Agw.Agents.Execution/Agents/Tools/AgwAgentExtensions.Tools.cs`
- 修改：`src/server/Agw.Agents.Execution/Agents/Context/PlanMode/PlanModeToolGuardProvider.cs`
- 修改：`src/server/Agw.Tools/Impl/ToolBlocks/Todo/TodoToolBlock.cs`、`src/server/Agw.Tools/Impl/ToolBlocks/Mode/ModeToolBlock.cs`
- 修改：`src/server/Agw.Projects/Infrastructure/ConversationHandoffProvider.cs`（`CreateCandidate`，约 196 至 206 行）
- 测试：`tests/Agw.Agents.Tests/ConversationContextChatClientTests.cs`、`AgwChatHistoryProviderTests.cs`、`AgwAgentExtensionsTests.cs`、`PlanModeToolGuardProviderTests.cs`，`tests/Agw.Tools.Tests/TodoToolBlockTests.cs`、`ModeToolBlockTests.cs`，`tests/Agw.Projects.Tests/ConversationHandoffProviderTests.cs`

**接口：**
- 使用：`IContextAttachmentSource`、`ContextAttachment`、`ConversationHistoryMetadata.SetContextAttachmentType`、`PlanModeAttachmentSource`（任务 6）。
- 产出：

```csharp
internal interface IContextAttachmentWriter
{
    // 把尾部上下文消息写入当前运行的历史，Step 序号与本次输入相同。
    // Writes the attachments into the current run's history with the step index of this request's input.
    ValueTask WriteContextAttachmentsAsync(
        AgentSession session,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken);
}
```

- `AgwChatHistoryProvider : ChatHistoryProvider, IContextAttachmentWriter`。
- `HistoryRecording.InputStepIndex`：`internal int? InputStepIndex { get; set; }`，`AgwChatHistoryProvider.InvokingCoreAsync` 在 `scope?.AdvanceStep()` 之前写入 `scope?.StepIndex`。
- 尾部上下文消息的形式：`new ChatMessage(ChatRole.User, "<system-reminder>\n" + attachment.Text + "\n</system-reminder>")`，`MessageId` 为 `Guid.CreateVersion7().ToString("D")`，调用 `WithAgentRequestMessageSource(AgentRequestMessageSourceType.AIContextProvider, attachment.Type)` 和 `ConversationHistoryMetadata.SetContextAttachmentType(message, attachment.Type)`。
- `AsAgwAgent`：`capabilities.ContextAttachmentSources` 不为空并且 `definition.ChatHistoryProvider` 没有实现 `IContextAttachmentWriter` 时，抛出 `InvalidOperationException`。

- [ ] **步骤 1：编写失败的测试**

`ConversationContextChatClientTests`。`CreatePlanSource()` 返回 `new PlanModeAttachmentSource(new AgentModeProvider(new AgentModeProviderOptions { DefaultMode = "plan" }))`。`CreateRecordedAgent(fixture, model, history, capabilities)` 返回 `HistoryTestFixture.Record(model.AsAgwAgent(AgwAgentTestHost.Definition(history), capabilities, NullLoggerFactory.Instance, fixture.Services), history)`。

```csharp
[Fact]
public async Task GetResponseAsync_ContextAttachment_PersistsRowWithTypeAndSameMessageId()
{
    using var owner = HistoryTestFixture.EnterUser();
    await using var fixture = await HistoryTestFixture.CreateAsync();
    var model = new RecordingModelClient();
    var history = fixture.CreateProvider();
    await using var capabilities = AgwAgentTestHost.Capabilities(contextAttachmentSources: [CreatePlanSource()]);
    var agent = CreateRecordedAgent(fixture, model, history, capabilities);
    var session = await fixture.CreateSessionAsync(agent);

    await AgwAgentTestHost.RunTurnAsync(fixture, agent, session, new ChatMessage(ChatRole.User, "one"));
    await AgwAgentTestHost.RunTurnAsync(fixture, agent, session, new ChatMessage(ChatRole.User, "two"));

    var sent = model.Requests[0].Messages[^1];
    Assert.StartsWith("<system-reminder>\nPlan mode is active.", sent.Text);
    Assert.EndsWith("\n</system-reminder>", sent.Text);
    var row = Assert.Single(
        await fixture.ReadMessagesAsync(),
        message => ConversationHistoryMetadata.GetContextAttachmentType(message) == ContextAttachmentTypes.PlanMode
    );
    Assert.Equal(Guid.Parse(sent.MessageId!), Guid.Parse(row.MessageId!));
    // 第二次请求以第一次请求的全部消息作为开头。The second request starts with every message of the first.
    var first = model.Requests[0].Messages.Select(message => message.Text).ToList();
    Assert.Equal(first, model.Requests[1].Messages.Take(first.Count).Select(message => message.Text));
    Assert.Single(model.Requests[1].Messages, message => message.Text == sent.Text);
}

[Fact]
public async Task GetResponseAsync_RerunAfterFailedModelCall_DoesNotWriteAttachmentAgain()
{
    using var owner = HistoryTestFixture.EnterUser();
    await using var fixture = await HistoryTestFixture.CreateAsync();
    var model = new RecordingModelClient((index, _) =>
        index == 0 ? throw new InvalidOperationException("model failed") : [new TextContent("done")]
    );
    var history = fixture.CreateProvider();
    await using var capabilities = AgwAgentTestHost.Capabilities(contextAttachmentSources: [CreatePlanSource()]);
    var agent = CreateRecordedAgent(fixture, model, history, capabilities);
    var session = await fixture.CreateSessionAsync(agent);
    var input = new ChatMessage(ChatRole.User, "one") { MessageId = Guid.CreateVersion7().ToString("D") };

    await Assert.ThrowsAnyAsync<Exception>(() => AgwAgentTestHost.RunTurnAsync(fixture, agent, session, input));
    await AgwAgentTestHost.RunTurnAsync(fixture, agent, session, input);

    Assert.Single(
        await fixture.ReadMessagesAsync(),
        message => ConversationHistoryMetadata.GetContextAttachmentType(message) == ContextAttachmentTypes.PlanMode
    );
}

[Fact]
public void AsAgwAgent_AttachmentSourcesWithoutWriter_Throws()
{
    var capabilities = AgwAgentTestHost.Capabilities(contextAttachmentSources: [CreatePlanSource()]);

    Assert.Throws<InvalidOperationException>(() =>
        new RecordingModelClient().AsAgwAgent(
            AgwAgentTestHost.Definition(new InMemoryChatHistoryProvider()),
            capabilities,
            NullLoggerFactory.Instance,
            new ServiceCollection().BuildServiceProvider()
        )
    );
}
```

`AgwChatHistoryProviderTests`（使用 `HistoryTestFixture`）：

```csharp
[Fact]
public async Task InvokingCoreAsync_ModeChangeNotice_IsRemoved()
{
    using var owner = HistoryTestFixture.EnterUser();
    await using var fixture = await HistoryTestFixture.CreateAsync();
    var model = new RecordingModelClient();
    var history = fixture.CreateProvider();
    var modeProvider = new AgentModeProvider(new AgentModeProviderOptions { DefaultMode = "execute" });
    await using var capabilities = AgwAgentTestHost.Capabilities(contextProviders: [modeProvider]);
    var agent = HistoryTestFixture.Record(
        model.AsAgwAgent(AgwAgentTestHost.Definition(history), capabilities, NullLoggerFactory.Instance, fixture.Services),
        history
    );
    var session = await fixture.CreateSessionAsync(agent);
    await AgwAgentTestHost.RunTurnAsync(fixture, agent, session, new ChatMessage(ChatRole.User, "one"));
    await modeProvider.SetModeAsync(session, "plan", Ct);
    // Agentflow 的节点指令使用相同的来源类型，来源 ID 为 AgentflowMessageTransforms。
    // Agentflow node instructions use the same source type with the source ID AgentflowMessageTransforms.
    var nodeInstruction = new ChatMessage(ChatRole.System, "node rules").WithAgentRequestMessageSource(
        AgentRequestMessageSourceType.AIContextProvider,
        "AgentflowMessageTransforms"
    );

    await AgwAgentTestHost.RunTurnAsync(fixture, agent, session, nodeInstruction, new ChatMessage(ChatRole.User, "two"));

    Assert.DoesNotContain(model.Requests[1].Messages, message => message.Text.StartsWith("[Mode changed", StringComparison.Ordinal));
    Assert.DoesNotContain(await fixture.ReadMessagesAsync(), message => message.Text.StartsWith("[Mode changed", StringComparison.Ordinal));
    Assert.Single(model.Requests[1].Messages, message => message.Text == "node rules");
    Assert.Single(model.Requests[1].Messages, message => message.Text == "two");
}
```

`AgwAgentExtensionsTests`。`ContinueOnceEvaluator` 的写法与任务 2 相同。`Signature(message)` 返回角色、`Text` 以及全部 `FunctionCallContent`、`FunctionResultContent` 的 `CallId` 组成的字符串。

```csharp
[Fact]
public async Task AsAgwAgent_ConsecutiveModelRequests_EachStartsWithPreviousRequest()
{
    using var owner = HistoryTestFixture.EnterUser();
    await using var fixture = await HistoryTestFixture.CreateAsync();
    await using var mode = await MaterializeBlockAsync(new ModeToolBlock(), new ModeToolBlockDefinition());
    var modeProvider = mode.ContextProviders.Select(provider => provider.GetService<AgentModeProvider>()).First(provider => provider != null)!;
    var search = AIFunctionFactory.Create((Func<string>)(() => "search result"), new AIFunctionFactoryOptions { Name = "web_search" });
    // 第一个 Turn：两次 Tool call、一次回答、LoopAgent 继续一次；第二个 Turn：一次 Tool call、一次回答。
    // Turn one: two tool calls, an answer, one LoopAgent continuation; turn two: one tool call and an answer.
    var model = new RecordingModelClient((index, _) =>
        index is 0 or 1 or 4 ? [new FunctionCallContent($"call-{index}", "web_search")] : [new TextContent("done")]
    );
    var history = fixture.CreateProvider();
    await using var capabilities = AgwAgentTestHost.Capabilities(
        tools: [new ApprovalRequiredAIFunction(search)],
        contextProviders: mode.ContextProviders,
        loopEvaluators: [new ContinueOnceEvaluator()],
        autoApprovalRules: [ToolApprovalAgent.AllToolsAutoApprovalRule],
        planModeAllowedToolNames: new HashSet<string>([.. mode.PlanModeAllowedToolNames, "web_search"], StringComparer.OrdinalIgnoreCase),
        contextAttachmentSources: mode.ContextAttachmentSources
    );
    var agent = HistoryTestFixture.Record(
        model.AsAgwAgent(AgwAgentTestHost.Definition(history), capabilities, NullLoggerFactory.Instance, fixture.Services),
        history
    );
    var session = await fixture.CreateSessionAsync(agent);
    await modeProvider.SetModeAsync(session, "execute", Ct);

    await AgwAgentTestHost.RunTurnAsync(fixture, agent, session, new ChatMessage(ChatRole.User, "one"));
    await modeProvider.SetModeAsync(session, "plan", Ct);
    await AgwAgentTestHost.RunTurnAsync(fixture, agent, session, new ChatMessage(ChatRole.User, "two"));

    Assert.Equal(6, model.Requests.Count);
    for (var index = 1; index < model.Requests.Count; index++)
    {
        var previous = model.Requests[index - 1];
        var current = model.Requests[index];
        Assert.Equal(previous.Messages.Select(Signature), current.Messages.Take(previous.Messages.Count).Select(Signature));
        Assert.Equal(previous.Instructions, current.Instructions);
        Assert.Equal(previous.ToolNames, current.ToolNames);
    }
    Assert.Equal(ContextAttachmentTypes.PlanMode, ConversationHistoryMetadata.GetContextAttachmentType(model.Requests[4].Messages[^1]));
    Assert.DoesNotContain(model.Requests[4].Messages, message => message.Text.StartsWith("[Mode changed", StringComparison.Ordinal));
}

[Fact]
public async Task AsAgwAgent_ModeSwitched_KeepsToolsAndInstructionsIdentical()
{
    await using var mode = await MaterializeBlockAsync(new ModeToolBlock(), new ModeToolBlockDefinition());
    var modeProvider = mode.ContextProviders.Select(provider => provider.GetService<AgentModeProvider>()).First(provider => provider != null)!;
    var shell = AIFunctionFactory.Create((Func<string>)(() => "ran"), new AIFunctionFactoryOptions { Name = "run_shell" });
    var model = new RecordingModelClient();
    var agent = model.AsAgwAgent(
        AgwAgentTestHost.Definition(new InMemoryChatHistoryProvider()),
        AgwAgentTestHost.Capabilities(
            tools: [new ApprovalRequiredAIFunction(shell)],
            contextProviders: mode.ContextProviders,
            planModeAllowedToolNames: mode.PlanModeAllowedToolNames
        ),
        NullLoggerFactory.Instance,
        new ServiceCollection().BuildServiceProvider()
    );
    var session = await agent.CreateSessionAsync(Ct);
    await modeProvider.SetModeAsync(session, "execute", Ct);

    await agent.RunAsync([new ChatMessage(ChatRole.User, "one")], session, cancellationToken: Ct);
    await modeProvider.SetModeAsync(session, "plan", Ct);
    await agent.RunAsync([new ChatMessage(ChatRole.User, "two")], session, cancellationToken: Ct);

    Assert.Equal(model.Requests[0].Instructions, model.Requests[1].Instructions);
    Assert.Equal(model.Requests[0].ToolNames, model.Requests[1].ToolNames);
    Assert.DoesNotContain("Current mode:", model.Requests[0].Instructions, StringComparison.Ordinal);
}
```

`ConversationHandoffProviderTests` 增加 `CreateAsync_ContextAttachmentRows_AreSkipped`：按 `CreateAsync_PrivateControlAndToolMessages_ReturnsOnlyPublicTextAndResult` 的方式用 `SeedAsync` 和 `CreateRecord` 写入三行：user 消息 `request`、调用过 `SetContextAttachmentType(message, "plan_mode")` 的 user 消息 `<system-reminder>\nPlan mode is active.\n</system-reminder>`、assistant 消息 `answer`。断言交接内容包含 `request` 和 `answer`，不包含 `Plan mode is active.`。

`ModeToolBlockTests` 增加 `MaterializeAsync_Instructions_DoNotContainCurrentMode`：`(await MaterializeAsync()).Context.Instructions` 不包含 `Current mode:`，包含 `The conversation is in Execute mode unless a system reminder says Plan mode is active.`。

`TodoToolBlockTests` 增加 `MaterializeAsync_Provider_ReturnsNoTodoListMessage`：按 `MaterializeAsync_LocalProvider_PreservesReferenceTodoBehavior` 的方式调用 Provider，`AIContext.Messages` 为 `null`，`contribution.ContextAttachmentSources` 中有一个 `TodoReminderAttachmentSource`。

- [ ] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-method "*ContextAttachment*"`
预期：失败，请求的末尾没有尾部上下文消息。

- [ ] **步骤 3：`AgwChatHistoryProvider` 写入尾部上下文消息，移除 Mode 切换提示**

- `InvokingCoreAsync`：从返回的 `messages` 和用于写入的 `requestMessages` 中移除 `AgentModeProvider` 产生的 Mode 切换提示，条件为 `GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.AIContextProvider` 并且 `GetAgentRequestMessageSourceId() == typeof(AgentModeProvider).FullName`（MAF 用 Provider 的完整类型名作为来源 ID）。其他来源 ID 的消息保持不变，包括 Agentflow 的节点指令（来源 ID 为 `AgentflowMessageTransforms`）和跨 Agent 交接内容。在 `scope?.AdvanceStep()` 之前设置 `recording.InputStepIndex = scope?.StepIndex`。
- `WriteContextAttachmentsAsync`：取 `_recordings` 中这个 session 的记录，调用 `recording.WriteMessagesAsync(messages, recording.InputStepIndex, cancellationToken)`。没有记录时抛出 `InvalidOperationException`。

- [ ] **步骤 4：`ConversationContextChatClient` 判断并追加尾部上下文消息**

在插入会话上下文之前执行：对 Compaction 之后的消息列表依次调用 `attachmentSources`；把返回的提示按“接口”中的形式创建为消息，追加在列表末尾；全部创建完成后调用一次 `attachmentWriter.WriteContextAttachmentsAsync`。没有 session 或者没有来源时跳过。

- [ ] **步骤 5：`AsAgwAgent` 的检查与相关修改**

- `AsAgwAgent` 开头按“接口”抛出 `InvalidOperationException`。
- `FunctionLoopContextTracker`、`FunctionLoopContextScopeAgent` 和 `FunctionLoopMessageIsolationChatClient` 保持不变：Agentflow 的节点指令以 `AIContextProvider` 来源进入请求，在 Function Loop 中依靠它们只发送一次。
- `PlanModeToolGuardProvider`：删除 `EnforcementInstructions` 和追加它的代码。
- `TodoToolBlock`：`new AgwTodoProvider(new AgwTodoProviderOptions { SuppressTodoListMessage = true })`。
- `ModeToolBlock.ModeInstructions` 中 `{available_modes}` 之后的内容改为设计文档“精简固定部分”中的两行（第一行为 `The conversation is in Execute mode unless a system reminder says Plan mode is active.`）。
- `ConversationHandoffProvider.CreateCandidate`：`ConversationHistoryMetadata.GetContextAttachmentType(message) != null` 时返回 `null`。

- [ ] **步骤 6：更新受影响的现有测试**

- `PlanModeToolGuardProviderTests` 中断言 `EnforcementInstructions`、`Plan mode is enforced by the server`、`Todo tools may be used`、`external Connection/MCP tools` 的地方，改为断言 `context.Instructions` 为 `null`（输入的 `AIContext` 没有 Instructions）。
- `AsAgwAgent_RepeatedContextAcrossSerializedTurns_PreservesLatestMessagesAndAssignsIdentity`、`AsAgwAgent_StreamingCompaction_PreservesFunctionLoopContextWithoutDuplication` 和 `AsAgwAgent_AutoApprovedFunctionLoop_PreservesHistoryAndDeduplicatesContext` 不修改，并且保持通过。

- [ ] **步骤 7：运行测试，确认通过**

运行：`dotnet test --project tests/Agw.Agents.Tests`、`dotnet test --project tests/Agw.Tools.Tests`、`dotnet test --project tests/Agw.Projects.Tests`、`dotnet test --project tests/Agw.Architecture.Tests`
预期：全部通过。

- [ ] **步骤 8：格式化并提交**

```bash
git commit -m "feat(agents): persist plan mode and todo reminders as context attachments"
```

---

### 任务 9：精简 Instructions 与 `ask_user_question` 的参数定义

**文件：**
- 修改：`src/server/Agw.Tools/Impl/ToolBlocks/Todo/AgwTodoProvider.cs`（`DefaultInstructions`）
- 修改：`src/server/Agw.Tools/Impl/ToolBlocks/FileAccess/AgwFileAccessProvider.cs`（`Instructions`）
- 修改：`src/server/Agw.Tools/Impl/ToolBlocks/ProjectMemory/ProjectMemoryProvider.cs`、`src/server/Agw.Tools/Impl/ToolBlocks/UserMemory/UserMemoryProvider.cs`（`Instructions`）
- 新建：`src/server/Agw.Tools/Infrastructure/HostManagedPropertiesAIFunction.cs`
- 修改：`src/server/Agw.Tools/Impl/Tools/Basic/AskUserQuestionTool.cs`（`ToAITool`）
- 测试：`tests/Agw.Tools.Tests/AskUserQuestionToolTests.cs`、`TodoToolBlockTests.cs`、`AgwFileAccessProviderTests.cs`、`ProjectMemoryProviderTests.cs`、`UserMemoryProviderTests.cs`

**接口：**
- 产出：

```csharp
internal sealed class HostManagedPropertiesAIFunction : DelegatingAIFunction
{
    public HostManagedPropertiesAIFunction(AIFunction innerFunction, IReadOnlySet<string> hostManagedProperties);

    // 内层 schema 去掉 hostManagedProperties 中的属性，同时从 required 中去掉它们。
    // The inner schema without the host-managed properties, also removed from "required".
    public override JsonElement JsonSchema { get; }
}
```

- 四段 Instructions 的文本逐字使用设计文档“精简固定部分”中的代码块。

- [ ] **步骤 1：编写失败的测试**

`AskUserQuestionToolTests`（`tool` 的创建方式与类中已有的测试相同）：

```csharp
[Fact]
public void AskUserQuestion_ModelSchema_ContainsOnlyQuestions()
{
    var function = Assert.IsAssignableFrom<AIFunction>(tool.ToAITool());

    var properties = function.JsonSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name);

    Assert.Equal(["questions"], properties);
}
```

类中已有的 `InvokeAsync_BeforeHumanResponse_RemainsPendingAndIgnoresModelAnswer` 等测试保持通过，用来确认 Host 写入的 `answers` 仍然能够绑定。四个 Provider 的测试各增加一组断言：`AIContext.Instructions` 以对应的标题开头，并且包含设计文档文本中的下面这一句：

| Provider | 标题 | 句子 |
| --- | --- | --- |
| `AgwTodoProvider` | `## Todo Items` | ``Track multi-step work with the `todos_*` tools. Answer single-step requests directly without a todo list.`` |
| `AgwFileAccessProvider` | `## File Access` | ``For a small edit, use `file_access_replace` or `file_access_replace_lines`.`` |
| `ProjectMemoryProvider` | `## Project Memory` | `Private user information belongs in user memory.` |
| `UserMemoryProvider` | `## User Memory` | `Knowledge that belongs to one project goes to project memory.` |

- [ ] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Tools.Tests --filter-method "*AskUserQuestion_ModelSchema*"`
预期：失败，`properties` 包含 `answers`、`annotations`、`metadata`。

- [ ] **步骤 3：编写 `HostManagedPropertiesAIFunction` 并在 `ToAITool` 中使用**

用 `System.Text.Json.Nodes.JsonNode` 解析内层的 `JsonSchema`，删除属性后转换为 `JsonElement`，在构造函数中计算一次。`ToAITool` 中把 `innerFunction` 包装为 `new HostManagedPropertiesAIFunction(innerFunction, new HashSet<string>(StringComparer.Ordinal) { "answers", "annotations", "metadata" })`，再交给 `HumanInteractionRequiredAIFunction`。

- [ ] **步骤 4：替换四段 Instructions**

逐字使用设计文档中的文本。`AgwFileReadonlyAccessProvider` 的 Instructions 不修改。

- [ ] **步骤 5：运行测试，确认通过**

运行：`dotnet test --project tests/Agw.Tools.Tests`
预期：全部通过。

- [ ] **步骤 6：格式化并提交**

```bash
git commit -m "perf(tools): trim tool block instructions and the ask_user_question schema"
```

---

### 任务 10：文档、采集与回归

**文件：**
- 修改：`src/server/Agw.Agents.Execution/README.md`（Compaction 的目标值、Plan 模式、User Memory 注入三处说明）
- 修改：`src/server/Agw.Tools/README.md`、`src/server/Agw.Tools/README.zh-CN.md`（Compaction 阈值的说明）
- 修改：`.scratch/prompt-token-opt/run.sh`、`src/clients/packages/chat-runtime/.tmp/prompt-capture.ts`（增加 `mode` 场景，两个文件不进入 git）

- [ ] **步骤 1：更新 README**

- Compaction：清空到 T 的 60%，截断到 T 的 80%，两次 Compaction 之间的请求只追加。
- Plan 模式：Tool 列表和 system 文本不随 Mode 变化；受限 Tool 被调用时返回 `PlanModeToolNotAllowed`；Plan 模式的要求通过 `plan_mode` 消息发送。
- User Memory：System Agent 的 User Memory 正文和 Project Memory 索引作为会话上下文，是每次请求的第一条 user 消息，不写入历史；External Agent 的注入方式不变。

- [ ] **步骤 2：全量测试**

运行：`env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS dotnet test Agw.slnx`
预期：全部通过。

- [ ] **步骤 3：采集 `baseline` 与 `compaction`**

```bash
dotnet build src/server/Agw.Standalone.Host/Agw.Standalone.Host.csproj -o .scratch/prompt-token-opt/host --nologo -v q
.scratch/prompt-token-opt/run.sh baseline
cp -R .scratch/prompt-token-opt/run .scratch/prompt-token-opt/run-baseline-final
node .scratch/prompt-token-opt/analyze.mjs .scratch/prompt-token-opt/run-baseline-final/requests.jsonl diff
node .scratch/prompt-token-opt/measure-system.mjs .scratch/prompt-token-opt/run-baseline-final/requests.jsonl .scratch/prompt-token-opt/texts
.scratch/prompt-token-opt/run.sh compaction
node .scratch/prompt-token-opt/analyze.mjs .scratch/prompt-token-opt/run/requests.jsonl diff
```

预期：
- `baseline` 的 `diff` 输出中每次请求只有 `+` 开头的行，没有 `-` 开头的行。
- `baseline` 中写入 User Memory 之后的新对话，第一条消息以 `<system-reminder>` 开头。
- `measure-system.mjs` 输出 `system 4830`、`tools 19172`、合计 24,002；`# Project Workspace` 与 Shell environment 排在 system 文本的末尾。
- `compaction` 中只有超过 5,000 token 的那一次请求出现 `-` 开头的行，清空后的消息不超过 3,000 token。

- [ ] **步骤 4：采集 Mode 切换**

增加 `mode` 场景：Agent 配置同 `baseline`，在同一个对话中执行三个 Turn，第一个 Turn 之后发送 `buildSetModeCommand(agent.id, "plan")`，第二个 Turn 之后发送 `buildSetModeCommand(agent.id, "execute")`，每个 Turn 的 PLAN 为空。运行 `.scratch/prompt-token-opt/run.sh mode`。

预期：三次请求的 `tools` 和 system 文本逐字节相同；第二次请求的最后一条消息以 `<system-reminder>\nPlan mode is active.` 开头；第三次请求的最后一条消息为 `<system-reminder>\nPlan mode has ended. The conversation is in Execute mode.\n</system-reminder>`。

- [ ] **步骤 5：模型服务的核对**

- 使用 Anthropic 类型的 Provider 执行包含两个以上 Step 的 Turn，`agent_usage.cached_input_token_count` 大于 0。
- 使用 OllamaNative 执行至少两个 Step 的 Turn，第二次请求的 `agent_usage.cached_input_token_count` 大于 0。
- 使用 `general-agent` 执行输入超过 58,000 token 的 Turn，查询该 Turn 的 `cached_input_token_count / input_token_count`，与设计文档背景中的 76.9% 对照。

这三项需要可用的模型服务，执行前向用户确认。

- [ ] **步骤 6：提交**

```bash
git commit -m "docs(agents): describe batched compaction and context messages"
```
