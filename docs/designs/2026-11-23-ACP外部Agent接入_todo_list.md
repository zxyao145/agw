# ACP 外部 Agent 接入：实施计划

> **给执行计划的 agent：** 必须使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 按任务逐个实施。步骤使用 checkbox（`- [ ]`）记录进度。

**目标：** 新增 External Agent 的第五种 Engine `EngineKind.ACP`，由 agw 自己实现 Agent Client Protocol v1 的 client，在 InProcess 与 Distributed 下调用 ACP agent 进程；启动配置写在 Agent 的 Extra JSON 中，registry agent 由 `acp install` 预先安装。

**架构：** `AcpAgent : AIAgent` 走现有的 `AgentRuntimeFactory.CreateExternalAgentAsync` 入口。它用 StreamJsonRpc 通过 stdio 与 agent 进程通信，把 `session/update` 转换为 `AgentResponseUpdate`，权限请求交给 `AcpPermissionBridge`。ACP `sessionId` 保存在现有的 provider session 绑定中。registry 只在查询接口与 `acp install` 命令中读取；执行时 `IAcpInstalledAgentCatalog` 只读取安装目录。

**技术栈：** .NET 10、ASP.NET Core、EF Core（SQLite、PostgreSQL）、Microsoft.Agents.AI 1.21.0、StreamJsonRpc 2.25.29、SharpCompress、DiffPlex 1.9.0、Semver 3.0.0、xUnit v3、Node.js（测试 agent 使用 `@agentclientprotocol/sdk`）、Next.js、TypeScript、Node test runner（`tsx --test`）。

**设计文档：** `docs/designs/2026-11-23-ACP外部Agent接入.md`。执行每个任务之前阅读设计文档的对应章节，下文用"设计 x.y"指代。

## 全局约束

- 没有兼容性要求，代码中不保留过渡字段与过渡行为。
- 协议只实现 ACP v1（`protocolVersion: 1`），不使用任何 ACP SDK；JSON-RPC 层使用 StreamJsonRpc。
- NuGet（`src/server/Directory.Packages.props`）：
  - 增加 `<PackageVersion Include="StreamJsonRpc" Version="2.25.29" />`，与 `CodexSdk.AppServer` 间接引用的版本相同。
  - 增加 `SharpCompress`：版本取 nuget.org 上的最新稳定版，加入前确认 `dotnet build` 的漏洞审计（`NU1903`/`NU1904`）通过。
  - `DiffPlex` 1.9.0、`Semver` 3.0.0 已存在。
  - `Agw.Agents.csproj` 增加 `SharpCompress`、`Semver` 的引用；`Agw.Agents.Execution.csproj` 增加 `StreamJsonRpc`、`DiffPlex` 的引用。项目之间的引用不变。
- 命名：
  - 枚举成员 `EngineKind.ACP = 5`，类名前缀 `Acp`，显示名称 `ACP`。
  - 客户端为 `ExternalAgentKind.ACP: 5`，extension SDK 为 `"acp"`。
  - 所有输出的 `AuthorName` 为 `acp`。
- 错误码（`Agw.Shared/Exceptions/ErrorCodes.cs`）：
  - `AcpRegistryUnavailable = new(502_0013, "The ACP registry could not be read.", HttpStatusCode.BadGateway)`
  - `AcpAgentNotInstalled = new(502_0014, "The ACP agent is not installed on this server.", HttpStatusCode.BadGateway)`
  - `AcpAgentStartFailed = new(502_0015, "The ACP agent process could not be started.", HttpStatusCode.BadGateway)`
  - `AcpAgentAuthenticationRequired = new(502_0016, "The ACP agent requires authentication.", HttpStatusCode.BadGateway)`
  - `AcpAgentRequestFailed = new(502_0017, "The ACP agent request failed.", HttpStatusCode.BadGateway)`
  - `AcpAgentTurnStopped = new(502_0018, "The ACP agent stopped the turn before completing it.", HttpStatusCode.BadGateway)`
  - `AcpDownloadFailed = new(502_0019, "The ACP download failed.", HttpStatusCode.BadGateway)`
  - `AcpAgentTimeout = new(504_0002, "The ACP agent did not respond in time.", HttpStatusCode.GatewayTimeout)`
  - `AcpInstallFailed = new(500_0034, "The ACP agent installation failed.", HttpStatusCode.InternalServerError)`
- 接口：JSON 接口返回 Bens.Results（`ApiResult.Ok(...)`），新增路由的参数放在 query 中。预期失败抛出 `AgwException(ErrorCodes.X[, message])`。
- 当前用户通过 `UserInfoUtil.RequiredUserId` 或 `IUserInfoService` 读取，不增加 `userId` 参数。
- 进程启动：
  - 使用 `ProcessStartInfo.ArgumentList`。
  - `Environment` 先清空，再写入设计 3.3 的结果。
  - Windows 上不经过 `cmd.exe`。
  - 环境变量的值、消息内容与工具参数不写入日志与 Activity。
- Domain 目录（`Definitions/Domain/`）中的代码不使用 System.Text.Json（`SemanticBoundaryTests.Domain_DoesNotDependOnJsonSerialization`）。
- 测试：
  - xUnit v3，命名为 `Method_Condition_ExpectedResult`，使用真正运行的实现、SQLite 与 PostgreSQL，不使用 mock 或替身。
  - ACP agent 统一使用任务 5 的测试 agent（Node.js + `@agentclientprotocol/sdk`）；registry 统一使用任务 4 的 `AcpTestRegistryServer`（测试进程内的 Kestrel）。
  - 运行这些测试需要 `node` 与 `npm` 在 `PATH` 中。
  - `uvx` 用例从 Kestrel 安装 wheel 和 extras 依赖，从 `.scratch/acp-uvx-python` 的本地镜像安装 CPython 3.12。
  - 测试运行时产生的文件保存在测试项目的输出目录下。
- PostgreSQL 用例读取 `.scratch/postgres-tests.json` 中的 `ConnectionString`，运行之前向用户确认。
- 注释使用中文和英文两种语言，数量保持克制。时间使用 `DateTimeOffset` 和 `TimeProvider`。出站请求使用 `IHttpClientFactory`。
- 格式化：C# 使用 `dotnet csharpier format <修改过的文件>`；客户端在 `src/clients` 下使用 `pnpm fmt`。
- 修改 Domain、项目引用或持久化访问之后运行 `dotnet test --project tests/Agw.Architecture.Tests`；修改 `ErrorCodes` 之后运行 `dotnet test --project tests/Agw.Shared.Tests`。
- 过滤参数在本机无效时，先执行 `dotnet build tests/<项目>`，再运行 `dotnet tests/<项目>/bin/Debug/net10.0/<项目>.dll -class "<完整类名>"`。
- 后端接口变化后更新客户端类型：
  1. 用 `--no-launch-profile --environment Development`、显式 SQLite 连接（`.scratch/acp-openapi/agw.db`）、`--AgwDataDir=.scratch/acp-openapi/data` 和空闲端口启动 `src/server/Agw.Standalone.Host`。
  2. 下载 `/openapi/v1.json` 到 `src/clients/packages/api/openapi.json`。
  3. 在 `src/clients` 下运行 `pnpm gen:api`。
- 每个任务最后的提交步骤只在用户要求提交时执行，提交信息使用 Conventional Commits。不使用 Git 回退代码。
- 创建子代理时，提示词要求它使用 Rider MCP 工具查找和阅读代码。
- 不修改 `docs/human/` 下的文档。

## 复核重点

下面五种输入或状态在设计中没有对应的测试，但最容易在使用中出错。每一项的测试已经加入对应任务。

1. agent 在 stdout 输出一行非 JSON 文本（常见于打印到 stdout 的日志）：连接进入故障状态，Turn 以 `5020017` 失败，消息附 stderr，不会一直等待；下一个 Turn 重新启动进程。测试在任务 5 与任务 10。
2. 命令能启动但永远不响应 `initialize`（例如误配为一个普通的常驻程序）：到 `InitializeTimeout` 时得到 `5040002`，进程树被结束。测试在任务 5。
3. 属于其他 `sessionId` 的 `session/update`，或者 `session/prompt` 响应之后才到达的更新：不进入本次输出，也不进入下一个 Turn。测试在任务 8。
4. `tool_call_update` 或权限请求引用了没有先出现 `tool_call` 的 `toolCallId`：按新的工具调用处理，`toolTitle` 取请求中的 `title`。测试在任务 7 与任务 9。
5. 用户把整行命令写进 `command`（例如 `"npx -y foo"`）：不按空格切分，得到 `5020015`，消息为 `Command 'npx -y foo' was not found.`。测试在任务 5。

## 文件结构

| 位置 | 内容 |
| --- | --- |
| `src/server/Agw.Agents/ExternalAgents/Acp/AcpAgentSettings.cs` | Extra 的 record（不带 JSON 特性） |
| `src/server/Agw.Agents/ExternalAgents/Acp/AcpAgentOptions.cs` | 配置项与安装目录解析 |
| `src/server/Agw.Agents/ExternalAgents/Acp/AcpRegistryDocument.cs`、`AcpInstallManifest.cs`、`AcpPlatform.cs`、`AcpClientDiagnostics.cs` | registry 与安装清单的 record、平台键、`ActivitySource` 与 `Meter` |
| `src/server/Agw.Agents/ExternalAgents/Acp/AcpServiceCollectionExtensions.cs` | `AddAcpAgentInstallation`，供 `AddAgents` 与 `acp` 命令共用 |
| `src/server/Agw.Agents/Definitions/Domain/Rules/AcpAgentSettingsRules.cs`、`AcpRegistryRules.cs` | Extra 规则；下载地址、distribution 选择与 `cmd` 规范化 |
| `src/server/Agw.Agents/ExternalAgents/Acp/AcpAgentSettingsJson.cs` | Extra 的 JSON 转换 |
| `src/server/Agw.Agents/Definitions/Agents/AcpRegistryAppService.cs` | registry 查询 |
| `src/server/Agw.Agents/Definitions/Contracts/AcpRegistryContracts.cs` | 查询接口的 DTO |
| `src/server/Agw.Agents/Application/Acp/IAcpRegistryClient.cs`、`IAcpAgentInstaller.cs`、`IAcpInstalledAgentCatalog.cs` | 接口 |
| `src/server/Agw.Agents/Infrastructure/Acp/AcpRegistryClient.cs`、`AcpDownloadClient.cs`、`AcpAgentInstaller.cs`、`AcpPackageInstaller.cs`、`AcpArchiveExtractor.cs`、`AcpInstalledAgentCatalog.cs` | registry、下载、安装、解压与已安装版本查找 |
| `src/server/Agw.Agents/ExternalAgents/Acp/AcpCommand.cs` | `acp install` 命令 |
| `src/server/Agw.Agents.Execution/Agents/ExternalAgents/Acp/Protocol/AcpSchema.cs`、`AcpJson.cs` | 协议 record 与序列化选项 |
| `src/server/Agw.Agents.Execution/Agents/ExternalAgents/Acp/AcpConnection.cs`、`AcpClientMethods.cs`、`AcpCancellationStrategy.cs`、`AcpCommandResolver.cs`、`AcpProcessEnvironment.cs`、`AcpStderrBuffer.cs` | 进程、JSON-RPC 连接与命令查找 |
| `src/server/Agw.Agents.Execution/Agents/ExternalAgents/Acp/AcpLaunchResolver.cs`、`AcpPromptBuilder.cs`、`AcpUpdateMapper.cs`、`AcpAgent.cs`、`AcpAgentSession.cs`、`AcpPermissionBridge.cs` | 执行适配器 |
| `src/server/Agw.Agents.Execution/Agents/ExternalAgents/ExternalToolApprovalCache.cs` | 由 `ClaudeCode/ClaudeToolApprovalCache.cs` 移动并改名 |
| `tests/Agw.Agents.Tests/Acp/TestAgent/` | `package.json`、`package-lock.json`、`acp-test-agent.mjs` |
| `tests/Agw.Agents.Tests/Acp/` | `AcpTestAgent`、`AcpTestRegistryServer`、`AcpAgentHarness` 与本功能的全部 Server 测试 |
| `src/clients/packages/agents/src/ui-web/pages/agents/components/acp-registry-picker.tsx`、`acp-settings.ts` | registry 选择器与 Extra 规则 |

---

### 任务 1：Engine 种类、Extra 规则、配置项与错误码

**文件：**
- 创建：
  - `Agw.Agents/ExternalAgents/Acp/AcpAgentSettings.cs`、`AcpAgentOptions.cs`、`AcpServiceCollectionExtensions.cs`
  - `Agw.Agents/Definitions/Domain/Rules/AcpAgentSettingsRules.cs`
  - `Agw.Agents/Definitions/Domain/Rules/AcpRegistryRules.cs`（本任务只实现 `IsAllowedDownloadUri`，其余方法在任务 4 加入）
  - `Agw.Agents/ExternalAgents/Acp/AcpAgentSettingsJson.cs`
- 修改：
  - `Agw.Agents.Contracts/Execution/EngineKind.cs`
  - `Agw.Agents/Definitions/Domain/Rules/ExternalAgentRules.cs`、`Agw.Agents/ExternalAgents/ExternalAgentDefaults.cs`
  - `Agw.Agents.Execution/Agents/ExternalAgents/EngineKinds.cs`
  - `Agw.Agents/Definitions/Domain/Behaviors/AgentBehavior.cs`（Extra 与 response schema 校验）
  - `Agw.Agents/DependencyInjection.cs`、`Agw.Host/appsettings.json`、`Agw.Shared/Exceptions/ErrorCodes.cs`
  - `src/server/Directory.Packages.props`、`Agw.Agents.csproj`、`Agw.Agents.Execution.csproj`
- 测试：
  - `tests/Agw.Agents.Tests/Acp/AcpAgentSettingsTests.cs`、`AcpAgentDefinitionTests.cs`、`AcpAgentOptionsTests.cs`
  - `tests/Agw.Agents.Tests/ExternalAgentDefaultsTests.cs`

**接口：**
- 产出：
  - `EngineKind.ACP = 5`
  - `EngineKinds.UsesProviderSessionBinding(EngineKind.ACP) == true`
  - `public static bool EngineKinds.SupportsDistributedExecution(EngineKind kind)`，对 `Maf`、`A2A`、`ACP` 返回 true
  - `public sealed record AcpAgentSettings(AcpRegistryReference? Registry, string? Command, IReadOnlyList<string>? Args, string? AuthMethodId, string? ModeId, IReadOnlyDictionary<string, AcpConfigOptionValue>? ConfigOptions)`
  - `public sealed record AcpRegistryReference(string AgentId, string? Version)`
  - `public sealed record AcpConfigOptionValue(string? Text, bool? Boolean)`，两者恰好一个有值
  - `public static AcpAgentSettings AcpAgentSettingsJson.Parse(string extra)`
  - `public static void AcpAgentSettingsRules.Validate(AcpAgentSettings settings)`
  - `public static bool AcpRegistryRules.IsAllowedDownloadUri(Uri uri)`：`https`，或者主机为 `localhost`、`127.0.0.0/8`、`::1` 的 `http`
  - `AcpAgentOptions`：`SectionName = "AcpAgents"`，属性与默认值见设计第 11 节（`RegistryUrl` 类型为 `Uri`，`InstallRoot` 类型为 `string?`）；方法 `public string ResolveInstallRoot(AgwDataPaths paths)`，`InstallRoot` 为空时返回 `Path.Combine(paths.Root, "acp-agents")`
  - `public static IServiceCollection AddAcpAgentInstallation(this IServiceCollection services, IConfiguration configuration)`：本任务只注册 `AcpAgentOptions`（`Bind`、`Validate`、`ValidateOnStart`），任务 4 与任务 6 在这里继续注册
  - 全局约束中的 registry 与执行错误码。任务 6 增加下载与安装错误码。

- [x] **步骤 1：编写失败的测试**

`AcpAgentSettingsTests`：
- `Parse_RegistryExample_ReturnsSettings`：解析设计 1.2 的第一个示例，断言 `Registry.AgentId == "goose"`、`Registry.Version == null`、`ConfigOptions["model"].Text == "claude-sonnet-4-5"`。
- `Parse_UnknownKey_ThrowsInvalidParam`：`{"command":"x","comand":"y"}`，断言错误码为 `InvalidParam`，消息包含 `comand`。
- `Validate_RegistryAndCommand_ThrowsInvalidParam`：三个用例 `{}`、`{"registry":{"agentId":"a"},"command":"b"}`、`{"command":"  "}`。`{}` 的消息为 `ACP agents require either registry or command in Extra Settings.`。
- `Validate_ArgsWithRegistry_ThrowsInvalidParam`。
- `Validate_RegistryIdOrVersionFormat_ThrowsInvalidParam`：`agentId` 为 `"Goose"` 与 `"1abc"`；`version` 为 `"1.2"`。
- `Validate_ConfigOptionValueObject_ThrowsInvalidParam`：`{"command":"x","configOptions":{"model":{"a":1}}}`。
- `Validate_Limits_ThrowsInvalidParam`：65 个 `args`；33 个 `configOptions`；长度为 1001 的值；含 `\n` 的 `command`。

`AcpAgentDefinitionTests`（使用 `AgentAppService` 与 SQLite，参照 `A2AAgentDefinitionTests` 的初始化方式）：
- `CreateAgent_AcpCommand_StoresExtra`。
- `CreateAgent_AcpEmptyExtra_ThrowsInvalidParam`：`Extra` 为 null，回填 `{}` 后被拒绝。
- `PrepareForCreate_AcpWithResponseSchema_ThrowsInvalidParam`。
- `CreateAgent_AcpWithModelProvider_ThrowsInvalidParam`：断言消息以 `ACP does not support` 开头。
- `UpdateAgent_AcpInvalidExtra_ThrowsInvalidParam`。
- `GetExternalOptions_IncludesAcp`：`kind == EngineKind.ACP`、`displayName == "ACP"`、`defaultExtra == "{}"`、`supportedProviderTypes` 为空。

`AcpAgentOptionsTests`：
- `Bind_DefaultSection_UsesDocumentedDefaults`：断言第 11 节的每个默认值，以及 `ResolveInstallRoot` 返回 `{root}/acp-agents`。
- `Validate_HttpRegistryUrl_FailsStartup`：`http://example.com/r.json` 失败；`http://127.0.0.1:5000/registry.json` 通过；`InstallRoot` 为相对路径时失败。

`ExternalAgentDefaultsTests`：增加 `GetDisplayName_Acp_ReturnsAcp`。

`AcpRegistryRulesTests`：
- `IsAllowedDownloadUri_Schemes_MatchDesign`：
  - `https://x` 为 true。
  - `http://x` 为 false。
  - `http://localhost`、`http://127.0.0.5`、`http://[::1]` 为 true。
  - `file:///x` 为 false。

- [x] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*Acp*"`
预期：编译失败，`EngineKind.ACP` 不存在。

- [x] **步骤 3：实现**

- `AcpAgentSettingsJson.Parse`：
  - 用私有 DTO 反序列化，`configOptions` 为 `Dictionary<string, JsonElement>`。
  - `JsonSerializerOptions` 使用 `JsonNamingPolicy.CamelCase` 与 `UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow`。
  - `JsonException` 转换为 `InvalidParam`，消息包含出错的属性名。
  - 字符串与布尔以外的值转换为 `AcpConfigOptionValue(null, null)`，由 `Validate` 拒绝。
- 选项校验中的 `RegistryUrl` 检查调用 `AcpRegistryRules.IsAllowedDownloadUri`。
- `AgentAppService` 归一化 Extra 后交给 `AgentBehavior`。创建 ACP Agent 和更新请求指定 Extra 时，`AgentBehavior` 通过 `AcpAgentSettingsJson` 与 `AcpAgentSettingsRules` 完成校验。
- `appsettings.json` 增加 `AcpAgents` 段。`InstallRoot` 写为 `null`，其余写默认值。

- [x] **步骤 4：运行测试，确认通过**

运行：
- `dotnet test --project tests/Agw.Agents.Tests --filter-class "*Acp*"`
- `dotnet test --project tests/Agw.Agents.Tests --filter-class "*ExternalAgentDefaultsTests"`
- `dotnet test --project tests/Agw.Shared.Tests`
- `dotnet test --project tests/Agw.Architecture.Tests`

预期：全部通过。

- [ ] **步骤 5：提交**（用户要求时）

```bash
git add src/server tests/Agw.Agents.Tests
git commit -m "feat(agents): add ACP engine kind and extra settings rules"
```

---

### 任务 2：执行能力与 Distributed 判断

**文件：**
- 修改：
  - `Agw.Agents.Contracts/Execution/ExecutionRuntimeContracts.cs`（`ExecutionRuntimeConfiguration`）
  - `Agw.Agents/Definitions/Agents/ExecutionInputService.cs`
  - `Agw.Agents/Definitions/Agents/ExecutionPermissionService.cs`
  - `Agw.Agents/Definitions/Contracts/ExecutionInputCapabilities.cs`
  - `Agw.Agents.Execution/Agents/Runtime/AgentRuntimeFactory.CreateExternalAgents.cs`（第 197、233、788 行的 `Validate` 调用）
  - `Agw.Agents.Execution/Runtimes/Durable/DurableExecutionCoordinator.cs`（`RunAgentSegmentAsync` 中的拒绝条件）
- 测试：
  - 修改：`tests/Agw.Agents.Tests/ExecutionPermissionServiceTests.cs`、`tests/Agw.Agents.Tests/A2A/A2AEngineCapabilityTests.cs`、`tests/Agw.Agents.Tests/A2A/A2ARemoteAgentDurableTests.cs`（第 77 行断言的消息）
  - 创建：`tests/Agw.Agents.Tests/Acp/AcpEngineCapabilityTests.cs`

**接口：**
- 消费：任务 1 的 `EngineKind.ACP`、`EngineKinds.SupportsDistributedExecution`。
- 产出：
  - `ExecutionProviderOptions` 放在 `Agw.Agents/Execution/Configuration`，提供共享的 `Provider`。`ExecutionRuntimeOptions` 保留在执行模块。模块注册和运行时读取同一份启动配置。
  - `public static ExecutionPermissionCapabilities ExecutionPermissionService.ForAgent(Agent agent, ExecutionProvider provider)`
  - `public static void ExecutionPermissionService.Validate(Agent agent, ExecutionProvider provider, AgwPermissionMode? mode)`
  - `ExecutionPermissionService` 与 `ExecutionInputService` 注入 `IOptions<ExecutionProviderOptions>`，将 `Provider` 用于查询能力。

- [x] **步骤 1：编写失败的测试**

`AcpEngineCapabilityTests`：
- `ForAgent_AcpInProcess_AllowsAllPermissionModes`：三种模式，`SupportsNoTools` 为 false，`Reason` 为 null。
- `ForAgent_AcpDistributed_AllowsFullAccessOnly`：只有 `FullAccess`，说明文字为 `ACP agents support Full access only with the Distributed execution provider.`。
- `ForAgent_CodexDistributed_Unchanged`：与 InProcess 的结果相同。
- `ForEngine_AcpDistributed_ReportsAcpReason` 与 `ForEngine_AcpInProcess_ReportsAcpReason`：不支持，说明文字为 `ACP agents do not accept input while a turn is running.`。
- `SupportsDistributedExecution_Kinds_MatchDesign`：`Maf`、`A2A`、`ACP` 为 true；`ClaudeCode`、`Codex`、`Pi` 为 false。

已有测试传入 `ExecutionProvider.InProcess`。`ExecutionPermissionServiceTests` 使用 `IOptions<ExecutionProviderOptions>`。`ExecutionRuntimeConfigurationTests` 验证定义服务和运行时共享启动配置，以及无效 Provider 名称的处理。`A2ARemoteAgentDurableTests` 断言的消息为 `Distributed execution supports System Agents, A2A Agents and ACP Agents.`。

- [x] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*AcpEngineCapabilityTests"`
预期：编译失败，`ForAgent` 没有两个参数的重载。

- [x] **步骤 3：实现**

- `ExecutionInputCapabilities.ForEngine`：ACP 分支放在 `_ when provider == ExecutionProvider.Distributed` 之前。
- `AgentRuntimeFactory` 从 `IOptions<ExecutionRuntimeOptions>` 读取启动时的 `Provider`，所有权限模式校验都使用它。
- `DurableExecutionCoordinator`：条件改为 `!EngineKinds.SupportsDistributedExecution(runtime.EngineKind)`，并改用上面的新消息。

- [x] **步骤 4：运行测试，确认通过**

运行：
- `dotnet test --project tests/Agw.Agents.Tests --filter-class "*EngineCapabilityTests"`
- `dotnet test --project tests/Agw.Agents.Tests --filter-class "*ExecutionPermissionServiceTests"`

预期：全部通过。`A2ARemoteAgentDurableTests` 需要 PostgreSQL，运行前向用户确认。

- [ ] **步骤 5：提交**（用户要求时）

```bash
git add src/server tests/Agw.Agents.Tests
git commit -m "feat(agents): report ACP execution capabilities per provider"
```

---

### 任务 3：provider session ID 改为字符串

**文件：**
- 修改：
  - `Agw.Agents.Execution/Agents/Contracts/CreateAiAgentRequest.cs`
  - `Agw.Agents.Execution/Agents/Runtime/ExternalProviderSessionBindings.cs`、`AgentRuntimeFactory.CreateRuntime.cs`、`AgentRuntimeFactory.CreateExternalAgents.cs`
  - `Agw.Agents.Execution/Agents/ExternalAgents/Codex/CodexAgentRuntimeOptionsMapper.cs`（入口参数）
- 测试：
  - 创建：`tests/Agw.Agents.Tests/Acp/AcpProviderSessionTests.cs`
  - 修改：`ProviderSessionRuntimeTests.cs`、`AgentRuntimeFactoryCompositionTests.cs` 中使用 `Guid?` 的位置

**接口：**
- 产出：
  - `CreateAiAgentRequest.ProviderSessionId` 的类型为 `string?`
  - `internal string? ExternalProviderSessionBindings.ResolveProviderSessionId(Agent agent, ExternalProviderSessionState? state)`
  - `internal static (string? ProviderSessionId, bool IsResume) ResolveExternalProviderSession(Agent agent, string? persistedProviderSessionId, bool requestedResume)`

- [x] **步骤 1：编写失败的测试**

`AcpProviderSessionTests`：
- `ResolveProviderSessionId_AcpNonGuid_ReturnsRawValue`：值为 `"sess_01JABC"`，返回原值。
- `ResolveProviderSessionId_AcpTooLong_ReturnsNull`：201 个字符，返回 null 并记录 Warning。
- `ResolveProviderSessionId_PiNonGuid_ReturnsNull`。
- `ResolveExternalProviderSession_AcpPersisted_ResumesWithValue`：返回 `("sess_01JABC", true)`；没有保存值时返回 `(null, false)`。
- `ResolveExternalProviderSession_ClaudeWithoutValue_GeneratesGuidString`：返回值能被 `Guid.Parse` 解析，`IsResume` 为 false。

- [x] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*AcpProviderSessionTests"`
预期：编译失败，`ResolveProviderSessionId` 不存在。

- [x] **步骤 3：实现**

- `CreateClaudeCodeAgent`、`CreateCodexAgentAsync`、`CreatePiAgent` 的参数改为 `string?`，在函数开头用 `Guid.Parse` 转换。值已由 `ResolveProviderSessionId` 校验为 Guid 格式。
- 原有 `Guid?` 的下游类型（如 `CodexAgentRuntimeOptions.ProviderSessionId`）不变。

- [x] **步骤 4：运行测试，确认通过**

运行：
- `dotnet test --project tests/Agw.Agents.Tests --filter-class "*AcpProviderSessionTests"`
- `dotnet test --project tests/Agw.Agents.Tests --filter-class "*ProviderSession*"`
- `dotnet test --project tests/Agw.Agents.Tests --filter-class "*AgentRuntimeFactoryCompositionTests"`

预期：全部通过。

- [ ] **步骤 5：提交**（用户要求时）

```bash
git add src/server tests/Agw.Agents.Tests
git commit -m "refactor(agents): carry provider session ids as strings"
```

---

### 任务 4：registry 读取、平台、distribution 选择与查询接口

**文件：**
- 创建：
  - `Agw.Agents/ExternalAgents/Acp/AcpRegistryDocument.cs`、`AcpPlatform.cs`、`AcpClientDiagnostics.cs`
  - `Agw.Agents/Definitions/Domain/Rules/AcpRegistryRules.cs`
  - `Agw.Agents/Application/Acp/IAcpRegistryClient.cs`
  - `Agw.Agents/Infrastructure/Acp/AcpDownloadClient.cs`、`AcpRegistryClient.cs`
  - `Agw.Agents/Definitions/Agents/AcpRegistryAppService.cs`、`Agw.Agents/Definitions/Contracts/AcpRegistryContracts.cs`
- 修改：`Agw.Agents/ExternalAgents/Acp/AcpServiceCollectionExtensions.cs`、`Agw.Agents/Definitions/Controllers/AgentsController.cs`
- 测试：`tests/Agw.Agents.Tests/Acp/AcpTestRegistryServer.cs`、`AcpRegistryRulesTests.cs`、`AcpRegistryClientTests.cs`、`AcpRegistryAppServiceTests.cs`、`AcpPlatformTests.cs`

**接口：**
- 产出：
  - `AcpRegistryDocument(string Version, IReadOnlyList<AcpRegistryAgent> Agents)`
  - `AcpRegistryAgent` 包含 `Id`、`Name`、`Version`、`Description`、`Repository`、`Website`、`License`、`Distribution` 和 `Authors`。
  - `AcpRegistryDistribution(IReadOnlyDictionary<string, AcpBinaryTarget>? Binary, AcpPackageDistribution? Npx, AcpPackageDistribution? Uvx)`
  - `AcpBinaryTarget(Uri Archive, string Cmd, IReadOnlyList<string>? Args, IReadOnlyDictionary<string, string>? Env, string? Sha256)`
  - `AcpPackageDistribution(string Package, IReadOnlyList<string>? Args, IReadOnlyDictionary<string, string>? Env)`
  - `public static string? AcpPlatform.Current { get; }`：没有平台键时为 null
  - `public static string? AcpPlatform.Resolve(OSPlatform os, Architecture arch, string runtimeIdentifier)`：供测试使用
  - `AcpRegistryRules`（任务 1 已有 `IsAllowedDownloadUri`）：
    - `string NormalizeCmd(string cmd)` 校验相对路径，非法时抛出 `AgwException(ErrorCodes.InvalidParam)`。
    - `AcpDistributionSelection SelectDistribution(AcpRegistryAgent entry, AcpDistributionHost host, bool allowUnverifiedBinaries)`
  - `AcpDistributionSelection` 提供发行方式、二进制目标或软件包，以及不可用原因。`Kind` 使用 `AcpDistributionKinds` 声明的 `binary`、`npx`、`uvx`，没有可用方式时为 null。
  - `IAcpRegistryClient`：
    - `Task<AcpRegistrySnapshot> GetAsync(bool refresh, CancellationToken ct)`
    - `Task<AcpRegistrySnapshot> FetchAsync(CancellationToken ct)`
  - `AcpRegistrySnapshot(AcpRegistryDocument Document, DateTimeOffset FetchedAt, Uri SourceUrl)`
  - `AcpDownloadClient`：
    - 命名 `HttpClient` 为 `acp-registry`，`AllowAutoRedirect = false`
    - `Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)`：最多跟随 5 次 301、302、303、307、308 重定向，每一跳先检查 `IsAllowedDownloadUri`
  - `GET /api/agents/acp-registry?refresh=` 返回 `AcpRegistryResponse(Uri SourceUrl, DateTimeOffset FetchedAt, IReadOnlyList<AcpRegistryAgentResponse> Agents)`
  - `AcpRegistryAgentResponse(string Id, string Name, string Version, string Description, string? Repository, string? Website, string? License, IReadOnlyList<string> Distributions)`
  - 测试：`AcpTestRegistryServer`
    - `static Task<AcpTestRegistryServer> StartAsync()`
    - `Uri RegistryUrl`
    - `void SetRegistry(string json)`
    - `Uri AddFile(string path, byte[] content)`
    - `int RegistryRequestCount`
    - `Task StopAsync()`
    - 支持 `ETag` 与 `If-None-Match`

- [x] **步骤 1：编写失败的测试**

`AcpPlatformTests`：
- `Resolve_KnownPlatforms_ReturnsKeys`：macOS Arm64 为 `darwin-aarch64`；Linux X64 且 RID 为 `linux-musl-x64` 时为 `linux-x86_64-musl`；Windows X64 为 `windows-x86_64`；Linux S390x 为 null。
- `Current_Platform_MatchesRuntime`。

`AcpRegistryRulesTests`：
- `NormalizeCmd_BackslashAndDotPrefix_ReturnsRelativePath`：`./goose-package\goose.exe` 得到 `goose-package/goose.exe`；`amp-acp.exe` 得到 `amp-acp.exe`；`/bin/x`、`C:\x.exe`、`./a/../../b` 抛出异常。
- 用设计中 registry 原文的 goose、codex-acp、fast-agent 条目做以下用例：
  - `SelectDistribution_BinaryWithSha_PrefersBinary`
  - `SelectDistribution_BinaryWithoutSha_FallsBackToNpx`
  - `SelectDistribution_NpmMissing_ReportsReasons`：`Kind` 为 null，`Reasons` 依次为 `binary: no target for linux-x86_64`、`npx: npm was not found`、`uvx: not provided`
  - `SelectDistribution_MuslWithoutMuslTarget_SkipsBinary`
  - `SelectDistribution_WindowsCmdScript_SkipsBinary`

`AcpRegistryClientTests`（使用 `AcpTestRegistryServer`）：
- `GetAsync_FirstCall_ParsesDocumentIgnoringUnknownFields`：registry 顶层带 `"extensions": []`。
- `GetRegistry_NotModified_KeepsSnapshot`：`refresh=true` 的第二次请求带 `If-None-Match`，收到 304；`FetchedAt` 更新，`Document` 不变。
- `GetRegistry_ServerStopped_UsesMemorySnapshot`。
- `GetAsync_NoSnapshotServerDown_ThrowsUnavailable`：错误码 `5020013`。
- `GetAsync_RegistryLargerThanLimit_ReturnsUnavailable`：`RegistryMaxBytes = 1024`。
- `GetAsync_ConcurrentCalls_SendOneRequest`：并发十次，`RegistryRequestCount == 1`。
- `SendAsync_RedirectToDisallowedUri_Fails`：重定向到 `http://example.com`。
- `GetAsync_FetchOutcomes_RecordedInMeter`：用 `MeterListener` 收集 `agw.acp.client.registry_fetches`，第一次为 `fetched`，304 之后为 `not_modified`。

`AcpRegistryAppServiceTests`：
- `GetAsync_Distributions_ListedInOrder`：同时有 `npx` 与 `binary` 的条目，`Distributions` 为 `["binary","npx"]`。

- [x] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*AcpRegistry*"`
预期：编译失败。

- [x] **步骤 3：实现**

- `AcpRegistryClient`：内存快照与单个进行中的请求用 `SemaphoreSlim` 保护；判断是否过期用 `TimeProvider`。
- `AcpRegistryAppService`：`Distributions` 列出条目中存在的种类，顺序为 `binary`、`npx`、`uvx`。
- `AgentsController` 增加 `[HttpGet("acp-registry")]`。
- 任务 1 中的地址校验改为调用 `IsAllowedDownloadUri`。

- [x] **步骤 4：运行测试，确认通过**

运行：
- `dotnet test --project tests/Agw.Agents.Tests --filter-class "*AcpRegistry*"`
- `dotnet test --project tests/Agw.Agents.Tests --filter-class "*AcpPlatformTests"`
- `dotnet test --project tests/Agw.Architecture.Tests`

预期：全部通过。

- [ ] **步骤 5：提交**（用户要求时）

```bash
git add src/server tests/Agw.Agents.Tests
git commit -m "feat(agents): read the ACP registry and expose it to the agent form"
```

---

### 任务 5：测试 agent 与 ACP 连接

**文件：**
- 创建：
  - `tests/Agw.Agents.Tests/Acp/TestAgent/package.json`、`package-lock.json`、`acp-test-agent.mjs`
  - `tests/Agw.Agents.Tests/Acp/AcpTestAgent.cs`
  - `Agw.Agents.Execution/Agents/ExternalAgents/Acp/Protocol/AcpSchema.cs`、`AcpJson.cs`
  - `Agw.Agents.Execution/Agents/ExternalAgents/Acp/AcpCommandResolver.cs`、`AcpProcessEnvironment.cs`、`AcpStderrBuffer.cs`、`AcpClientMethods.cs`、`AcpCancellationStrategy.cs`、`AcpConnection.cs`
- 修改：`tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj`（复制 `Acp/TestAgent/**` 到输出目录，排除 `node_modules`）、`.gitignore`（`tests/Agw.Agents.Tests/Acp/TestAgent/node_modules/`）
- 测试：`tests/Agw.Agents.Tests/Acp/AcpJsonTests.cs`、`AcpProcessEnvironmentTests.cs`、`AcpCommandResolverTests.cs`、`AcpConnectionTests.cs`

**接口：**
- 产出：
  - 测试 agent（`acp-test-agent.mjs`）
    - 包名 `agw-acp-test-agent`，`bin` 为 `{ "agw-acp-test-agent": "acp-test-agent.mjs" }`，依赖 `@agentclientprotocol/sdk` 2.9.1。
    - 使用该 SDK 的 agent 侧 API；实施前阅读 2.9.1 的 README，选用它推荐的入口。
    - 参数：`<scenario.json> <log.jsonl>`。
    - 场景文件：
      - 声明：`capabilities`（`loadSession`、`resume`、`close`、`image`、`audio`、`embeddedContext`、`additionalDirectories`）、`authMethods`、`requireAuth`、`modes`、`configOptions`、`replay`（`session/load` 时回放的 update 数组）。
      - `turns` 数组，每项包含 `steps` 与 `stopReason`（默认 `end_turn`），以及可选的 `ignoreCancel`。
      - `steps` 的每一项是以下之一：
        - `{ "update": {...} }`：原样作为 `session/update` 的 `update` 发出
        - `{ "updateForSession": "<id>", "update": {...} }`
        - `{ "permission": { "toolCall": {...}, "options": [...] } }`：发出 `session/request_permission`，并把结果写入记录
        - `{ "delayMs": n }`
        - `{ "stdout": "text" }`：向 stdout 写一行原始文本
        - `{ "exit": n, "stderr": "text" }`
    - 第 n 次 `session/prompt` 使用 `turns[n]`。下标与已建立的 session 保存在 `<log.jsonl>.state.json`，进程重启后保留；删除该文件相当于 agent 的会话目录不可见。
    - 记录文件每行一个 JSON：进程启动时写 `{"event":"start","pid":..,"cwd":..,"argv":[..],"envNames":[..]}`，每个收到的请求或通知写 `{"method":..,"params":..}`，每个权限结果写 `{"event":"permission","outcome":..}`。
  - `AcpTestAgent`（测试）
    - `static Task<AcpTestAgent> EnsureInstalledAsync()`：在输出目录执行一次 `npm ci --no-audit --no-fund`，进程内只执行一次
    - `string ScriptPath`
    - `string CreateScenario(object scenario, out string logPath)`
    - `IReadOnlyList<JsonElement> ReadLog(string logPath)`
    - `Task<string> PackAsync()`：`npm pack` 得到 tarball 路径，任务 6 使用
  - 协议 record：设计 4.2 列出的方法。
    - 主要类型：`AcpInitializeRequest`、`AcpInitializeResponse`、`AcpAgentCapabilities`、`AcpPromptCapabilities`、`AcpSessionCapabilities`、`AcpAuthMethod`、`AcpNewSessionRequest`、`AcpNewSessionResponse`、`AcpResumeSessionRequest`、`AcpLoadSessionRequest`、`AcpSessionStateResponse`、`AcpPromptRequest`、`AcpPromptResponse`、`AcpSetModeRequest`、`AcpSetConfigOptionRequest`、`AcpSetConfigOptionResponse`、`AcpSessionModeState`、`AcpSessionConfigOption`、`AcpSessionNotification`、`AcpRequestPermissionRequest`、`AcpPermissionOption`、`AcpRequestPermissionResponse`、`AcpPermissionOutcome`。
    - `AcpSessionCapabilities` 的 `Resume`、`Close`、`AdditionalDirectories` 类型为 `JsonElement?`，存在即表示支持。
    - `AcpSessionUpdate`（非 abstract 基类，`sessionUpdate` 区分，`IgnoreUnrecognizedTypeDiscriminators = true`）的派生类型：`AcpAgentMessageChunk(AcpContentBlock Content, string? MessageId)`、`AcpAgentThoughtChunk`、`AcpUserMessageChunk`、`AcpToolCall`、`AcpToolCallUpdate`、`AcpPlan(IReadOnlyList<AcpPlanEntry> Entries)`、`AcpCurrentModeUpdate(string CurrentModeId)`、`AcpConfigOptionUpdate(IReadOnlyList<AcpSessionConfigOption> ConfigOptions)`。
    - `AcpToolCall` 与 `AcpToolCallUpdate` 都有字段 `ToolCallId`、`Title`、`Kind`、`Status`、`Content`、`RawInput`、`RawOutput`，后者只有 `ToolCallId` 必填。
    - `AcpContentBlock` 的派生类型：`AcpTextContent`、`AcpImageContent`、`AcpAudioContent`、`AcpResourceLink`、`AcpEmbeddedResource`。
    - `AcpToolCallContent` 的派生类型：`AcpToolCallContentBlock`、`AcpToolCallDiff(string Path, string? OldText, string NewText)`、`AcpToolCallTerminal`、`AcpToolCallSession`。
  - 常量类：`AcpStopReasons`、`AcpToolCallStatuses`、`AcpPermissionOptionKinds`，以及 `AcpRpcErrorCodes`（`AuthRequired = -32000`、`ResourceNotFound = -32002`）。
  - `AcpJson.Options`：camelCase、`AllowOutOfOrderMetadataProperties = true`、写入时忽略 null。
  - `static Task<AcpResolvedCommand> AcpCommandResolver.ResolveAsync(string command, IReadOnlyDictionary<string, string> environment, CancellationToken ct)`，返回 `AcpResolvedCommand(string Path, string? LoginShellPath)`
  - `static Dictionary<string, string> AcpProcessEnvironment.Build(IDictionary hostEnvironment, IReadOnlyDictionary<string, string> launchEnvironment, IReadOnlyDictionary<string, string> userEnvironment)`
  - `AcpLaunchSpec(string FileName, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment, string Source, string? Version)`：本任务定义，任务 8 产生
  - `interface IAcpClientHandler`
    - `void OnSessionUpdate(AcpSessionNotification notification)`：在有序上下文中同步调用
    - `Task<AcpRequestPermissionResponse> OnRequestPermissionAsync(AcpRequestPermissionRequest request, CancellationToken ct)`
  - `sealed class AcpConnection : IAsyncDisposable`
    - `static Task<AcpConnection> StartAsync(AcpLaunchSpec spec, string workingDirectory, IReadOnlyDictionary<string, string> userEnvironment, AcpAgentOptions options, IAcpClientHandler handler, ILogger logger, CancellationToken ct)`：启动进程并完成 `initialize`
    - `AcpInitializeResponse Initialize { get; }`
    - `int ProcessId { get; }`
    - `bool IsFaulted { get; }`
    - `string StderrTail { get; }`
    - `Task<T> InvokeAsync<T>(string method, object parameters, TimeSpan timeout, CancellationToken ct)`
    - `Task<T> InvokeOrderedAsync<T>(string method, object parameters, CancellationToken ct)`：响应完成后，在有序上下文中投递标记并等待它，再返回
    - `Task NotifyAsync(string method, object parameters)`
    - `Task KillAsync()`：结束整个进程树
  - JSON-RPC 错误统一转换为 `AcpRpcException(int Code, string Message)`；进程退出转换为 `AcpAgentRequestFailed`，消息包含退出码与 `StderrTail`。

- [x] **步骤 1：编写测试 agent 与失败的测试**

`AcpJsonTests`：
- `Deserialize_UnknownSessionUpdate_ReturnsBaseType`：`{"sessionUpdate":"cursor_update","cursorId":"c"}` 得到 `AcpSessionUpdate` 基类实例。
- `Deserialize_DiscriminatorNotFirst_ParsesDerived`：`{"content":{"text":"hi","type":"text"},"sessionUpdate":"agent_message_chunk"}` 得到 `AcpAgentMessageChunk`，内容为 `AcpTextContent("hi")`。
- `Serialize_Initialize_MatchesDesign`：与设计 4.3 的 JSON 相同。

`AcpProcessEnvironmentTests`：
- `Build_Environment_LaterSourcesOverride`：宿主 `PATH=/a`、`SECRET=x`、`LC_ALL=C`，launch `PATH=/b`，user `PATH=/c`、`K=v`。结果 `PATH=/c`、`K=v`、`LC_ALL=C`，没有 `SECRET`。

`AcpCommandResolverTests`：
- `ResolveAsync_NameOnPath_ReturnsAbsolutePath`：在测试目录中放一个可执行文件，并加入 `PATH`。
- `ResolveAsync_CommandWithSpaces_ThrowsStartFailed`：`"npx -y foo"` 得到 `5020015`，消息为 `Command 'npx -y foo' was not found.`（复核重点 5）。
- `ResolveAsync_WindowsCmdScript_ThrowsStartFailed`：只在 Windows 上执行。

`AcpConnectionTests`（使用测试 agent，`command` 为 `node`，参数为脚本路径）：
- `StartAsync_TestAgent_InitializesWithProtocolVersion1`：记录中有 `initialize`，参数与设计 4.3 相同；`Initialize.ProtocolVersion == 1`。
- `StartAsync_NeverResponds_TimesOutAndKills`：命令为 `node -e "setInterval(()=>{},1000)"`，`InitializeTimeout = 2s`，得到 `5040002`，进程已退出（复核重点 2）。
- `StartAsync_ProcessExitsEarly_ThrowsStartFailedWithStderr`：命令为 `node -e "console.error('boom');process.exit(3)"`，得到 `5020015`，消息包含 `boom`。
- `InvokeOrderedAsync_UpdatesBeforeResponse_AreDeliveredFirst`：场景先发 50 个 `agent_message_chunk` 再结束本轮；返回之前处理函数已经收到全部 50 个。
- `Connection_NonJsonStdoutLine_Faults`：场景步骤 `{ "stdout": "hello" }`；`IsFaulted` 变为 true，进行中的请求得到 `5020017`（复核重点 1）。
- `Connection_UnknownMethodFromAgent_ReturnsMethodNotFound`：场景让 agent 调用 `fs/read_text_file`，记录中 agent 收到错误 `-32601`。

- [x] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*AcpJsonTests"`，以及 `AcpConnectionTests`、`AcpProcessEnvironmentTests`、`AcpCommandResolverTests` 三个类。
预期：编译失败。

- [x] **步骤 3：实现**

- `AcpConnection.StartAsync`：
  - 先用 `AcpCommandResolver` 查找 `spec.FileName`；login shell 命中时，用它的 `PATH` 替换环境中的 `PATH`。
  - `RedirectStandardInput`、`RedirectStandardOutput`、`RedirectStandardError` 都为 true。
  - 创建 `NewLineDelimitedMessageHandler(stdin, stdout, new SystemTextJsonFormatter { JsonSerializerOptions = AcpJson.Options })`。
  - `JsonRpc` 的 `SynchronizationContext` 设为 `new NonConcurrentSynchronizationContext(sticky: false)`，`CancellationStrategy` 设为 `AcpCancellationStrategy`。
  - 用 `AddLocalRpcTarget(new AcpClientMethods(handler), new JsonRpcTargetOptions { UseSingleObjectParameterDeserialization = true })` 注册入站方法。
- `AcpClientMethods` 用 `[JsonRpcMethod("session/update")]` 与 `[JsonRpcMethod("session/request_permission")]` 标注。
- `InvokeOrderedAsync`：请求完成后，用 `TaskCompletionSource` 在同一个 `SynchronizationContext` 上 `Post` 标记，再等待标记完成。
- `AcpStderrBuffer` 保留最近 `StderrTailLines` 行，总长度截断到 4000 个字符。

- [x] **步骤 4：运行测试，确认通过**

运行步骤 2 的命令。
预期：全部通过（Windows 专用用例在其他平台上不执行）。

- [ ] **步骤 5：提交**（用户要求时）

```bash
git add .gitignore src/server tests/Agw.Agents.Tests
git commit -m "feat(agents): add ACP protocol types and stdio JSON-RPC connection"
```

---

### 任务 6：安装、安装清单、已安装版本查找与 `acp install`

**文件：**
- 创建：
  - `Agw.Agents/ExternalAgents/Acp/AcpInstallManifest.cs`
  - `Agw.Agents/Application/Acp/IAcpAgentInstaller.cs`、`IAcpInstalledAgentCatalog.cs`
  - `Agw.Agents/Infrastructure/Acp/AcpAgentInstaller.cs`、`AcpPackageInstaller.cs`、`AcpArchiveExtractor.cs`、`AcpInstalledAgentCatalog.cs`
  - `Agw.Agents/ExternalAgents/Acp/AcpCommand.cs`
- 修改：`Agw.Agents/ExternalAgents/Acp/AcpServiceCollectionExtensions.cs`、`Agw.Host/Program.cs`
- 测试：`tests/Agw.Agents.Tests/Acp/AcpAgentInstallerTests.cs`、`AcpInstalledAgentCatalogTests.cs`、`AcpCommandTests.cs`、`AcpTestRegistryServer.cs`

**接口：**
- 消费：任务 4 的 `AcpRegistryRules`、`IAcpRegistryClient.FetchAsync`、`AcpDownloadClient`；任务 5 的 `AcpTestAgent.PackAsync`。
- 产出：
  - `AcpInstallManifest(string AgentId, string Name, string Version, Uri RegistryUrl, string Distribution, string Platform, string Runtime, string Executable, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env, DateTimeOffset InstalledAt)`
    - 文件名常量 `AcpInstallManifest.FileName = "agw-acp-agent.json"`
    - `Runtime` 取 `native` 或 `node`
  - `IAcpAgentInstaller.InstallAsync(AcpRegistryAgent entry, CancellationToken ct)`，返回 `Task<AcpInstallResult>`
    - `AcpInstallResult(string AgentId, string Version, string Distribution, string Directory, bool AlreadyInstalled)`
    - 失败时抛出 `AcpInstallException(string message)`
  - `IAcpInstalledAgentCatalog.Find(string agentId, string? version)`，返回 `AcpInstalledAgent`
    - `AcpInstalledAgent(AcpInstallManifest Manifest, string Directory, string ExecutablePath)`
    - 找不到时抛出 `AcpAgentNotInstalled`，消息格式见设计 2.6
  - `public static Task<bool> AcpCommand.TryRunAsync(string[] args, IConfiguration configuration, AgwDataPaths dataPaths)`：参数以 `acp install` 开头时返回 true，并设置 `Environment.ExitCode`

- [x] **步骤 1：编写失败的测试**

`AcpAgentInstallerTests`（`AcpTestRegistryServer` 提供 registry 与文件，`InstallRoot` 为测试目录）：
- `Install_TarBz2WithSha_WritesManifestAndMarksExecutable`：
  - 在测试中用 SharpCompress 生成 tar.bz2，内含 `goose`。
  - 断言 `{root}/goose/1.60.2/agw-acp-agent.json` 的 `runtime == "native"`、`executable == "goose"`、`args == ["acp"]`。
  - Unix 上断言文件模式包含 `UserExecute`。
- `Install_ZipWithWindowsCmd_NormalizesExecutable`：`cmd` 为 `./goose-package\goose.exe`，`executable` 为 `goose-package/goose.exe`。
- `Install_InvalidRelease_RemovesPartialDirectory` 中下载超限与 SHA-256 校验失败使用 `AcpDownloadFailed`。压缩包路径、链接、条目类型与启动文件校验失败使用 `AcpInstallFailed`。失败后清除临时目录。
- `Install_InvalidRegistryIdentity_ReportsInstallFailure` 和 `Install_InvalidUvxPackage_ReportsInstallFailure` 验证 registry 条目或包名无效时的 `AcpInstallFailed`。
- `Install_RawBinaryUrlWithQuery_NamesFileByCmd`：地址为 `/download/?file=x`，`cmd` 为 `./tool`，安装后存在 `tool`。
- `Install_AlreadyInstalled_Skips`：`AlreadyInstalled` 为 true，清单的 `installedAt` 没有改变。
- `Install_Npx_ResolvesBinEntrypoint`：`npx.package` 为 `AcpTestAgent.PackAsync()` 的路径，`env` 中有 `NODE_OPTIONS` 与 `KEEP=1`；清单的 `runtime == "node"`、`executable == "node_modules/agw-acp-test-agent/acp-test-agent.mjs"`、`env` 只有 `KEEP`。
- `Install_UvxWithManagedPython_RunsAfterMovingVersionDirectory` 使用 Kestrel 提供的 wheel 和本地 Python 镜像安装带 extras 的包，移动版本目录后启动 Python entry point。`Install_UvxWheelDataScript_RunsAfterMovingVersionDirectory` 对 wheel 中的 Python 与 POSIX shell 脚本执行同样的验证。

`AcpInstalledAgentCatalogTests`：
- `Find_NoVersion_ReturnsHighestSemver`：`1.9.0` 与 `1.10.0` 中选出 `1.10.0`。
- `Find_PlatformMismatch_SkipsVersion`：没有其他版本时得到 `5020014`，消息包含 `acp install goose`。
- `Find_ExecutableMissing_SkipsVersion`。

`AcpCommandTests`：
- `TryRunAsync_InstallWithoutDatabase_ReportsResultAndDiagnostics` 使用不存在的数据库地址，验证退出码、stdout 的安装结果，以及 stderr 的安装、下载与失败诊断。
- `TryRunAsync_OtherArgs_ReturnsFalse` 验证 `serve` 参数。

- [x] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*AcpAgentInstallerTests"`，以及 `AcpInstalledAgentCatalogTests`、`AcpCommandTests` 两个类。
预期：编译失败。

- [x] **步骤 3：实现**

- `AcpAgentInstaller` 按设计 2.3 与 2.4 实现，通过 Pi SDK 的 `CommandUtil` 查找 `npm` 和 `uv`。发布文件下载使用独立的 `DownloadTimeout`，下载与安装失败各有错误码。
- Python 随版本目录安装。使用 `uv pip install` 安装依赖，通过同目录的解释器启动 entry point 和 wheel 的 Python 脚本，原生命令直接执行。子进程失败时错误消息附上 stderr。
- `AcpArchiveExtractor` 使用 SharpCompress 解压 ZIP、gzip 和 BZip2。使用 `TarReader` 读取 tar 条目的类型与 Unix 权限，按设计 2.4 检查路径与链接。
- `AcpCommand`：
  - 在独立的 `ServiceProvider` 中注册数据路径、安装服务与输出到 stderr 的 Console Logger。
  - 对每个 agent id，在 `FetchAsync` 的结果中查找条目。
  - 把 `installed <id> <version> (<distribution>) at <dir>` 或 `already installed <id> <version>` 写到 stdout。
- `Program.cs`：在 `AgwDataPaths.ResolveFromConfiguration` 之后、`CreateLogger` 之前调用 `TryRunAsync`，返回 true 时结束。

- [x] **步骤 4：运行测试，确认通过**

运行步骤 2 的命令，以及 `dotnet test --project tests/Agw.Architecture.Tests`。
预期：全部通过。

- [ ] **步骤 5：提交**（用户要求时）

```bash
git add src/server tests/Agw.Agents.Tests
git commit -m "feat(agents): add acp install command and installed agent catalog"
```

---

### 任务 7：prompt 构造与更新转换

**文件：**
- 创建：`Agw.Agents.Execution/Agents/ExternalAgents/Acp/AcpPromptBuilder.cs`、`AcpUpdateMapper.cs`
- 修改：`Agw.Agents.Execution/Agents/Tools/ToolStateSnapshots.cs`
- 测试：`tests/Agw.Agents.Tests/Acp/AcpPromptBuilderTests.cs`、`AcpUpdateMapperTests.cs`

**接口：**
- 消费：任务 5 的协议 record。
- 产出：
  - `static IReadOnlyList<AcpContentBlock> AcpPromptBuilder.Build(IEnumerable<ChatMessage> messages, AcpPromptCapabilities? capabilities)`
  - `internal static ChatMessage ToolStateSnapshots.CreateTodoSnapshotMessage(string toolName, string callId, string messageId, IReadOnlyList<TodoSnapshotItem> items)`
    - `internal sealed record TodoSnapshotItem(string Id, string Title, string? Description, bool IsComplete)`
    - 与 `CreateTodoAsync` 共用私有的 `CreateMessage`；该方法增加 `messageId` 参数，`CreateTodoAsync` 传入原来生成的 Guid v7
  - `sealed class AcpUpdateMapper(string responseId)`
    - `IReadOnlyList<AgentResponseUpdate> Map(AcpSessionUpdate update)`
    - `IReadOnlyList<AgentResponseUpdate> EnsureToolCall(AcpToolCallUpdate toolCall)`：权限请求使用
    - `AcpToolCallState? GetToolCall(string toolCallId)`
    - `IReadOnlyList<AgentResponseUpdate> Complete()`：prompt 返回时输出尚未输出的调用与缺失的结果
    - `AgentResponseUpdate? CreateResult()`
  - `AcpToolCallState(string ToolCallId, string? Title, string? Kind, string? Status, IReadOnlyList<AcpToolCallContent>? Content, JsonElement? RawInput, JsonElement? RawOutput, bool CallEmitted, bool ResultEmitted)`

- [x] **步骤 1：编写失败的测试**

`AcpPromptBuilderTests`：
- `BuildPrompt_TextAndSystem_KeepsOrder`。
- `BuildPrompt_ImageWithoutCapability_ThrowsInvalidParam`：消息为 `This ACP agent does not accept image content.`。
- `BuildPrompt_ImageWithCapability_EncodesBase64`。
- `BuildPrompt_OtherData_UsesNamedResourceUri`：`DataContent.Name = "a.pdf"`，得到 `agw:attachment/a.pdf`；没有名称时为 `agw:attachment/1`。
- `BuildPrompt_UriContent_ProducesResourceLink`：`name` 取地址最后一段。
- `BuildPrompt_NothingConvertible_ThrowsInvalidParam`。

`AcpUpdateMapperTests`（`responseId = "r1"`）：
- `MapMessages_WithoutMessageId_GroupsContiguousChunks`：正文、正文、推理、正文四个分片，得到 `MessageId` 依次为 `r1-message-1`、`r1-message-1`、`r1-thought-1`、`r1-message-2`；`AuthorName == "acp"`。
- `MapMessages_WithMessageId_UsesPrefixedId`：`acp:m-9`。
- `MapToolCall_RawInputArrivesLater_EmitsCallOnInProgress`：
  - `tool_call`（pending，没有 `rawInput`）不输出。
  - `tool_call_update`（带 `rawInput {"path":"a"}`、`status: in_progress`）输出 `FunctionCallContent`，其中 `CallId == "t1"`、`Name == "read"`、`Arguments["path"] == "a"`、`InformationalOnly == true`，`AdditionalProperties["toolTitle"]` 为 `title`，`MessageId == "r1-tool-t1"`。
- `MapToolCall_UpdateForUnknownId_TreatedAsNew`（复核重点 4）。
- `MapToolResult_Diff_ProducesUnifiedDiff`：断言结果以 `--- a/src/x.ts` 与 `+++ b/src/x.ts` 开头，包含 `-old` 与 `+new`；新文件用例的旧文件为 `/dev/null`。
- `MapToolResult_Failed_MarksError`：标记方式与 Pi 的工具失败结果相同。
- `MapToolResult_EmptyContent_UsesRawOutput`。
- `Complete_CallWithoutResult_EmitsErrorResult`：文本为 `The ACP agent ended the turn before this tool call finished.`。
- `MapPlan_Entries_ProducesTodoSnapshot`：`toolName == "acp_plan"`、`callId == "r1-plan"`、`MessageId == "r1-plan"`，条目 `id` 依次为 `r1:0`、`r1:1`，`isComplete` 只对 `completed` 为 true。
- `CreateResult_LastMessageAfterToolCall_PointsToSource`：`resultSourceMessageId` 指向最后一条正文消息；最后一个工具调用之后没有正文时返回 null。
- `Map_UnknownUpdate_ReturnsEmpty`。

- [x] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*AcpUpdateMapperTests"`，以及 `AcpPromptBuilderTests`。
预期：编译失败。

- [x] **步骤 3：实现**

- diff 使用 `new UnidiffRenderer(new Differ(), 3).Generate(oldText ?? "", newText, $"a/{path}", $"b/{path}")`；`oldText` 为 null 时旧文件名为 `/dev/null`。
- 计划快照：把 `CreateTodoSnapshotMessage` 返回的 `ChatMessage` 的 `Role`、`Contents`、`MessageId`、`AuthorName`、`AdditionalProperties` 复制到 `AgentResponseUpdate`。

- [x] **步骤 4：运行测试，确认通过**

运行步骤 2 的命令。
预期：全部通过。

- [ ] **步骤 5：提交**（用户要求时）

```bash
git add src/server tests/Agw.Agents.Tests
git commit -m "feat(agents): map ACP session updates to agent responses"
```

---

### 任务 8：`AcpAgent`、session 与 Runtime 接入

**文件：**
- 创建：
  - `Agw.Agents.Execution/Agents/ExternalAgents/Acp/AcpLaunchResolver.cs`、`AcpAgent.cs`、`AcpAgentSession.cs`
  - `tests/Agw.Agents.Tests/Acp/AcpAgentHarness.cs`
- 修改：
  - `Agw.Agents.Execution/Agents/Runtime/AgentRuntimeFactory.CreateExternalAgents.cs`（两个 switch 与 `CreateAcpAgent`）
  - `Agw.Agents.Execution/Agents/History/AgentMessageAdapters.cs`、`AgwChatHistoryProvider.cs`
- 测试：`tests/Agw.Agents.Tests/Acp/AcpAgentExecutionTests.cs`、`AcpLaunchResolverTests.cs`

**接口：**
- 消费：
  - 任务 1 的 `AcpAgentSettingsJson.Parse`
  - 任务 3 的 `CreateAiAgentRequest.ProviderSessionId`
  - 任务 5 的 `AcpConnection`、`IAcpClientHandler`
  - 任务 6 的 `IAcpInstalledAgentCatalog`
  - 任务 7 的 `AcpPromptBuilder`、`AcpUpdateMapper`
- 产出：
  - `sealed class AcpLaunchResolver(IAcpInstalledAgentCatalog catalog)`，方法 `AcpLaunchSpec Resolve(AcpAgentSettings settings)`
  - `sealed record AcpAgentRuntimeOptions(Guid AgentId, string AgentName, AcpAgentSettings Settings, string WorkingDirectory, IReadOnlyList<string> AdditionalDirectories, IReadOnlyDictionary<string, string> EnvironmentVariables, string? ProviderSessionId, Func<string, CancellationToken, ValueTask>? OnSessionStartedAsync, AcpAgentOptions Options)`
  - `sealed class AcpAgent : AIAgent, IAsyncDisposable, IAcpClientHandler`
    - 构造函数参数：`AcpAgentRuntimeOptions runtime`、`AcpLaunchResolver launcher`、`AcpPermissionBridge? permissions`、`ILogger logger`
    - `permissions` 在任务 9 接入；本任务中为 null 时，权限请求一律回复 `cancelled`
    - 实现 `RunCoreStreamingAsync`；`RunCoreAsync` 聚合流式结果；session 的创建与序列化方法参照 `A2ARemoteAgent`
  - `sealed class AcpAgentSession : AgentSession`，属性：`string? SessionId`、`AcpSessionModeState? Modes`、`IReadOnlyList<AcpSessionConfigOption>? ConfigOptions`
  - `internal sealed class AcpMessageAdapter : ModelMessageAdapter`，`IsExternalEngine => true`
  - `AcpAgentHarness`：参照 `A2ARemoteAgentHarness`
    - `static Task<AcpAgentHarness> CreateAsync(string extra, IReadOnlyDictionary<string, string>? env = null, string? postgres = null, Action<Dictionary<string, string?>>? configure = null)`
    - 另有 `StartTurnAsync`、`RunTurnAsync`、`WaitIdleAsync`、`Interrupt`、`RebuildCoordinatorAsync`、`ClearConversationAsync`、`ReadMessages`、`ReadTurnAsync`、`ReadHistoryAsync`、`ReadProviderSessionIdAsync`、`WaitForInteractionAsync(Guid turnId)`、`SubmitApprovalAsync(string interactionId, bool approved)`
    - `SubmitApprovalAsync` 调用 `InProcessTurnHost.TrySubmitHumanResponseAsync(new ToolApprovalDecision { InteractionId = ..., Approved = ... })`

- [x] **步骤 1：编写失败的测试**

`AcpLaunchResolverTests`：
- `Resolve_Command_UsesArgs`：`Source == "command"`。
- `Resolve_NodeRuntime_StartsNodeWithEntrypoint`：`FileName == "node"`，`Arguments[0]` 为入口文件的绝对路径，之后是清单的 `args`，`Environment` 为清单的 `env`。
- `Resolve_NativeRuntime_UsesAbsoluteExecutable`。

`AcpAgentExecutionTests`（`AcpAgentHarness` 与测试 agent）：
- `RunTurn_CustomCommand_StreamsMessageAndResult`：
  - Extra 为 `{"command":"node","args":["<script>","<scenario>","<log>"]}`，测试 agent 输出两段正文。
  - 历史中是一条 Assistant 消息，文本为两段之和。
  - Result 的 `resultSourceMessageId` 指向它，Turn 为 `completed`。
  - 记录文件的 `cwd` 为 `WorkspacePath`。
- `RunTurn_InstalledRegistryAgent_Launches`：
  - 先用任务 6 的安装器安装测试 agent 的 `npx` 条目，Extra 为 `{"registry":{"agentId":"test-agent"}}`。
  - 记录中的 `argv` 以 `node` 与入口文件开头。
- `RunTurn_RegistryAgentNotInstalled_ReturnsNotInstalled`：`turn-finished` 的 `errorCode == "5020014"`。
- `RunTurn_Thought_PersistsReasoning`。
- `RunTurn_ToolCallWithDiff_PersistsCallAndResult`：历史中有调用与结果，调用内容的 `toolTitle` 存在，结果包含 `+++ b/`。
- `RunTurn_Plan_EmitsTodoSnapshot`：两次 `plan` 更新后，历史中只有一条 `type == "tool-todo-snapshot"` 的消息，为第二次的内容。
- `RunTurn_ModeAndConfigOptions_AppliedAfterSession`：Extra 带 `modeId: "code"` 与 `configOptions: {"model":"b"}`；记录中依次是 `session/new`、`session/set_mode`、`session/set_config_option`、`session/prompt`。
- `RunTurn_UnknownConfigOption_ReturnsInvalidParam`：消息包含 `model`。
- `RunTurn_AuthRequired_ReturnsAuthCodeWithMethods`：`requireAuth: true`，`errorCode == "5020016"`，消息包含 `api-key`。
- `RunTurn_AuthMethodConfigured_CallsAuthenticate`：记录中 `authenticate` 在 `session/new` 之前。
- `RunTurn_TerminalAuthMethod_ReturnsInvalidParam`：`authMethodId` 指向 `type: "terminal"` 的方式，消息列出可用的方式。
- `RunTurn_ModeWithoutModes_ReturnsInvalidParam`：消息为 `This ACP agent does not report session modes.`。
- `RunTurn_AdditionalDirectories_SentWhenSupported`：
  - Project 有一个附加目录。
  - 声明 `additionalDirectories` 时，`session/new` 带上该目录。
  - 不声明时，`session/new` 没有这个字段。
- `RunTurn_Diagnostics_RecordTurnAndProcessStart`：用 `MeterListener` 收集指标。
  - `agw.acp.client.turns` 的 `outcome` 为 `completed`。
  - `agw.acp.client.process_starts` 的 `source` 为 `command`。
  - `ActivityListener` 收到 `acp.client.start`、`acp.client.session`、`acp.client.turn`。
- `RunTurn_RefusalStopReason_ReturnsTurnStopped`：`errorCode == "5020018"`，消息为 `The ACP agent stopped the turn: refusal.`。
- `RunTurn_EnvironmentVariables_ReachProcess`：Agent 的 `K=v` 出现在 `envNames` 中；测试进程中设置的 `AGW_ACP_TEST_SECRET` 没有出现。
- `RunTurn_UpdateForOtherSession_Ignored`（复核重点 3）：场景用 `updateForSession: "other"` 发正文，历史中没有它。
- `RunTurn_AgentflowNode_CreatesSessionPerInvocation`：同一节点调用两次，记录中有两次 `session/new`，`project_conversation_binding` 中没有该 Agent 的行。
- `RunTurn_NonStreamingNode_AggregatesResult`。

- [x] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*AcpAgentExecutionTests"`，以及 `AcpLaunchResolverTests`。
预期：编译失败。

- [x] **步骤 3：实现**

- `AcpAgent` 每次运行按设计第 6 节执行：
  1. 确保连接：连接不存在或已故障时，调用 `AcpConnection.StartAsync`，并按设计 4.4 处理 `authMethodId`。
  2. 确保 session：按设计 5.1、5.2、5.4。
  3. 创建本次 prompt 的 `Channel<object>`（有序队列），`OnSessionUpdate` 只写入当前队列，并丢弃 `sessionId` 不同的更新。
  4. `InvokeOrderedAsync<AcpPromptResponse>("session/prompt", ...)`。
  5. 逐个输出 `AcpUpdateMapper` 的结果。
  6. 按 `stopReason` 处理。
- `CreateAcpAgent`：
  - 用 `_services` 取得 `IAcpInstalledAgentCatalog` 与 `IOptions<AcpAgentOptions>`。
  - 返回 `ResourceOwningAIAgent(DecorateExternalAgent(acpAgent, historyProvider, isBackground, createMemoryContextAsync), acpAgent)`。
  - 第二个 switch 增加 `EngineKind.ACP => aiAgent`。
- `AgwChatHistoryProvider.Create` 为 `EngineKind.ACP` 返回 `AcpMessageAdapter`。

- [x] **步骤 4：运行测试，确认通过**

运行步骤 2 的命令。
预期：全部通过。

- [ ] **步骤 5：提交**（用户要求时）

```bash
git add src/server tests/Agw.Agents.Tests
git commit -m "feat(agents): run turns on ACP agents"
```

---

### 任务 9：权限请求

**文件：**
- 创建：
  - `Agw.Agents.Execution/Agents/ExternalAgents/Acp/AcpPermissionBridge.cs`
  - `Agw.Agents.Execution/Agents/ExternalAgents/ExternalToolApprovalCache.cs`（由 `ClaudeCode/ClaudeToolApprovalCache.cs` 移动，类名与命名空间改变，内容不变）
- 删除：`Agw.Agents.Execution/Agents/ExternalAgents/ClaudeCode/ClaudeToolApprovalCache.cs`
- 修改：
  - `AgentRuntimeFactory.cs`（字段 `_claudeApprovals` 的类型）
  - `ClaudeCodeAskUserQuestionBridge.cs`（参数类型）
  - `AgentRuntimeFactory.CreateExternalAgents.cs`（`CreateAcpAgent` 中创建 bridge，并用 `.AsBuilder().Use(runFunc: bridge.BindRunAsync, runStreamingFunc: bridge.BindRunStreamingAsync)` 包住 `AcpAgent`）
  - `AcpAgent.cs`
- 测试：
  - 创建：`tests/Agw.Agents.Tests/Acp/AcpPermissionTests.cs`
  - 修改：`tests/Agw.Agents.Tests/ClaudeCodeAskUserQuestionBridgeTests.cs`（第 256、300 行的类型名）

**接口：**
- 消费：任务 7 的 `AcpUpdateMapper.EnsureToolCall` 与 `GetToolCall`；任务 8 的 `AcpAgentHarness.WaitForInteractionAsync` 与 `SubmitApprovalAsync`。
- 产出：
  - `sealed class AcpPermissionBridge`
    - 构造函数参数：`HumanInteractionContextAccessor? contextAccessor`、`bool allowInteraction`、`AgwPermissionMode? permissionMode`、`string workingDirectory`、`Guid agentId`、`IAgentExecutionContextAccessor? executionContext`、`ExternalToolApprovalCache cache`、`ILogger logger`
    - 方法：`BindRunAsync`、`BindRunStreamingAsync`（签名与 `ClaudeCodeAskUserQuestionBridge` 相同）
    - `ValueTask<AcpPermissionDecision> DecideAsync(string toolName, string? title, JsonElement? rawInput, string toolCallId, CancellationToken ct)`
  - `enum AcpPermissionDecision { Allowed, Rejected, Cancelled }`
  - `static AcpRequestPermissionResponse AcpPermissionOptions.ToResponse(IReadOnlyList<AcpPermissionOption> options, AcpPermissionDecision decision)`

- [x] **步骤 1：编写失败的测试**

`AcpPermissionTests`：
- `SelectOption_ApprovedWithoutAllowOnce_UsesAllowAlways`：选项只有 `allow_always` 与 `reject_once`，`Allowed` 选中 `allow_always`。
- `SelectOption_NoMatchingKind_ReturnsCancelled`：只有 `allow_once` 时，`Rejected` 得到 `{"outcome":{"outcome":"cancelled"}}`。
- `RunTurn_PermissionAlwaysAsk_ApprovedSelectsAllowOnce`：
  - 交互请求为 `tool-approval`，`Prompt` 为 `Allow the ACP agent to run "Edit src/x.ts"?`，`Source.CallId == "t1"`。
  - 批准后，记录中的 `outcome.optionId` 为 `allow_once` 选项的 id。
- `RunTurn_PermissionRejected_SelectsRejectOnce`。
- `RunTurn_PermissionAllowSameArguments_SecondCallSkipsPrompt`：第二次参数相同的请求没有产生交互请求。
- `RunTurn_PermissionFullAccess_AllowsWithoutInteraction`。
- `RunTurn_BackgroundAgentPermission_Rejects`：System Agent 的 `background-agents` 调用 ACP Agent。
- `RunTurn_PermissionForUnannouncedTool_EmitsCallWithTitle`（复核重点 4）。
- `RunTurn_PermissionPendingWhenInterrupted_RepliesCancelled`：等待审批时中断，记录中 `outcome == "cancelled"`。

- [x] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*AcpPermissionTests"`
预期：编译失败。

- [x] **步骤 3：实现**

- 决定规则按设计第 7 节的表格，缓存的读写方式与 `ClaudeCodeAskUserQuestionBridge.HandleToolApprovalAsync` 相同。
- `AcpAgent.OnRequestPermissionAsync` 的处理顺序：
  1. 先在有序上下文中调用 `EnsureToolCall`，把结果写入当前队列。
  2. 再调用 `DecideAsync`。
  3. 用 `ToResponse` 回复。

- [x] **步骤 4：运行测试，确认通过**

运行：
- `dotnet test --project tests/Agw.Agents.Tests --filter-class "*AcpPermissionTests"`
- `dotnet test --project tests/Agw.Agents.Tests --filter-class "*ClaudeCodeAskUserQuestionBridgeTests"`

预期：全部通过。

- [ ] **步骤 5：提交**（用户要求时）

```bash
git add src/server tests/Agw.Agents.Tests
git commit -m "feat(agents): route ACP permission requests through tool approval"
```

---

### 任务 10：session 延续、中断、进程故障与 Distributed 执行

**文件：**
- 修改：`Agw.Agents.Execution/Agents/ExternalAgents/Acp/AcpAgent.cs`
- 测试：`tests/Agw.Agents.Tests/Acp/AcpAgentSessionTests.cs`、`AcpAgentInterruptTests.cs`、`AcpAgentDurableTests.cs`

**接口：**
- 消费：任务 8 的 `AcpAgent` 与 `AcpAgentHarness`（`postgres` 参数）；任务 2 的 Distributed 判断。
- 产出：设计 5.2、第 8 节与第 9 节的行为，没有新的公开类型。

- [x] **步骤 1：编写失败的测试**

`AcpAgentSessionTests`：
- `RunTurn_SecondTurn_ReusesSessionAndProcess`：两次 `start` 事件中只有一个 PID，两次 `session/prompt` 的 `sessionId` 相同。
- `RunTurn_AfterRuntimeRebuild_ResumesSession`：`capabilities.resume`，调用 `RebuildCoordinatorAsync` 后记录中出现 `session/resume`，其 `sessionId` 等于 `ReadProviderSessionIdAsync()`。
- `RunTurn_LoadSessionReplay_IgnoresReplayedUpdates`：只有 `loadSession`，`replay` 含一段正文 `old`；历史中没有 `old`。
- `RunTurn_ResumeNotFound_CreatesNewSession`：Runtime 重建前删除 `.state.json`；恢复返回 `-32002` 后出现 `session/new`，provider session 绑定更新为新 id。
- `RunTurn_ResumeOtherError_FailsWithCode`：`errorCode == "5020017"`，消息包含 JSON-RPC 错误码。
- `RunTurn_AfterConversationCleared_StartsNewSession`。
- `RunTurn_SessionIdTooLong_FailsWithRequestFailed`：`session/new` 返回 201 个字符的 `sessionId`，`errorCode == "5020017"`。
- `DisposeAsync_CloseCapability_SendsSessionClose`：声明 `close` 时，释放 Runtime 后记录中有 `session/close`，进程已退出。

`AcpAgentInterruptTests`：
- `RunTurn_Interrupted_SendsCancelAndKeepsProcess`：
  - 场景先输出正文 `before`，等待 2 秒，收到 `session/cancel` 后再发正文 `after`，然后以 `cancelled` 结束。
  - Turn 为 `interrupted`，历史中有 `before`、没有 `after`。
  - 下一个 Turn 的 PID 不变。
- `RunTurn_InterruptIgnored_KillsProcessAfterGrace`：`ignoreCancel: true`，`CancelGracePeriod = 1s`；Turn 为 `interrupted`，下一个 Turn 有新的 `start` 事件和 `session/resume`。
- `RunTurn_ProcessExitsMidTurn_ReturnsRequestFailedWithStderr`：场景步骤 `{ "exit": 7, "stderr": "fatal" }`，`errorCode == "5020017"`，消息包含 `7` 与 `fatal`；下一个 Turn 成功。
- `RunTurn_NonJsonStdout_FailsThenRecovers`（复核重点 1）。

`AcpAgentDurableTests`（需要 `AGW_TEST_POSTGRES_CONNECTION_STRING`，没有时跳过并写明原因）：
- `RunTurn_DistributedAcpAgent_CompletesInOneSegment`：
  - 以 `FullAccess` 运行两个 Turn。
  - 每个 segment 结束后测试 agent 进程已退出，两次 `start` 事件的 PID 不同。
  - 第二个 Turn 用 `session/resume` 恢复同一个 `sessionId`。
- `RunTurn_DistributedAlwaysAsk_ReturnsInvalidParam`。
- `RunTurn_DistributedCodexAgent_StillRejected`：消息为 `Distributed execution supports System Agents, A2A Agents and ACP Agents.`。

- [x] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*AcpAgentSessionTests"`，以及 `AcpAgentInterruptTests`。
预期：失败（中断、故障与恢复的行为尚未实现）。

- [x] **步骤 3：实现**

- 中断按设计第 8 节：
  - 取消信号到达后不再输出。
  - 用 `CancellationToken.None` 发送 `session/cancel`，对等待中的权限请求回复 `cancelled`。
  - 在 `CancelGracePeriod` 内丢弃队列中的更新，直到处理到响应标记。
  - 超时后调用 `KillAsync`。
  - 最后抛出 `OperationCanceledException`。
- 连接故障后的下一次运行，`AcpAgentSession.SessionId` 作为已知 id 走设计 5.2 的第 3 步。
- `session/load` 进行期间设置标记，忽略位于响应标记之前的更新。

- [x] **步骤 4：运行测试，确认通过**

运行步骤 2 的命令。向用户确认后，运行 `dotnet test --project tests/Agw.Agents.Tests --filter-class "*AcpAgentDurableTests"`。
预期：全部通过。

- [ ] **步骤 5：提交**（用户要求时）

```bash
git add src/server tests/Agw.Agents.Tests
git commit -m "feat(agents): resume ACP sessions, handle interrupts, and support distributed turns"
```

---

### 任务 11：客户端

**文件：**
- 创建（目录 `src/clients/packages/agents/src/ui-web/pages/agents/components/`）：`acp-settings.ts`、`acp-settings.test.ts`、`acp-registry-picker.tsx`、`acp-registry-picker.test.ts`
- 修改：
  - 同一目录：`types.ts`、`agent-form-fields.tsx`、`agent-form-fields.test.ts`、`create-agent-dialog.tsx`、`edit-agent-dialog.tsx`、`agents-table.tsx`、`agents-table.test.ts`、`agent-form-summary.test.ts`
  - `src/clients/packages/agents/src/ui-web/pages/agents/page.tsx`、`page.test.ts`
  - `src/clients/packages/agents/src/types/agentflow.ts`
  - `src/clients/packages/chat/src/ui-web/pages/chat/components/provider-sessions-section.tsx`
  - `src/clients/packages/chat-core/src/conversation-render-model.ts`、`conversation-render-model.test.ts`
  - `src/clients/packages/extension-sdk/src/api.ts`
  - `src/clients/desktop/renderer/src/runtime/extensions/extension-bridge-broker.ts`、`context-service.test.ts`
  - `src/clients/packages/api/openapi.json`、`src/clients/packages/api/src/openapi.d.ts`

**接口：**
- 消费：任务 4 的 `GET /api/agents/acp-registry`（`AcpRegistryResponse`）。
- 产出：
  - `export function getAcpSettingsError(extra: string): string | null`
  - `export function withAcpRegistryAgent(extra: string, agentId: string): string`
  - `export function readAcpRegistryAgentId(extra: string): string | null`
  - `export function isAcpAgent(agentType: number, kind: number): boolean`
  - `AcpRegistryPicker` 组件，props 为 `{ extra: string; setExtra(value: string): void; idPrefix?: string }`

- [x] **步骤 1：编写失败的测试**

- `acp-settings.test.ts`：
  - `getAcpSettingsError("{}")` 返回 `Select an agent from the ACP registry or set command.`。
  - `{"registry":{"agentId":"a"},"command":"b"}` 返回错误。
  - `withAcpRegistryAgent('{"command":"x","args":["y"],"registry":{"agentId":"a","version":"1.0.0"},"modeId":"m"}', "goose")` 得到 `{"registry":{"agentId":"goose"},"modeId":"m"}`。
- `acp-registry-picker.test.ts`：请求失败时显示错误消息，输入框仍可编辑；Extra 无法解析时选择器不可用。
- `agent-form-fields.test.ts`：
  - 选择 ACP 后，Model provider 与 Response schema 不显示，Environment variables 与 Extra Settings 显示。
  - Extra 为 `{}` 时创建按钮不可用。
- `agents-table.test.ts`：kind 5 显示 `External · ACP`。
- `agent-form-summary.test.ts`：遍历的 kind 包含 ACP。
- `conversation-render-model.test.ts`：工具调用内容带 `toolTitle: "Read src/x.ts"`、参数带 `path: "src/x.ts"` 时，摘要为 `Read src/x.ts`。
- `context-service.test.ts`：5 映射为 `"acp"`。
- `page.test.ts`：路由表增加 `GET /api/agents/acp-registry`。

- [x] **步骤 2：运行测试，确认失败**

运行（在 `src` 下）：
- `pnpm --filter @agw/agents exec tsx --test src/ui-web/pages/agents/components/acp-settings.test.ts`
- `pnpm --filter @agw/chat-core exec tsx --test src/conversation-render-model.test.ts`

预期：失败。

- [x] **步骤 3：实现**

- 按设计第 12 节实现。
- 按全局约束更新 `openapi.json` 与 `openapi.d.ts`。

- [x] **步骤 4：运行测试，确认通过**

运行（在 `src` 下）：`pnpm test:boundaries`、`pnpm typecheck`、`pnpm test`、`pnpm fmt:check`。
预期：全部通过。

`pnpm test:boundaries`、`pnpm typecheck`、`pnpm test --concurrency=1` 与修改文件的格式检查已通过。当前 checkout 将原有文件转换为 CRLF。使用同一份代码的 LF 副本执行全量 `pnpm fmt:check`，37 个包通过。

- [ ] **步骤 5：提交**（用户要求时）

```bash
git add src/clients
git commit -m "feat(agents): support ACP agents in the agent form"
```

---

### 任务 12：文档、回归与验收

**文件：**
- 修改：
  - `docs/2.Architecture.md`：`Agent.ExternalAgentKind` 的种类列表，以及 Agents 模块对 ACP 的说明
  - `docs/4.Deployment.md`：设计第 13 节
  - `docs/1.Development.md`：ACP 测试需要 `node` 与 `npm`。Python 安装测试需要 `uv` 与本地 CPython 镜像。
  - `src/server/Agw.Agents.Execution/README.md`：在 "A2A Agent" 一节之后增加 "ACP Agent" 一节

**接口：** 无。

- [x] **步骤 1：更新文档**，内容取自设计第 9、11、13 节。

- [x] **步骤 2：格式化与全量回归**

运行：
- `dotnet csharpier format <本功能修改过的 .cs 文件>`
- `dotnet build Agw.slnx`
- `dotnet test Agw.slnx`
- 在 `src` 下运行 `pnpm test`

预期：全部通过；构建没有新的警告。

CSharpier 检查了 107 个修改或新增的 C# 文件。解决方案构建通过。使用本机 Redis 的后端全量测试有 4,902 项通过、57 项按条件跳过，一项 TaskPilot 用例单独复测通过。只允许回环连接时，三项 `uvx` 用例通过。ACP、A2A 与执行审批专项测试有 6 项通过，其中 5 项使用独立 PostgreSQL。客户端 `pnpm test` 的 64 个任务和 `pnpm fmt:check` 的 37 个任务通过。

- [ ] **步骤 3：本机 Standalone 验收**

用 `--environment=Production` 启动独立的 Standalone 实例，避免中断开发 Host。

1. 安装：执行 `agw-server acp install codex-acp claude-acp goose`。
2. 创建 Agent：
   - 在 Web 表单的 registry 选择器中为这三个 agent 各创建一个 ACP Agent。
   - 再创建一个 Extra 为 `{"command":"opencode","args":["acp"]}` 的 ACP Agent。
3. 在 Chat 中各运行一次包含读文件、改文件、执行命令的 Turn，逐项验证：
   - `AlwaysAsk` 的审批
   - `AllowSameArguments` 的缓存
   - `FullAccess` 下不审批
   - 中断
   - 刷新页面后继续对话
   - Server 重启后继续对话
   - `toolTitle` 与 diff 结果的显示

独立 Production Standalone 已安装 `codex-acp 2.11.0`、`claude-acp 0.110.0` 和 `goose 1.60.2`。Web 表单通过 registry 选择器创建了前三种 Agent，并通过 `command` 配置创建了 OpenCode。Claude 的 Turn 中断通过。OpenCode 完成了读文件、修改文件和执行命令，Web Chat 刷新后仍展示 `toolTitle` 和 unified diff。OpenCode 实测通过 `AlwaysAsk`、`FullAccess` 和同一连接中的 `AllowSameArguments`。SignalR 重新连接与 Standalone 重启后，OpenCode 都恢复了同一个 ACP session。Codex 和 Claude 的完整 Turn 需要供应商认证，Goose 需要配置模型供应商。

- [x] **步骤 4：podman Distributed 验收**

1. 按设计第 13 节构建派生 Data Plane 镜像。
2. 运行两个 Data Plane 与一个 Control Plane，agent 的会话目录放在共享卷上。
3. 连续运行多个 Turn，从日志确认它们由不同容器执行，并且继续同一个 ACP session。

运行之前向用户确认。

经用户授权，使用当前代码的 Linux ARM64 发布产物构建 Control Plane 和 Data Plane 镜像。派生 Data Plane 镜像通过 `acp install` 安装官方 `goose 1.60.2`。验收使用 PostgreSQL、Redis 和共享卷。ACP SDK 测试 agent 的两个 Turn 由不同容器完成，第二个容器通过 `session/resume` 延续同一个 session。管理接口通过 HTTP 验证，执行和恢复通过 Data Plane 的 SignalR 验证。

- [ ] **步骤 5：提交**（用户要求时）

```bash
git add docs src/server/Agw.Agents.Execution/README.md
git commit -m "docs: describe ACP agents"
```
