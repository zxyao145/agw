# A2A 外部 Agent 接入：实施计划

> **给执行计划的 agent：** 必须使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 按任务逐个实施。步骤使用 checkbox（`- [ ]`）记录进度。

**目标：** agw 的 Agent 定义支持远端 A2A agent：保存它的 card 地址与按 origin 管理的请求凭据，并把它作为 External Agent 的第四种 Engine 执行 Turn。

**架构：** `EngineKind.A2A` 走现有的 `AgentRuntimeFactory.CreateExternalAgentAsync` 入口。连接配置保存在 Agent 聚合的子记录 `agent_a2a_endpoint` 中。`A2ARemoteAgent : AIAgent` 用 `A2A` 包的 `IA2AClient` 发送请求，把远端事件转换为 `AgentResponseUpdate`，会话状态 `A2ARemoteAgentSession` 由 `AgentSessionStateStore` 持久化。

**技术栈：** .NET 10、ASP.NET Core、EF Core（SQLite、PostgreSQL）、Microsoft.Agents.AI 1.21.0、`A2A` 与 `A2A.AspNetCore` 1.0.0-preview2、xUnit v3、Next.js、TypeScript、Node test runner（`tsx --test`）。

**设计文档：** `docs/designs/2026-11-21-A2A外部Agent接入.md`。执行每个任务之前阅读设计文档的对应章节。

## 全局约束

- 没有兼容性要求，代码中不保留过渡字段与过渡行为。
- NuGet：只使用 `src/server/Directory.Packages.props` 中已有的 `A2A` 1.0.0-preview2。`Agw.Agents.csproj` 与 `Agw.Agents.Execution.csproj` 各增加 `<PackageReference Include="A2A" />`，不增加其他包，项目之间的引用不变。测试项目经 `Agw.Host` 的引用得到 `A2A.AspNetCore`。
- protocol binding 的取值使用 `A2A.ProtocolBindingNames.HttpJson`（`HTTP+JSON`）与 `ProtocolBindingNames.JsonRpc`（`JSONRPC`）。
- 错误码：
  - `A2AAgentCardFetchFailed = new(502_0008, "Failed to fetch the A2A agent card.", HttpStatusCode.BadGateway)`
  - `A2AAgentCardInvalid = new(502_0009, "The A2A agent card is invalid or declares no supported interface.", HttpStatusCode.BadGateway)`
  - `A2ARemoteAgentRequestFailed = new(502_0010, "The remote A2A agent request failed.", HttpStatusCode.BadGateway)`
  - `A2ARemoteAgentUnauthorized = new(502_0011, "The remote A2A agent rejected the configured credentials.", HttpStatusCode.BadGateway)`
  - `A2ARemoteTaskFailed = new(502_0012, "The remote A2A task did not complete.", HttpStatusCode.BadGateway)`
  - `A2ARemoteAgentTimeout = new(504_0001, "The remote A2A agent did not respond in time.", HttpStatusCode.GatewayTimeout)`
- 接口：JSON 接口返回 Bens.Results（`ApiResult.Ok(...)`），新增路由的参数放在 body 中，不使用路径参数。预期失败抛出 `AgwException(ErrorCodes.X)`。
- 当前用户通过 `UserInfoUtil.RequiredUserId` 或 `IUserInfoService` 读取，不增加 `userId` 参数。
- 凭据按规范 origin 保存，headers 的值不写入日志、Activity 标签、异常消息与接口响应；共享 HTTP handler 禁用 CookieContainer。用户明确配置的 `Cookie` header 仍按 origin 规则发送。
- 测试：xUnit v3，命名为 `Method_Condition_ExpectedResult`，使用真正运行的实现、SQLite 与 PostgreSQL，不使用 mock 或替身。远端 agent 统一使用任务 4 的 `A2ATestAgentServer`，它是测试进程内按 A2A 协议运行的 server。测试运行时产生的文件保存在测试项目的输出目录或 `.scratch/` 下。
- 注释使用中文和英文两种语言，数量保持克制。时间使用 `DateTimeOffset` 和 `TimeProvider`。出站 transport 由 `IHttpClientFactory` 管理；需要 endpoint 级请求策略时，使用 `IHttpMessageHandlerFactory.CreateHandler` 的 pooled transport 与独立外层 handler 组合，由 A2A 专用 client factory 提供包装后的 `HttpClient`。
- 格式化：C# 使用 `dotnet csharpier format <修改过的文件>`；客户端在 `src/clients` 下使用 `pnpm fmt`。
- 修改 entity、持久化访问或 Behavior 之后运行 `dotnet test --project tests/Agw.Architecture.Tests`；修改 `ErrorCodes` 之后运行 `dotnet test --project tests/Agw.Shared.Tests`。
- `tests/Agw.Projects.Tests/Infrastructure/InitialMigrationTests.cs` 检查 `HasPendingModelChanges`。任务 2 在同一个任务内修改 entity 并生成 migration，任务结束时它保持通过。
- 过滤参数在本机无效时，先执行 `dotnet build tests/<项目>`，再运行 `dotnet tests/<项目>/bin/Debug/net10.0/<项目>.dll -class "<完整类名>"`。
- migration 只在任务 2 生成，命令见 `docs/1.Development.md`。执行生成命令之前向用户确认。
- 后端接口变化后更新客户端类型：用 `--no-launch-profile --environment Development`、显式 SQLite 连接（`.scratch/a2a-openapi/agw.db`）、`--AgwDataDir=.scratch/a2a-openapi/data` 和空闲端口启动 `src/server/Agw.Standalone.Host`，下载 `/openapi/v1.json` 到 `src/clients/packages/api/openapi.json`，再在 `src/clients` 下运行 `pnpm gen:api`。
- 每个任务最后的提交步骤只在用户要求提交时执行，提交信息使用 Conventional Commits。不使用 Git 回退代码。
- 创建子代理时，提示词要求它使用 Rider MCP 工具查找和阅读代码。
- 不修改 `docs/human/` 下的文档。

## 复核重点

下面六种输入或状态最容易在使用中出错，每一项的测试已经加入对应任务。

1. 凭据只发往配置的规范 origin；card 声明新的 interface origin 后，旧 origin 的 Authorization 不会发给新地址。测试在任务 3、4、5、7。
2. 更新或预览时在另一个 origin 沿用 header 值：返回 `InvalidParam`，新地址没有收到任何请求。测试在任务 3 与任务 5。
3. interface URL 或 binding 改变后，session 的旧 `contextId` 与 `taskId` 不发送给新 endpoint；`TaskNotFound` 后 context 与 task 都清空，下一轮不发送旧 ID。测试在任务 6 与任务 7。
4. card 地址返回 200 但内容是 HTML，或 card 的 `supportedInterfaces` 为空：返回 `5020009`，没有保存 Agent。测试在任务 4 与任务 5。
5. 流式请求返回 200 后没有任何事件就结束：Turn 以 `5020010` 失败，没有输出 Result。测试在任务 7。
6. 不支持流式的远端在 `SendMessage` 的响应中直接返回 `completed` 的 task：不调用 `GetTask`，直接输出 artifacts 与 Result。测试在任务 8。

## 文件结构

| 位置 | 内容 |
| --- | --- |
| `src/server/Agw.Shared/Net/PrivateNetworkAddresses.cs` | 私有网络地址判断，Events 与 Agents 共用 |
| `src/server/Agw.Data/Entities/Agents/AgentA2AEndpoint.cs`、`AgentA2AEndpointConfiguration.cs`、`AgentA2AEndpointExtra.cs` | endpoint 的 entity、EF 配置与 `extra` 的 record |
| `src/server/Agw.Data/Conversions/ExtraJson.cs` | `extra` 的 camelCase 转换器 |
| `src/server/Agw.Agents/Definitions/Domain/Rules/A2AEndpointRules.cs`、`A2AEndpointCredentialsJson.cs` | `cardUrl`、规范 origin 与 credentials 校验、凭据转换、interface 选定 |
| `src/server/Agw.Agents/ExternalAgents/A2A/A2ARemoteAgentOptions.cs`、`A2AClientDiagnostics.cs` | 配置项、`ActivitySource` 与 `Meter`，定义管理与执行共用 |
| `src/server/Agw.Agents/Application/A2A/IA2ARemoteHttpClientFactory.cs`、`IA2AAgentCardClient.cs` | 出站 HTTP 与 card 获取的接口 |
| `src/server/Agw.Agents/Infrastructure/A2A/A2ARemoteHttpClientFactory.cs`、`A2AOriginCredentialHandler.cs`、`A2AAgentCardClient.cs` | 出站 HTTP、origin 凭据校验与 card 获取 |
| `src/server/Agw.Agents.Execution/Agents/ExternalAgents/A2A/A2ARemoteAgent.cs`、`A2ARemoteAgentSession.cs` | 执行适配器与会话状态 |
| `src/server/Agw.Migrations.Sqlite`、`Agw.Migrations.Postgres` | `AddAgentA2AEndpoint` migration |
| `tests/Agw.Agents.Tests/A2A/` | `A2ATestAgentServer`、`A2ARemoteAgentHarness` 与本功能的全部 Server 测试 |
| `src/clients/packages/agents/src/ui-web/pages/agents/components/a2a-endpoint-fields.tsx`、`a2a-endpoint.ts` | endpoint 表单与校验 |

---

### 任务 1：Engine 种类、能力、错误码与私有地址判断

**文件：**
- 创建：`src/server/Agw.Shared/Net/PrivateNetworkAddresses.cs`
- 修改：`src/server/Agw.Agents.Contracts/Execution/EngineKind.cs`、`src/server/Agw.Agents.Execution/Agents/ExternalAgents/EngineKinds.cs`、`src/server/Agw.Agents/ExternalAgents/ExternalAgentDefaults.cs`、`src/server/Agw.Agents/Definitions/Agents/ExecutionPermissionService.cs`、`src/server/Agw.Agents/Definitions/Contracts/ExecutionInputCapabilities.cs`、`src/server/Agw.Shared/Exceptions/ErrorCodes.cs`、`src/server/Agw.Events/Infrastructure/Sources/EventHttpPoller.cs`
- 测试：`tests/Agw.Shared.Tests/Net/PrivateNetworkAddressesTests.cs`、`tests/Agw.Agents.Tests/A2A/A2AEngineCapabilityTests.cs`、`tests/Agw.Agents.Tests/ExternalAgentDefaultsTests.cs`

**接口：**
- 产出：
  - `EngineKind.A2A = 4`
  - `EngineKinds.Resolve(AgentType.External, EngineKind.A2A)` 返回 `EngineKind.A2A`
  - `ExternalAgentDefaults`：`SupportedKinds` 包含 `A2A`；`GetDisplayName(A2A)` 为 `"A2A"`；`GetDefaultExtra(A2A)` 为 `"{}"`；`GetSupportedProviderTypes(A2A)` 为空
  - `ExecutionPermissionService.ForAgent`：A2A Agent 只有 `AgwPermissionMode.FullAccess`，`SupportsNoTools` 为 false，说明文字为 `Remote A2A agents run their own tools; Agw permission modes do not apply.`
  - `ExecutionInputCapabilities.ForEngine(EngineKind.A2A, provider)`：两种 provider 下都是不支持，说明文字为 `A2A agents do not accept input while a turn is running.`
  - `public static bool PrivateNetworkAddresses.IsPrivate(IPAddress address)`
  - 全局约束中的六个错误码

- [ ] **步骤 1：编写失败的测试**

```csharp
[Theory]
[InlineData("127.0.0.1", true)]
[InlineData("10.1.2.3", true)]
[InlineData("172.16.0.1", true)]
[InlineData("172.32.0.1", false)]
[InlineData("192.168.1.1", true)]
[InlineData("169.254.169.254", true)]
[InlineData("100.64.0.1", true)]
[InlineData("224.0.0.1", true)]
[InlineData("8.8.8.8", false)]
[InlineData("::1", true)]
[InlineData("fe80::1", true)]
[InlineData("fd00::1", true)]
[InlineData("::ffff:10.0.0.1", true)]
[InlineData("2001:4860:4860::8888", false)]
public void IsPrivate_KnownRanges_MatchesExpected(string address, bool expected) =>
    Assert.Equal(expected, PrivateNetworkAddresses.IsPrivate(IPAddress.Parse(address)));
```

`A2AEngineCapabilityTests`：
- `Resolve_ExternalA2A_ReturnsA2A`。
- `ForAgent_A2A_AllowsFullAccessOnly`：断言模式只有 `FullAccess`、`SupportsNoTools` 为 false、说明文字与上面一致。
- `ForEngine_A2ADistributed_ReportsA2AReason` 与 `ForEngine_A2AInProcess_ReportsA2AReason`：断言不支持，说明文字与上面一致。

`ExternalAgentDefaultsTests` 增加 `GetDefaultExtra_A2A_ReturnsEmptyObject` 与 `GetSupportedProviderTypes_A2A_ReturnsEmpty`。

- [ ] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Shared.Tests --filter-class "*PrivateNetworkAddressesTests"`
预期：编译失败，`PrivateNetworkAddresses` 不存在。

- [ ] **步骤 3：实现**

- `PrivateNetworkAddresses.IsPrivate` 的内容来自 `EventHttpPoller.IsPrivateAddress`，`EventHttpPoller` 删除自己的方法并改为调用它。
- `ExecutionInputCapabilities.ForEngine` 中 `EngineKind.A2A` 的分支放在 Distributed 的判断之前。

- [ ] **步骤 4：运行测试，确认通过**

运行：`dotnet test --project tests/Agw.Shared.Tests`、`dotnet test --project tests/Agw.Agents.Tests --filter-class "*A2AEngineCapabilityTests" --filter-class "*ExternalAgentDefaultsTests" --filter-class "*ExecutionPermissionServiceTests"`、`dotnet test --project tests/Agw.Events.Tests --filter-class "*EventHttpPollerTests"`
预期：全部通过。

- [ ] **步骤 5：提交**

```bash
git add src/server/Agw.Shared src/server/Agw.Agents.Contracts src/server/Agw.Agents src/server/Agw.Agents.Execution src/server/Agw.Events tests/Agw.Shared.Tests tests/Agw.Agents.Tests
git commit -m "feat(agents): add A2A engine kind, capabilities and error codes"
```

---

### 任务 2：数据模型与 migration

**文件：**
- 创建：`src/server/Agw.Data/Entities/Agents/AgentA2AEndpoint.cs`、`AgentA2AEndpointConfiguration.cs`、`AgentA2AEndpointExtra.cs`
- 创建：`src/server/Agw.Data/Conversions/ExtraJson.cs`（文件已经存在时直接使用）
- 修改：`src/server/Agw.Data/Entities/Agents/Agent.cs`、`src/server/Agw.Agents/Application/Persistence/IAgentsDbContext.cs`、`src/server/Agw.Infrastructure/Data/AgwDbContext.cs`、`tests/Agw.Architecture.Tests/BackendArchitectureTests.cs`
- 创建：`Agw.Migrations.Sqlite` 与 `Agw.Migrations.Postgres` 中的 `AddAgentA2AEndpoint`
- 测试：`tests/Agw.Agents.Tests/A2A/AgentA2AEndpointPersistenceTests.cs`

**接口：**
- 产出：
  - `[Table("agent_a2a_endpoint")] public class AgentA2AEndpoint`：`Guid AgentId`（主键）、`string CardUrl`、`[Encrypted] Dictionary<string, string> Credentials`（key 为规范 origin，value 为该 origin 的 header JSON 文本）、`string InterfaceUrl`、`string ProtocolBinding`、`Guid InterfaceRevision`、`bool Streaming`、`AgentA2AEndpointExtra Extra`（列名 `extra`）、`Agent? Agent`
  - `public sealed record AgentA2AEndpointExtra { JsonElement Card; DateTimeOffset CardFetchedAt; }`
  - `Agent.A2AEndpoint`（`AgentA2AEndpoint?`），一对一，模型中的删除行为是 Cascade
  - `credentials` 使用 `ValueConverter<Dictionary<string, string>, string>` 序列化外层 origin 对象，并设置比较器跟踪字典变化；每个字典 value 是 header 对象 JSON 文本，由现有 `[Encrypted]` 字典处理逐项加密
  - `ExtraJson.Converter<T>()`：`ValueConverter<T, string>`，使用 `JsonNamingPolicy.CamelCase`
  - `DbSet<AgentA2AEndpoint> AgentA2AEndpoints`
  - 列长度：`card_url` 与 `interface_url` 2048，`protocol_binding` 32；`credentials` 与 `extra` 的默认值均为 `{}`
  - `EntityOwners["AgentA2AEndpoint"] = "Agw.Agents"`

- [ ] **步骤 1：编写失败的测试**

`AgentA2AEndpointPersistenceTests`（SQLite 文件位于测试输出目录）：
- `SaveChanges_EndpointCredentials_StoredProtectedAndReadBack`：保存 origin `https://a.example:443` 到 header JSON `{"Authorization":"Bearer secret-1"}` 的映射；用原始 SQL 读取 `credentials` 列，确认它是按 origin 索引的 JSON 对象且文本中没有 `secret-1`；新的 DbContext 读回后，经 `A2AEndpointCredentialsJson` 还原出的 header 值相同。
- `SaveChanges_Extra_UsesCamelCaseKeys`：保存 `Card` 为 `{"name":"n","supportedInterfaces":[]}`、`CardFetchedAt` 为 `2026-11-21T08:00:00Z`；原始 SQL 读到的 `extra` 含有键 `card` 与 `cardFetchedAt`，并且 `card` 内的键 `supportedInterfaces` 保持原样。
- `Query_AgentWithInclude_LoadsEndpoint`：`Include(agent => agent.A2AEndpoint)` 读回 endpoint。

- [ ] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*AgentA2AEndpointPersistenceTests"`
预期：编译失败，`AgentA2AEndpoint` 不存在。

- [ ] **步骤 3：实现 entity、配置、converter、DbSet 与 `EntityOwners`**

- [ ] **步骤 4：向用户确认后生成 migration**

按 `docs/1.Development.md` 中两个 provider 的命令各生成一次，名称 `AddAgentA2AEndpoint`。生成的内容只有创建 `agent_a2a_endpoint` 表，没有外键。

- [ ] **步骤 5：运行测试，确认通过**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*AgentA2AEndpointPersistenceTests"`、`dotnet test --project tests/Agw.Architecture.Tests`、`dotnet test --project tests/Agw.Projects.Tests --filter-class "*InitialMigrationTests"`
预期：全部通过。

- [ ] **步骤 6：提交**

```bash
git add src/server/Agw.Data src/server/Agw.Agents src/server/Agw.Infrastructure src/server/Agw.Migrations.Sqlite src/server/Agw.Migrations.Postgres tests/Agw.Architecture.Tests tests/Agw.Agents.Tests
git commit -m "feat(agents): add agent_a2a_endpoint table"
```

---

### 任务 3：endpoint 规则与 `AgentBehavior`

**文件：**
- 创建：`src/server/Agw.Agents/Definitions/Domain/Rules/A2AEndpointRules.cs`
- 修改：`src/server/Agw.Agents/Agw.Agents.csproj`（`A2A` 包）、`src/server/Agw.Agents/Definitions/Domain/Behaviors/AgentBehavior.cs`、`src/server/Agw.Agents/Definitions/Domain/ValueObjects/AgentUpdate.cs`、`src/server/Agw.Agents/Definitions/Agents/AgentUpdateCommand.cs`
- 测试：`tests/Agw.Agents.Tests/A2A/A2AEndpointRulesTests.cs`、`tests/Agw.Agents.Tests/AgentBehaviorTests.cs`

**接口：**
- 使用：任务 1 的 `EngineKind.A2A`、`ErrorCodes.A2AAgentCardInvalid`；任务 2 的 `AgentA2AEndpoint`
- 产出：
  - `public sealed record A2AOriginCredentialsInput(string Origin, IReadOnlyDictionary<string, string?> Headers)`
  - `public sealed record A2AEndpointInput(string? CardUrl, IReadOnlyList<A2AOriginCredentialsInput>? Credentials)`
  - `public sealed record A2ASelectedInterface(string Url, string ProtocolBinding)`
  - `public sealed record A2AEndpointSnapshot(string CardUrl, IReadOnlyDictionary<string, Dictionary<string, string>> Credentials, A2ASelectedInterface Interface, Guid InterfaceRevision, bool Streaming, JsonElement Card, DateTimeOffset CardFetchedAt)`
  - `A2AEndpointRules.ValidateCardUrl(string? cardUrl)` 返回 `Uri`
  - `A2AEndpointRules.NormalizeOrigin(Uri uri)`：scheme、IDNA 小写 host 与有效 port，显式写出有效 port
  - `A2AEndpointRules.ResolveCredentials(IReadOnlyDictionary<string, Dictionary<string, string>>? stored, IReadOnlyList<A2AOriginCredentialsInput>? requested)`：只在规范 origin 相同时沿用 null header 值
  - `A2AEndpointCredentialsJson.Serialize(IReadOnlyDictionary<string, Dictionary<string, string>> credentials)` 与 `Deserialize(Dictionary<string, string> stored)`：每个规范 origin 映射到该 origin 的 JSON header 对象；序列化结果供实体 `[Encrypted] Dictionary<string, string>` 使用
  - `A2AEndpointRules.SelectInterface(AgentCard card)` 返回 `A2ASelectedInterface`
  - `AgentUpdateField.A2AEndpoint`；`AgentUpdate.A2AEndpoint` 与 `AgentUpdateCommand.A2AEndpoint`（`A2AEndpointInput?`）
  - `AgentBehavior.EnsureA2AEndpointInput(A2AEndpointInput? input)`：A2A Agent 创建时必须有输入，其他 Engine 必须没有
  - `AgentBehavior.SetA2AEndpoint(A2AEndpointSnapshot snapshot)`：创建或整体替换 `Agent.A2AEndpoint`

规则的取值（违反时都是 `InvalidParam`，`SelectInterface` 除外）：
- `cardUrl`：绝对地址，scheme 为 `http` 或 `https`，没有 userinfo，长度不超过 2048。
- credentials 最多含 2 个 origin；输入 origin 必须是无 userinfo、path、query、fragment 的绝对 HTTP(S) 地址，规范化后不能重复。每组 headers 最多 20 项；名称符合 HTTP token，不区分大小写时互不相同，不属于 `Host`、`Content-Length`、`Content-Type`、`Transfer-Encoding`、`Connection`、`Accept`、`A2A-Version`、`A2A-Extensions`；用户可明确配置 `Cookie`；值长度 1 到 8192，不含 CR、LF。
- `ResolveCredentials`：`requested` 为 null 时返回 `stored`；给出时是完整 origin 集合。每组中值为 null 的名称只从同一规范 origin 的存储值读取；名称不存在时拒绝。省略的 header 名称与 origin 不保留。
- `SelectInterface`：按 `SupportedInterfaces` 的顺序取第一个 binding 为 `HTTP+JSON` 或 `JSONRPC`（不区分大小写）并且 `Url` 通过与 `cardUrl` 相同的地址规则的 interface；没有时抛出 `A2AAgentCardInvalid`。返回的 `ProtocolBinding` 是 `ProtocolBindingNames` 中的标准写法。
- A2A Agent：`ModelProviderId` 为 null，`Extra` 为 null、空白或 `{}`（保存为 `{}`），`EnvironmentVariables` 为空，`ResponseSchema` 为 null。更新时带有非 null 的 `modelProviderId`、非空的 `extra`、非空的 `environmentVariables` 或非 null 的 `responseSchema` 被拒绝。

- [ ] **步骤 1：编写失败的测试**

```csharp
[Theory]
[InlineData("ftp://example.com/card.json")]
[InlineData("/relative/card.json")]
[InlineData("https://user:pass@example.com/card.json")]
[InlineData("")]
public void ValidateCardUrl_Invalid_ThrowsInvalidParam(string cardUrl)
{
    var error = Assert.Throws<AgwException>(() => A2AEndpointRules.ValidateCardUrl(cardUrl));
    Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
}

[Fact]
public void SelectInterface_MultipleSupported_ReturnsFirstInCardOrder()
{
    var card = new AgentCard
    {
        SupportedInterfaces =
        [
            new AgentInterface { Url = "https://a.example/grpc", ProtocolBinding = "GRPC" },
            new AgentInterface { Url = "https://a.example/rpc", ProtocolBinding = "jsonrpc" },
            new AgentInterface { Url = "https://a.example/rest", ProtocolBinding = "HTTP+JSON" },
        ],
    };
    Assert.Equal(new A2ASelectedInterface("https://a.example/rpc", "JSONRPC"), A2AEndpointRules.SelectInterface(card));
}
```

`A2AEndpointRulesTests` 还包含：
- `ValidateCardUrl_LongerThan2048_ThrowsInvalidParam`。
- `ValidateCredentials` 的拒绝用例：3 个 origin；同一规范 origin 重复；origin 有 userinfo、path、query、fragment 或非 HTTP scheme；每组 21 项；名称 `Host`；名称 `X Bad`；`Authorization` 与 `authorization` 同时出现；值为空字符串；值含 `\\n`；值长度 8193。
- `NormalizeOrigin_DefaultPortAndHostCase_ReturnsCanonicalOrigin`：`HTTPS://A.EXAMPLE/x` 与 `https://a.example:443/y` 得到同一 origin。
- `NormalizeOrigin_NonDefaultPort_ReturnsExplicitPort`：`https://a.example:8443/x` 得到 `https://a.example:8443`。
- `ResolveCredentials_NullValue_KeepsValueForSameOrigin`：同一规范 origin 的 null 使用原 header 值，并移除省略的 header。
- `ResolveCredentials_NullValueForUnknownName_ThrowsInvalidParam`。
- `ResolveCredentials_NullValueForOtherOrigin_ThrowsInvalidParam`：不能从另一个 origin 读取值。
- `ResolveCredentials_OmittedOriginRemovesCredentials`。
- `SelectInterface_EmptyInterfaces_ThrowsCardInvalid`、`SelectInterface_OnlyGrpc_ThrowsCardInvalid`、`SelectInterface_RelativeUrl_ThrowsCardInvalid`，断言错误码 `5020009`。

`AgentBehaviorTests` 增加：
- `EnsureA2AEndpointInput_A2AWithoutInput_ThrowsInvalidParam`。
- `EnsureA2AEndpointInput_CodexWithInput_ThrowsInvalidParam`。
- `PrepareForCreate_A2AWithModelProvider_ThrowsInvalidParam`，以及 `extra` 为 `{"a":1}`、`environmentVariables` 非空、`responseSchema` 非 null 各一个用例。
- `PrepareForCreate_A2AWithBlankExtra_StoresEmptyObject`。
- `EnsureUpdateAllowed_A2AWithExtra_ThrowsInvalidParam`。
- `SetA2AEndpoint_ExistingEndpoint_ReplacesAllValues`。

- [ ] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*A2AEndpointRulesTests"`
预期：编译失败，`A2AEndpointRules` 不存在。

- [ ] **步骤 3：实现上面列出的规则、record 与 `AgentBehavior` 的方法**

`AgentBehavior.EnsureAgentKindIsValid` 接受 `EngineKind.A2A`。

- [ ] **步骤 4：运行测试，确认通过**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*A2AEndpointRulesTests" --filter-class "*AgentBehaviorTests"`、`dotnet test --project tests/Agw.Architecture.Tests`
预期：全部通过。

- [ ] **步骤 5：提交**

```bash
git add src/server/Agw.Agents tests/Agw.Agents.Tests
git commit -m "feat(agents): add A2A endpoint rules"
```

---

### 任务 4：出站 HTTP、card 获取与测试 server

**文件：**
- 创建：`src/server/Agw.Agents/ExternalAgents/A2A/A2ARemoteAgentOptions.cs`、`src/server/Agw.Agents/Application/A2A/IA2ARemoteHttpClientFactory.cs`、`IA2AAgentCardClient.cs`、`src/server/Agw.Agents/Infrastructure/A2A/A2ARemoteHttpClientFactory.cs`、`A2AOriginCredentialHandler.cs`、`A2AAgentCardClient.cs`
- 修改：`src/server/Agw.Agents/DependencyInjection.cs`、`src/server/Agw.Host/appsettings.json`
- 测试：`tests/Agw.Agents.Tests/A2A/A2ATestAgentServer.cs`、`A2AAgentCardClientTests.cs`、`A2ARemoteAgentOptionsTests.cs`

**接口：**
- 使用：任务 1 的 `PrivateNetworkAddresses` 与错误码
- 产出：
  - `A2ARemoteAgentOptions`：`SectionName = "A2ARemoteAgents"`；`AllowPrivateNetworks = true`；`CardFetchTimeout = 00:00:15`；`CardMaxBytes = 1048576`；`RequestTimeout = 00:01:00`；`StreamIdleTimeout = 00:05:00`；`PollInterval = 00:00:02`；`ResubscribeAttempts = 3`
  - `IA2ARemoteHttpClientFactory.Create(Uri targetUri, IReadOnlyDictionary<string, Dictionary<string, string>> credentials)` 返回只绑定到目标 origin 的包装 `HttpClient`；`A2ARemoteHttpClientFactory.ClientName = "a2a-remote"`
  - `IA2AAgentCardClient.FetchAsync(Uri cardUrl, IReadOnlyDictionary<string, Dictionary<string, string>> credentials, CancellationToken cancellationToken)` 返回 `Task<AgentCard>`
  - `AddAgents` 注册配置段、命名 client 与两个接口的实现
  - 测试类型：

```csharp
internal sealed record A2ATestAgentSetup(
    string ProtocolBinding,
    bool Streaming,
    IAgentHandler Handler,
    IReadOnlyDictionary<string, string>? RequiredCardHeaders = null,
    IReadOnlyDictionary<string, string>? RequiredInterfaceHeaders = null
);

internal sealed record A2ARecordedRequest(string Method, string PathAndQuery, IReadOnlyDictionary<string, string> Headers, string Body);

internal sealed class A2ATestAgentServer : IAsyncDisposable
{
    public static Task<A2ATestAgentServer> StartAsync(A2ATestAgentSetup setup);
    public Uri CardUrl { get; }
    public Uri InterfaceUrl { get; }
    public AgentCard Card { get; set; }
    public string? CardBodyOverride { get; set; }
    public string? RedirectLocation { get; set; }
    public string? SetCookie { get; set; }
    public TimeSpan CardDelay { get; set; }
    public IReadOnlyList<A2ARecordedRequest> Requests { get; }
    public IReadOnlyList<string> CanceledTaskIds { get; }
    public void DropActiveStreams();
}

internal sealed class ScriptedAgentHandler(
    Func<RequestContext, AgentEventQueue, CancellationToken, Task> execute
) : IAgentHandler;
```

`A2ATestAgentServer` 的行为：
- Kestrel 监听 `127.0.0.1` 的随机端口。agent 位于 `/agent`：`ProtocolBinding` 为 `HTTP+JSON` 时用 `MapHttpA2A` 映射，为 `JSONRPC` 时用 `MapA2A` 映射；card 位于 `/agent/.well-known/agent-card.json`。
- `Card` 的初始内容：`Name = "test-agent"`、`Description = "Test agent"`、`Version = "1.0.0"`、`Capabilities.Streaming = setup.Streaming`、`SupportedInterfaces` 只有 `InterfaceUrl` 与 `setup.ProtocolBinding` 一项。
- `CardBodyOverride` 不为 null 时，card 请求原样返回这段文本，状态 200。`CardDelay` 是 card 响应前的等待时间。
- `RequiredCardHeaders` 与 `RequiredInterfaceHeaders` 中任一 header 不匹配时，对应路径得到 401。
- `RedirectLocation` 不为 null 时，card 请求返回 302；目标 URI 可指向另一测试 server。
- `SetCookie` 不为 null 时，响应携带该 `Set-Cookie`；后续经共享 handler 发出的请求记录是否带有 `Cookie`。
- 一个中间件在处理之前把每个请求记入 `Requests`。`ScriptedAgentHandler.CancelAsync` 把 task 的 id 记入 `CanceledTaskIds` 并用 `TaskUpdater.CancelAsync` 结束 task。
- `DropActiveStreams()` 中止当前所有正在响应的连接。
- server 由 `A2AServer`、SDK 的 `InMemoryTaskStore` 与 `ChannelEventNotifier` 组成。

- [ ] **步骤 1：编写 `A2ATestAgentServer` 与失败的测试**

`A2ARemoteAgentOptionsTests`：`Bind_DefaultSection_UsesDocumentedDefaults`，空配置绑定后各项等于上面的默认值。

`A2AAgentCardClientTests`（服务由 `AddAgents` 的注册组成，配置用 `AddInMemoryCollection`）：
- `FetchAsync_ValidCard_ReturnsCardAndSendsOriginCredentials`：返回的 `Name` 为 `test-agent`；card origin 配置的 `X-Api-Key` 被发送；请求地址与 `CardUrl` 完全相同。
- `FetchAsync_CardUrlWithQuery_RequestsExactUrl`：`CardUrl` 后加 `?v=2`，记录到的 `PathAndQuery` 以 `?v=2` 结尾。
- `FetchAsync_Unauthorized_ThrowsFetchFailed`：`RequiredCardHeaders` 不匹配，错误码 `5020008`，消息含 `401`。
- `FetchAsync_HtmlBody_ThrowsCardInvalid`：`CardBodyOverride = "<html></html>"`，错误码 `5020009`。
- `FetchAsync_CardLargerThanLimit_ThrowsCardInvalid`：`CardMaxBytes = 1024`，`CardBodyOverride` 为 4096 个字符的合法 JSON。
- `FetchAsync_ServerSlowerThanTimeout_ThrowsFetchFailed`：`CardFetchTimeout = 00:00:01`，`CardDelay` 为 3 秒。
- `FetchAsync_PrivateNetworksDisallowed_RejectsLoopbackTarget`：`AllowPrivateNetworks = false`，错误码 `InvalidParam`，`Requests` 为空。
- `FetchAsync_RedirectResponse_ThrowsFetchFailed`：card 地址返回 302，`Requests` 中只有一个请求；redirect target 没有收到任何请求或 secret header。
- `RemoteHttpClient_SharedHandlerDoesNotShareCookiesBetweenClients`：两个 client 共用命名 handler；第一方响应设置 Cookie 后，第二方请求没有 `Cookie` header。
- `RemoteHttpClient_ExplicitCookieHeaderUsesOnlyItsOrigin`：用户配置的 `Cookie` header 只出现在对应 origin 的请求中。

- [ ] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*A2AAgentCardClientTests"`
预期：编译失败，`IA2AAgentCardClient` 不存在。

- [ ] **步骤 3：实现**

- `a2a-remote` 的主 handler 是 `SocketsHttpHandler`：`AllowAutoRedirect = false`、`UseCookies = false`、`PooledConnectionLifetime` 5 分钟，handler 生存期无限。`AllowPrivateNetworks` 为 false 时 `UseProxy = false`，`ConnectCallback` 解析主机后任一地址 `IsPrivate` 即抛出 `AgwException(ErrorCodes.InvalidParam, "A2A endpoint resolves to a prohibited address.")`。
- `A2ARemoteHttpClientFactory.Create(targetUri, credentials)` 通过 `IHttpMessageHandlerFactory.CreateHandler("a2a-remote")` 取得 pooled transport，并包上本 client 独享的 `A2AOriginCredentialHandler`。外层 handler 持有不可变的 origin 与凭据，验证每个 request 的 origin 后再加 header；外层 handler 不进入共享 pool，凭据不放入 pooled handler 或 `DefaultRequestHeaders`。包装 client 释放时，named handler factory 负责 pooled transport 生命周期。`Timeout` 为无限。
- `A2AAgentCardClient`：`A2ACardResolver` 的 `baseUrl` 取 `cardUrl`，`agentCardPath` 取 `cardUrl` 最后一个路径片段加 query（路径以 `/` 结尾时取 `.` 加 query）；响应体长度由一个包装响应流的 `DelegatingHandler` 限制；`A2AException` 与 `HttpRequestException` 按设计文档第 2 节转换为错误码。
- `appsettings.json` 增加 `A2ARemoteAgents` 配置段，写出全部默认值。

- [ ] **步骤 4：运行测试，确认通过**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*A2AAgentCardClientTests" --filter-class "*A2ARemoteAgentOptionsTests"`
预期：全部通过。

- [ ] **步骤 5：提交**

```bash
git add src/server/Agw.Agents src/server/Agw.Host/appsettings.json tests/Agw.Agents.Tests
git commit -m "feat(agents): add A2A outbound http client and card fetch"
```

---

### 任务 5：定义管理接口

**文件：**
- 修改：`src/server/Agw.Agents/Definitions/Contracts/AgentRequests.cs`、`src/server/Agw.Agents/Definitions/Controllers/AgentsController.cs`、`src/server/Agw.Agents/Definitions/Agents/AgentAppService.cs`、`src/server/Agw.Infrastructure/Agents/AgentDeletionCoordinator.cs`
- 修改：`tests/Agw.Agents.Tests/TestAgentAppService.cs`（构造函数增加的依赖）、原始持久化访问清单的测试数据
- 测试：`tests/Agw.Agents.Tests/A2A/A2AAgentDefinitionTests.cs`

**接口：**
- 使用：任务 3 的 `A2AEndpointInput`、`A2AEndpointRules`、`AgentBehavior`；任务 4 的 `IA2AAgentCardClient`、`A2ATestAgentServer`
- 产出：
  - `AgentCreateRequest` 增加 `A2AEndpointRequest? A2AEndpoint = null`；`AgentUpdateRequest.A2AEndpoint`（设置时加入 `AgentUpdateField.A2AEndpoint`）
  - `public sealed record A2AEndpointRequest(string? CardUrl, IReadOnlyList<A2AOriginCredentialsRequest>? Credentials)`
  - `public sealed record A2AOriginCredentialsRequest(string Origin, Dictionary<string, string?> Headers)`
  - `public sealed record A2AOriginHeaderNamesDto(string Origin, IReadOnlyList<string> Names)`
  - `public sealed record A2AEndpointDto(string CardUrl, IReadOnlyList<A2AOriginHeaderNamesDto> CredentialHeaderNames, string InterfaceUrl, string ProtocolBinding, bool Streaming, AgentA2AEndpointExtra Extra)`；`AgentResponse.A2AEndpoint`（`A2AEndpointDto?`）。`Extra` 序列化为 `extra: { card, cardFetchedAt }`
  - `public sealed record A2ACardPreviewRequest(string? CardUrl, IReadOnlyList<A2AOriginCredentialsRequest>? Credentials, Guid? AgentId)`
  - `public sealed record A2ACardPreviewResponse(JsonElement Card, string InterfaceUrl, string ProtocolBinding, bool Streaming)`
  - `public sealed record A2ACardRefreshRequest(Guid AgentId)`
  - `AgentAppService.CreateAgentAsync` 增加参数 `A2AEndpointInput? a2aEndpoint`
  - `AgentAppService.PreviewA2ACardAsync(A2ACardPreviewRequest request, CancellationToken cancellationToken)` 返回 `Task<A2ACardPreviewResponse>`
  - `AgentAppService.RefreshA2ACardAsync(Guid agentId, CancellationToken cancellationToken)` 返回 `Task<Agent?>`
  - 路由 `POST /api/agents/a2a-card/preview`、`POST /api/agents/a2a-card/refresh`
  - `AgentAppService.CreateAgentQuery` 增加 `Include(agent => agent.A2AEndpoint)`

- [ ] **步骤 1：编写失败的测试**

`A2AAgentDefinitionTests`（SQLite，`A2ATestAgentServer`，两个用户用 `IUserInfoService` 的两个对象表示）：
- `CreateAgent_A2A_StoresEndpointAndHidesCredentialValues`：创建后 `AgentResponse.FromDomain` 的 `A2AEndpoint.CredentialHeaderNames` 含 card origin 与 `["Authorization"]`；`InterfaceUrl` 等于测试 server 的 `InterfaceUrl`；`Streaming` 等于 setup；`A2AEndpoint.Extra.Card` 的 `name` 为 `test-agent`；序列化后的响应文本含有 `"extra":{"card":` 与 `"cardFetchedAt"`，没有 header 的值；Agent 自己的 `Extra`（`agent.extra` 列）为 `{}`。
- `CreateAgent_A2AWithoutEndpoint_ThrowsInvalidParam`：`Requests` 为空。
- `CreateAgent_CardWithoutInterfaces_ThrowsCardInvalidAndSavesNothing`：`Card.SupportedInterfaces = []`，错误码 `5020009`，数据库中没有该 Agent。
- `CreateAgent_CardBodyIsHtml_ThrowsCardInvalidAndSavesNothing`。
- `UpdateAgent_CredentialsWithKeptValue_RefetchesCardAndStampsAgent`：card origin A 的 `Authorization = null`、`X-Tenant = "blue"`；card 请求带原 `Authorization` 与新 `X-Tenant`；Agent 的 `UpdateTime` 晚于更新前。
- `UpdateAgent_CardUrlOnOtherOriginWithOldOriginValue_RejectsNullReuse`：启动第二个测试 server；在新 origin B 的 header 组中把只存在于 origin A 的 `Authorization` 设为 null；返回 `InvalidParam`，B 没有收到请求。
- `UpdateAgent_CardUrlOnOtherOrigin_OmittedCredentialsSendsNoOldValue`：保留 A 的已存凭据并把 cardUrl 改到 B；B 收到的 card 请求没有 A 的 `Authorization`。
- `UpdateAgent_InterfacePathOrQueryChanged_RotatesRevision`：仅修改 interface path 或 query，也生成新的 `InterfaceRevision`。
- `UpdateAgent_InterfaceFragmentChanged_ReuseRevision`：只修改 fragment，保留 revision。
- `UpdateAgent_InterfaceDefaultPortAndHostCaseEquivalent_ReuseRevision`：scheme/host 大小写与默认 port 的不同写法规范化后相同，保留 revision；binding 名称大小写变化后规范化为标准名比较。
- `UpdateAgent_DisplayNameOnly_KeepsEndpointAndSendsNoRequest`。
- `RefreshCard_CardChanged_ReplacesSnapshotAndInterface`：修改测试 server 的 `Card.Description`、`Capabilities.Streaming` 与 interface URL 后刷新，`Extra.Card`、`Streaming`、`InterfaceUrl` 与按需更新的 `InterfaceRevision` 随之改变。
- `RefreshCard_InterfaceOriginChanged_KeepsCredentialsBoundToOldOrigin`：card origin A 声明 interface origin B；保存的 A 凭据仍只绑定 A，B 没有配置凭据时 Turn 请求不包含 A 的值。
- `RefreshCard_NonA2AAgent_ReturnsNull`。
- `PreviewCard_WithAgentIdAndKeptValue_UsesStoredValue`：card 请求带有已保存的 `Authorization`；数据库没有变化。
- `PreviewCard_OtherOriginWithKeptValue_ThrowsInvalidParam`。
- `PreviewCard_AgentOfOtherUser_ThrowsResourceNotFound`。
- `GetAgent_OtherUser_ReturnsNull`：另一个用户读取与刷新都得到 null。
- `DeleteAgent_A2A_RemovesEndpointRow`。

- [ ] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*A2AAgentDefinitionTests"`
预期：编译失败，`A2AEndpointRequest` 不存在。

- [ ] **步骤 3：实现**

- `CreateAgentAsync`、`UpdateAgentAsync`、`RefreshA2ACardAsync` 在 `AgentDefinitionLock` 内完成读取现有 endpoint、`ResolveCredentials`、获取 card、`SelectInterface`、确定 `InterfaceRevision` 与保存。`CardFetchedAt` 取自注入的 `TimeProvider`。
- 读回 endpoint 时通过 `A2AEndpointCredentialsJson.Deserialize` 还原 credentials；写入 entity 时通过 `Serialize` 生成可由 `[Encrypted] Dictionary<string, string>` 处理的存储 map。`A2AEndpointDto.CredentialHeaderNames` 只从还原后的 map 取 origin 与 header 名称。
- card 请求只使用 `cardUrl` origin 的凭据。interface URL 或 binding 改变时生成新的 `InterfaceRevision`；只更新凭据或 card 快照时保留 revision。
- `InterfaceRevision` 比较 target 时包含规范 scheme、host、effective port、path 与 query，忽略 fragment；binding 使用 A2A 标准名比较。
- `Streaming` 取 `card.Capabilities?.Streaming == true`。`Card` 由 `JsonSerializer.SerializeToElement(card, A2AJsonUtilities.DefaultOptions)` 得到。
- endpoint 改变时按现有做法把 `DisplayName` 标为已修改，使 `UpdateTime` 更新。
- `PreviewA2ACardAsync` 不取锁、不写数据库；`AgentId` 对应的 Agent 不属于当前用户或不是 A2A Agent 时抛出 `ResourceNotFound`。
- `AgentsController` 的两个新 action 返回 `ApiResult.Ok(...)`，refresh 的结果为 null 时返回 `ErrorCodes.ResourceNotFound.ToApiResult()`。
- `AgentDeletionCoordinator` 在同一个事务中删除 `AgentA2AEndpoints` 中的行。
- 更新原始持久化访问清单，使 `tests/Agw.Architecture.Tests` 通过。

- [ ] **步骤 4：运行测试，确认通过**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*A2AAgentDefinitionTests" --filter-class "*AgentAppService*"`、`dotnet test --project tests/Agw.Architecture.Tests`
预期：全部通过。

- [ ] **步骤 5：提交**

```bash
git add src/server/Agw.Agents src/server/Agw.Infrastructure tests/Agw.Agents.Tests tests/Agw.Architecture.Tests
git commit -m "feat(agents): manage A2A agent endpoints"
```

---

### 任务 6：会话状态与持久化规则

**文件：**
- 创建：`src/server/Agw.Agents.Execution/Agents/ExternalAgents/A2A/A2ARemoteAgentSession.cs`
- 修改：`src/server/Agw.Agents.Execution/Agw.Agents.Execution.csproj`（`A2A` 包）、`src/server/Agw.Agents.Execution/Agents/Sessions/AgentSessionStateStore.cs`、`Agents/Runtime/AgentRuntime.cs`、`Agents/Runtime/AgentRuntimeFactory.CreateRuntime.cs`、`Agents/Runtime/AgentRuntimeFactory.Mode.cs`、`Agents/Runtime/ExternalProviderSessionBindings.cs`、`Agents/Turns/AgentTurnExecutor.cs`
- 修改：`src/server/Agw.Agents/Application/Persistence/IAgentSessionStatePersistence.cs`、`src/server/Agw.Infrastructure/Agents/AgentSessionStatePersistence.cs`
- 测试：`tests/Agw.Agents.Tests/A2A/A2ARemoteAgentSessionTests.cs`、`tests/Agw.Agents.Tests/AgentSessionStateStoreTests.cs`

**接口：**
- 产出：
  - `public sealed class A2ARemoteAgentSession : AgentSession`：`Guid? InterfaceRevision`、`string? ContextId`、`string? TaskId`、`TaskState? TaskState`；`internal void BindEndpointRevision(Guid revision)`（revision 不同时清空远端状态并绑定新值）、`internal void RecordContext(string? contextId)`（已有值而新值不同时抛出 `A2ARemoteAgentRequestFailed`）、`internal void RecordTask(string taskId, TaskState state)`、`internal void ClearTask()`、`internal void ClearRemoteState()`；`internal JsonElement Serialize(JsonSerializerOptions? options)`、`internal static A2ARemoteAgentSession Deserialize(JsonElement state, JsonSerializerOptions? options)`
  - 序列化的属性名：`interfaceRevision`、`contextId`、`taskId`、`taskState`、`stateBag`；`taskState` 使用 A2A SDK 的 JSON 写法（例如 `input-required`）
  - `AgentSessionStateStore.UsesSdkSession(EngineKind kind)`：`Maf` 与 `A2A` 为 true
  - `AgentSessionStateStore.SaveAsync(EngineKind engineKind, AgentSessionStateScope sessionScope, AIAgent aiAgent, AgentSession session, CancellationToken cancellationToken)`
  - `IAgentSessionStatePersistence.GetEngineKindAsync(Guid agentId, string ownerUserId, CancellationToken cancellationToken)` 返回 `Task<EngineKind?>`，取代 `GetAgentTypeAsync`
  - `AgentRuntime.EngineKind`（构造函数参数，默认 `EngineKind.Maf`）
  - `ExternalProviderSessionBindings.UsesProviderSessionBinding`：只对 `ClaudeCode`、`Codex`、`Pi` 为 true

- [ ] **步骤 1：编写失败的测试**

`A2ARemoteAgentSessionTests`：
- `Serialize_SessionWithTask_RoundTrips`：绑定 `InterfaceRevision`，再执行 `RecordContext("c-01")`、`RecordTask("t-07", TaskState.InputRequired)`，并在 `StateBag` 中放入一个值；序列化文本包含 `interfaceRevision`、`contextId`、`taskId`、`taskState`；反序列化后各属性与 `StateBag` 的值相同。
- `RecordContext_DifferentValue_ThrowsRequestFailed`：错误码 `5020010`。
- `ClearTask_AfterTask_ResetsTaskOnly`：`ContextId` 保留。
- `BindEndpointRevision_ChangedRevision_ClearsRemoteState`：revision 改变时清空 `ContextId`、`TaskId`、`TaskState` 并记录新 revision。
- `ClearRemoteState_AfterTaskNotFound_ClearsContextAndTask`：清空远端 ID 与状态，保留当前 revision。

`AgentSessionStateStoreTests` 增加：
- `SaveAsync_A2AEngine_PersistsSession` 与 `GetOrCreateAsync_A2AEngine_RestoresSession`：使用一个返回 `A2ARemoteAgentSession` 的 `AIAgent`（任务 7 之前用测试文件内直接继承 `AIAgent` 并只实现 session 三个方法的类）。
- `SaveAsync_CodexEngine_SkipsPersistence`。
- `GetOrCreateForNodeAsync_A2AAgentTwoNodes_KeepsSeparateSessions`：同一个对话与 A2A Agent、两个不同的 `AgentflowNodeId`，分别保存 `ContextId` 为 `c-a` 与 `c-b` 的 session；按节点读回的值各自相同。

`AgentRuntimeFactoryCompositionTests` 增加 `UsesProviderSessionBinding_A2A_ReturnsFalse`。

- [ ] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*A2ARemoteAgentSessionTests"`
预期：编译失败，`A2ARemoteAgentSession` 不存在。

- [ ] **步骤 3：实现**

- `AgentSessionStateStore` 中三处 `AgentType` 判断改为 `UsesSdkSession`。
- `AgentRuntimeFactory.CreateRuntime.cs` 构造 `AgentRuntime` 时传入 `EngineKinds.Resolve(agent)`；`AgentTurnExecutor` 与 `AgentRuntimeFactory.Mode.cs` 调用 `SaveAsync` 时传入 `runtime.EngineKind`。
- `AgentSessionStatePersistence.GetEngineKindAsync`：System Agent 返回 `Maf`，External Agent 返回 `ExternalAgentKind`。

- [ ] **步骤 4：运行测试，确认通过**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*A2ARemoteAgentSessionTests" --filter-class "*AgentSessionStateStoreTests" --filter-class "*ProviderSession*"`、`dotnet test --project tests/Agw.Architecture.Tests`
预期：全部通过。

- [ ] **步骤 5：提交**

```bash
git add src/server/Agw.Agents src/server/Agw.Agents.Execution src/server/Agw.Infrastructure tests/Agw.Agents.Tests
git commit -m "feat(agents): persist A2A remote sessions"
```

---

### 任务 7：`A2ARemoteAgent` 的流式执行

**文件：**
- 创建：`src/server/Agw.Agents.Execution/Agents/ExternalAgents/A2A/A2ARemoteAgent.cs`
- 修改：`src/server/Agw.Agents.Execution/Agents/History/AgentMessageAdapters.cs`、`Agents/History/AgwChatHistoryProvider.cs`
- 测试：`tests/Agw.Agents.Tests/A2A/A2ARemoteAgentStreamingTests.cs`

**接口：**
- 使用：任务 4 的 `A2ARemoteAgentOptions`、`A2ATestAgentServer`；任务 6 的 `A2ARemoteAgentSession`
- 产出：
  - `public sealed class A2ARemoteAgent : AIAgent`，构造函数 `(IA2AClient client, string id, string name, string description, bool streaming, Guid interfaceRevision, A2ARemoteAgentOptions options, TimeProvider timeProvider, ILogger logger)`；Runtime 传入 `AgentA2AEndpoint.InterfaceRevision`
  - `CreateSessionCoreAsync` 返回 `A2ARemoteAgentSession`；`SerializeSessionCoreAsync` 与 `DeserializeSessionCoreAsync` 使用任务 6 的方法
  - `RunCoreAsync` 汇总 `RunCoreStreamingAsync` 的输出
  - 输出的 `AgentResponseUpdate`：
    - `Message`：`Role = Assistant`，`MessageId = message.MessageId`
    - artifact：`MessageId = $"{taskId}:{artifactId}"`；同一个 artifact 再次以 `Append = false` 到达时为 `$"{taskId}:{artifactId}#{n}"`，`n` 从 1 开始
    - `submitted`、`working` 的 status message：内容为 `TextReasoningContent`，`MessageId = status.Message.MessageId`
    - `completed`、`input-required`、`auth-required` 的 status message：内容来自 `Part.ToAIContent()`，`MessageId = status.Message.MessageId`
    - Result：`AdditionalProperties["type"] = "result"`，`AdditionalProperties["resultSourceMessageId"]` 为本轮最后一条 Assistant 消息的 `MessageId`，内容是一个 `TextContent`
    - 每个 update 的 `RawRepresentation` 是 SDK 的事件对象
  - `internal sealed class A2AMessageAdapter : ModelMessageAdapter`，`IsExternalEngine` 为 true；`AgwChatHistoryProvider.Create` 为 `EngineKind.A2A` 使用它

请求、状态处理、Result 文本与错误码的规则见设计文档 5.1、5.4、5.5、5.6。本任务只实现 `streaming` 为 true 的路径，`streaming` 为 false 时抛出 `NotSupportedException`，由任务 8 取代。

- [ ] **步骤 1：编写失败的测试**

`A2ARemoteAgentStreamingTests`（每个用例启动一个测试 server，用 `IA2ARemoteHttpClientFactory` 与 `A2AHttpJsonClient` 或 `A2AClient` 构造 `A2ARemoteAgent`，传入 endpoint 的 `InterfaceRevision`，直接调用 `RunStreamingAsync`）：
- `RunStreaming_MessageReply_EmitsAssistantAndResult`（`HTTP+JSON`）：handler 用 `MessageResponder.ReplyAsync("hello")`；输出依次是一条文本为 `hello` 的 Assistant update 与一条 Result，Result 的文本为 `hello`，`resultSourceMessageId` 等于前一条的 `MessageId`；session 的 `ContextId` 不为空，`TaskId` 为 null。
- `RunStreaming_JsonRpcBinding_SendsJsonRpcRequest`（`JSONRPC`）：记录到的请求 `Body` 含有 `"jsonrpc":"2.0"`，路径为 `/agent`。
- `RunStreaming_ChunkedArtifact_UsesOneMessageId`：handler 依次 `SubmitAsync`、`StartWorkAsync`（message 文本 `searching`）、三次 `AddArtifactAsync`（同一个 `artifactId`，第一次不追加，后两次追加，文本 `a`、`b`、`c`）、`CompleteAsync`；三条 artifact update 的 `MessageId` 相同；有一条 `TextReasoningContent` 为 `searching`；Result 文本为 `abc`；session 的 `TaskState` 为 `Completed`。
- `RunStreaming_ArtifactReplaced_UsesSuffixedMessageId`：同一个 `artifactId` 两次不追加，第二条的 `MessageId` 以 `#1` 结尾。
- `RunStreaming_InputRequiredThenReply_ContinuesSameTask`：第一轮 handler 调用 `RequireInputAsync`（message 文本 `Which city?`）；输出一条 Assistant 文本与 Result，文本都是 `Which city?`；session 的 `TaskState` 为 `InputRequired`。第二轮用同一个 session 发送 `Paris`；记录到的第二个请求的 message 中 `contextId` 与 `taskId` 等于第一轮的值。
- `RunStreaming_CompletedThenNewInput_OmitsTaskId`：第二个请求有 `contextId`，没有 `taskId`。
- `RunStreaming_MultipleInputMessages_MergesPartsIntoOneMessage`：传入两条 `ChatMessage`（文本 `prefix` 与 `question`）；记录到的请求中只有一个 message，含两个 text part。
- `RunStreaming_NoConvertibleContent_ThrowsInvalidParam`：传入只含 `UsageContent` 的消息；`Requests` 中没有调用请求。
- `RunStreaming_TaskFailed_ThrowsTaskFailedWithRemoteText`：handler 调用 `FailAsync`（message 文本 `quota exceeded`）；错误码 `5020012`，消息含 `quota exceeded`；没有 Result。
- `RunStreaming_TaskRejected_ThrowsTaskFailed`。
- `RunStreaming_Unauthorized_ThrowsUnauthorized`：`RequiredInterfaceHeaders` 不匹配，错误码 `5020011`。
- `RunStreaming_StreamEndsWithoutEvents_ThrowsRequestFailed`：handler 直接返回；错误码 `5020010`；没有 Result。
- `RunStreaming_ContextIdChanged_ThrowsRequestFailed`：session 先 `RecordContext("other")`，远端返回自己的 `contextId`；错误码 `5020010`。
- `RunStreaming_ContinuationRejectedWithTaskNotFound_ClearsRemoteState`：session 绑定当前 revision，先 `RecordContext` 为远端可接受的值并 `RecordTask("missing", TaskState.InputRequired)`；错误码 `5020010`；之后 session 的 `ContextId`、`TaskId` 与 `TaskState` 为 null；再运行一轮，记录到的请求没有 `contextId` 或 `taskId`。
- `RunStreaming_ChangedInterfaceRevision_StartsNewContext`：session 带旧 revision、context 与 task；更换 interface revision 后运行，新请求没有旧 `contextId` 或 `taskId`。
- `Map_UserRoleUpdate_IsExcludedFromModelHistory`：`A2AMessageAdapter.Map` 对 `Role = User` 的 update 返回的消息，`ConversationHistoryMetadata.IsModelHistoryExcluded` 为 true；对 `Role = Assistant` 的 update 为 false。

- [ ] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*A2ARemoteAgentStreamingTests"`
预期：编译失败，`A2ARemoteAgent` 不存在。

- [ ] **步骤 3：实现**

- 输入用 `AIContent.ToPart()` 转换，输出用 `Part.ToAIContent()` 转换。
- 每个事件到达时先更新 session，再输出 update。
- 单次请求的时限 `RequestTimeout` 用与调用方 token 关联的 `CancellationTokenSource` 实现，收到响应头后解除。
- 每次运行开始先调用 `session.BindEndpointRevision(endpointRevision)`。`A2AException` 的 `ErrorCode` 为 `TaskNotFound` 并且请求带有 `taskId` 时调用 `session.ClearRemoteState()`；当前 Turn 返回错误，不自动重试。

- [ ] **步骤 4：运行测试，确认通过**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*A2ARemoteAgentStreamingTests" --filter-class "*HistoryRecording*"`
预期：全部通过。

- [ ] **步骤 5：提交**

```bash
git add src/server/Agw.Agents.Execution tests/Agw.Agents.Tests
git commit -m "feat(agents): add A2A remote agent streaming execution"
```

---

### 任务 8：轮询、重新订阅与中断

**文件：**
- 修改：`src/server/Agw.Agents.Execution/Agents/ExternalAgents/A2A/A2ARemoteAgent.cs`
- 测试：`tests/Agw.Agents.Tests/A2A/A2ARemoteAgentRecoveryTests.cs`

**接口：**
- 使用：任务 7 的 `A2ARemoteAgent`；`A2ATestAgentServer.DropActiveStreams`、`CanceledTaskIds`
- 产出：`A2ARemoteAgent` 在 `streaming` 为 false 时轮询；流中断后重新订阅；取消时调用 `CancelTaskAsync`。规则见设计文档 5.2、5.3 与第 7 节。

测试中的时间参数：`PollInterval = 00:00:00.050`，`StreamIdleTimeout = 00:00:01`，`ResubscribeAttempts = 2`。重新订阅之前的等待由 `Task.Delay(delay, timeProvider, cancellationToken)` 实现，等待时间为 1 秒乘以 2 的次数幂。

- [ ] **步骤 1：编写失败的测试**

- `Run_NonStreamingAgent_PollsUntilCompleted`：`Streaming = false`；handler 在 `StartWorkAsync` 之后等待一个由测试控制的信号，再 `AddArtifactAsync`（文本 `done`）与 `CompleteAsync`；第一个请求的 `configuration.returnImmediately` 为 true；`Requests` 中至少有两个 `GetTask` 请求；输出的 artifact 文本为 `done`，只出现一次；有 Result。
- `Run_NonStreamingAgentAlreadyCompleted_DoesNotPoll`：handler 不等待，直接完成；`Requests` 中没有 `GetTask` 请求；输出 artifact 与 Result。
- `Run_NonStreamingMessageReply_EmitsAssistantAndResult`。
- `RunStreaming_StreamDropped_ResubscribesAndCompletes`：handler 输出第一个完整的 artifact（文本 `one`）后等待信号；测试调用 `DropActiveStreams()` 再放行，handler 输出第二个 artifact（文本 `two`）并完成；`Requests` 中有一个订阅 task 的请求；文本 `one` 的 artifact update 只出现一次；Result 文本为 `onetwo`。
- `RunStreaming_ResubscribeAfterTaskFinished_ReadsFinalState`：断开之后 handler 立即完成；重新订阅被远端以 `UnsupportedOperation` 拒绝后有一个 `GetTask` 请求；Turn 正常结束。
- `RunStreaming_IdleBeyondTimeoutAndPeerUnreachable_ThrowsTimeout`：handler 在 `StartWorkAsync` 之后停止输出；空闲 1 秒后测试关闭 server；错误码 `5040001`。
- `RunStreaming_DroppedBeforeTaskId_ThrowsRequestFailed`：handler 在输出任何事件之前等待，测试断开连接；错误码 `5020010`；没有订阅请求。
- `RunStreaming_Canceled_CancelsRemoteTask`：handler 在 `StartWorkAsync` 后等待；测试取消 token；抛出 `OperationCanceledException`；`CanceledTaskIds` 含有该 task；session 的 `TaskState` 为 `Canceled`。
- `RunStreaming_CanceledInputRequiredReply_NextTurnOmitsTaskId`：第一轮 `input-required`；第二轮续答时取消；第三轮的请求有 `contextId`，没有 `taskId`。
- `RunStreaming_CancelRejectedByPeer_StillThrowsCanceled`：测试 server 在取消前已关闭；仍然抛出 `OperationCanceledException`。
- `RunStreaming_RemoteCanceledTask_ThrowsTaskFailed`：handler 自己调用 `CancelAsync`；错误码 `5020012`。

- [ ] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*A2ARemoteAgentRecoveryTests"`
预期：`Run_NonStreamingAgent_PollsUntilCompleted` 以 `NotSupportedException` 失败。

- [ ] **步骤 3：实现**

- 取消 task 使用独立的 5 秒时限，与已经取消的调用方 token 无关。
- 已经完整输出的 artifact 以 `{taskId}:{artifactId}` 记录在本次运行内；重新订阅得到的 `Task` 快照跳过它们，其余 artifact 以带 `#{n}` 后缀的 `MessageId` 输出完整内容。

- [ ] **步骤 4：运行测试，确认通过**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*A2ARemoteAgentRecoveryTests" --filter-class "*A2ARemoteAgentStreamingTests"`
预期：全部通过。

- [ ] **步骤 5：提交**

```bash
git add src/server/Agw.Agents.Execution tests/Agw.Agents.Tests
git commit -m "feat(agents): add A2A polling, resubscribe and cancel"
```

---

### 任务 9：Runtime 构造与两种执行提供者

**文件：**
- 修改：`src/server/Agw.Agents.Execution/Agents/Runtime/AgentRuntimeFactory.CreateExternalAgents.cs`、`src/server/Agw.Agents.Execution/Runtimes/Durable/DurableExecutionCoordinator.cs`
- 测试：`tests/Agw.Agents.Tests/A2A/A2ARemoteAgentHarness.cs`、`A2ARemoteAgentExecutionTests.cs`、`A2ARemoteAgentDurableTests.cs`

**接口：**
- 使用：任务 4 的 `IA2ARemoteHttpClientFactory`；任务 5 的 `AgentAppService`；任务 6 的 `AgentRuntime.EngineKind`；任务 7、8 的 `A2ARemoteAgent`
- 产出：
  - `AgentRuntimeFactory.CreateA2AAgentAsync(CreateAiAgentRequest request, bool isBackground, CancellationToken cancellationToken)` 返回 `Task<AIAgent?>`，由 `CreateExternalAgentAsync` 在 `BackgroundToolPolicy` 检查之后调用
  - `DurableExecutionCoordinator.RunAgentSegmentAsync` 只对 `ClaudeCode`、`Codex`、`Pi` 抛出现有的不支持错误
  - `A2ARemoteAgentHarness`：结构与 `ProviderSessionExecutionHarness` 相同（全部模块的注册、SQLite、`InProcessExecutionCoordinator`），`CreateAsync(A2ATestAgentServer server, IReadOnlyList<A2AOriginCredentialsRequest>? credentials = null)` 通过 `AgentAppService.CreateAgentAsync` 创建 A2A Agent，并提供 `RunTurnAsync(string text)`、`StartTurnAsync(string text)`、`InterruptAsync()`、`RebuildCoordinatorAsync()`、`ClearConversationAsync()`、`ReadMessages(Guid turnId)`、`ReadHistoryAsync()`、`ReadSessionAsync()`（读取保存的 `A2ARemoteAgentSession`）

构造步骤见设计文档第 4 节。A2A 分支不校验工作目录、不调用 `AgentCapabilityComposer`、不解析模型配置。

- [ ] **步骤 1：编写 `A2ARemoteAgentHarness` 与失败的测试**

`A2ARemoteAgentExecutionTests`：
- `RunTurn_HttpJsonStreamingMessage_EmitsAssistantAndResult`：Turn 的消息依次有 `turn-start`、Assistant 文本、Result、`turn-finished(completed)`；测试 server 收到的调用请求带有配置的 `Authorization`；历史中有用户输入与 Assistant 文本。
- `RunTurn_WorkspaceMissing_StillRuns`：Project 的工作目录不存在，Turn 正常完成。
- `RunTurn_AfterRuntimeRebuild_ReusesContext`：两轮之间 `RebuildCoordinatorAsync()`；第二个请求的 `contextId` 等于第一轮的值。
- `RunTurn_InputRequiredAcrossRebuild_ContinuesSameTask`：第一轮 `input-required`，重建之后续答；请求带有相同的 `taskId`。
- `RunTurn_AfterConversationCleared_StartsNewContext`：清空对话后的请求没有 `contextId`。
- `RunTurn_TaskFailed_FinishesFailedWithRemoteMessage`：消息中有错误码 `5020012` 的 `AgwErrorContent`，之后是 `turn-finished(failed)`。
- `RunTurn_Interrupted_CancelsRemoteTask`：`turn-finished(interrupted)`；`CanceledTaskIds` 不为空；`ReadSessionAsync()` 的 `TaskState` 为 `Canceled`。
- `RunTurn_FailedTurn_SavesSessionContext`：task 失败后保存的 session 有 `ContextId`。
- `RunTurn_EndpointCredentialsUpdatedBetweenTurns_UsesMatchingOrigin`：两轮之间用 `UpdateAgentAsync` 更新 interface origin B 的 credentials；第二轮发往 B 的请求带新值，card origin A 的请求仍只使用 A 的值。
- `RunTurn_InterfaceEndpointChangedBetweenTurns_StartsNewContext`：保存 session 后把 interface path 或 origin 改变；Runtime 重建后新请求不带旧 `contextId` 或 `taskId`。
- `RunTurn_PermissionModeAlwaysAsk_Fails`：权限模式不是 `FullAccess` 时得到现有的权限错误。
- `CreateRuntime_SystemAgentAllowingA2ABackgroundAgent_BuildsRuntime`：System Agent 的 `background-agents` Tool Block 允许该 A2A Agent，组装方式与 `RuntimeDefinitionRefreshTests` 中带 `AllowedAgentIds` 的用例相同；创建父 Runtime 成功，创建过程中测试 server 没有收到请求。

`A2ARemoteAgentDurableTests`（需要 `AGW_TEST_POSTGRES_CONNECTION_STRING`，组装方式与现有的 `DurableExecution*Tests` 相同）：
- `RunAgentSegment_A2AAgent_CompletesInOneSegment`：Turn 完成，事件流中有 Assistant 文本与 Result；保存的 session 有 `ContextId`。
- `RunAgentSegment_CodexAgent_StillRejected`：得到现有的 `Distributed execution currently supports System Agents only.`。

- [ ] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*A2ARemoteAgentExecutionTests"`
预期：`RunTurn_HttpJsonStreamingMessage_EmitsAssistantAndResult` 失败，原因是 `CreateExternalAgentWithDecoratorsAsync` 对 `A2A` 返回 null。

- [ ] **步骤 3：实现 `CreateA2AAgentAsync` 与 Durable 的检查**

`HttpClient` 用一个实现 `IAsyncDisposable` 的包装交给 `ResourceOwningAIAgent`。

- [ ] **步骤 4：运行测试，确认通过**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*A2ARemoteAgentExecutionTests" --filter-class "*A2ARemoteAgentDurableTests" --filter-class "*ProviderSession*" --filter-class "*DurableExecution*"`
预期：全部通过；没有 PostgreSQL 连接时 Durable 用例按现有方式跳过。

- [ ] **步骤 5：提交**

```bash
git add src/server/Agw.Agents.Execution tests/Agw.Agents.Tests
git commit -m "feat(agents): run A2A agents in process and distributed"
```

---

### 任务 10：可观测性

**文件：**
- 创建：`src/server/Agw.Agents/ExternalAgents/A2A/A2AClientDiagnostics.cs`
- 修改：`src/server/Agw.Agents/Infrastructure/A2A/A2AAgentCardClient.cs`、`src/server/Agw.Agents.Execution/Agents/ExternalAgents/A2A/A2ARemoteAgent.cs`
- 测试：`tests/Agw.Agents.Tests/A2A/A2AClientDiagnosticsTests.cs`

**接口：**
- 产出：
  - `A2AClientDiagnostics.SourceName = "Agw.Agents.A2AClient"`，`ActivitySource` 与 `Meter` 使用这个名称
  - span：`a2a.client.card`、`a2a.client.turn`
  - 指标：`agw.a2a.client.turns`、`agw.a2a.client.turn.duration`（秒）、`agw.a2a.client.card_fetches`、`agw.a2a.client.resubscribes`
  - 标签：`outcome`（Turn：`completed`、`input_required`、`failed`、`interrupted`、`timeout`、`unauthorized`；card：`ok`、`fetch_failed`、`invalid`）、`protocol_binding`
  - span 的标签：`agw.agent.id`、`server.address`（只有主机名）、`a2a.protocol_binding`、`a2a.streaming`、`a2a.context_id`、`a2a.task_id`、`a2a.task_state`、`a2a.resubscribe_count`

- [ ] **步骤 1：编写失败的测试**

用 `ActivityListener` 与 `MeterListener` 订阅 `Agw.Agents.A2AClient`：
- `RunStreaming_Completed_RecordsTurnSpanAndCounter`：一个 `a2a.client.turn` span，`a2a.task_state` 为 `completed`；`agw.a2a.client.turns` 增加 1，`outcome = completed`。
- `RunStreaming_InputRequired_RecordsInputRequiredOutcome`。
- `RunStreaming_Unauthorized_RecordsUnauthorizedOutcome`。
- `RunStreaming_Resubscribed_RecordsResubscribeCounter`。
- `FetchAsync_InvalidCard_RecordsInvalidOutcome`。
- `RunStreaming_WithSecretHeaderAndQuery_TagsContainNoSecret`：header 值 `Bearer secret-1`，interface 地址带 `?key=secret-2`；所有 span 的标签值中没有 `secret-1` 与 `secret-2`。

- [ ] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*A2AClientDiagnosticsTests"`
预期：编译失败，`A2AClientDiagnostics` 不存在。

- [ ] **步骤 3：实现 span、指标与结构化日志**

日志字段见设计文档第 9 节，使用带语义占位符的模板。

- [ ] **步骤 4：运行测试，确认通过**

运行：`dotnet test --project tests/Agw.Agents.Tests --filter-class "*A2A*"`
预期：全部通过。

- [ ] **步骤 5：提交**

```bash
git add src/server/Agw.Agents src/server/Agw.Agents.Execution tests/Agw.Agents.Tests
git commit -m "feat(agents): add A2A client diagnostics"
```

---

### 任务 11：客户端

**文件：**
- 创建：`src/clients/packages/agents/src/ui-web/pages/agents/components/a2a-endpoint.ts`、`a2a-endpoint.test.ts`、`a2a-endpoint-fields.tsx`
- 修改：同目录的 `types.ts`、`agent-form-fields.tsx`、`agent-form-fields.test.ts`、`create-agent-dialog.tsx`、`edit-agent-dialog.tsx`、`copy-agent-dialog.tsx`、`agents-table.tsx`、`agents-table.test.ts`
- 修改：`src/clients/packages/agents/src/ui-web/pages/copy-requests.ts`、`copy-requests.test.ts`、`src/clients/packages/agents/src/types/agentflow.ts`
- 修改：`src/clients/packages/extension-sdk/src/api.ts`、`src/clients/desktop/renderer/src/runtime/extensions/extension-bridge-broker.ts`、`context-service.test.ts`
- 修改：`src/clients/packages/api/openapi.json`、`src/clients/packages/api/src/openapi.d.ts`

**接口：**
- 使用：任务 5 的 HTTP 接口
- 产出：
  - `ExternalAgentKind.A2A = 4`
  - `interface A2AEndpointDto { cardUrl: string; credentialHeaderNames: { origin: string; names: string[] }[]; interfaceUrl: string; protocolBinding: string; streaming: boolean; extra: { card: Record<string, unknown>; cardFetchedAt: string } }`
  - `interface A2AOriginCredentialsRequest { origin: string; headers: Record<string, string | null> }`
  - `interface A2AEndpointRequest { cardUrl?: string | null; credentials?: A2AOriginCredentialsRequest[] | null }`；`AgentCreateRequest.a2aEndpoint`、更新请求的 `a2aEndpoint`、`AgentDto.a2aEndpoint`
  - `interface A2ACredentialHeaderRow { origin: string; name: string; value: string; saved: boolean }`
  - `interface A2ACredentialGroup { origin: string; rows: A2ACredentialHeaderRow[] }`
  - `getA2ACardUrlError(cardUrl: string): string | null`
  - `normalizeA2AOrigin(uri: string): string | null`：返回 scheme、IDNA 小写 host 与 effective port 组成的规范 origin
  - `getA2ACredentialsError(groups: A2ACredentialGroup[]): string | null`
  - `toA2AEndpointRequest(cardUrl: string, groups: A2ACredentialGroup[]): A2AEndpointRequest`：saved header 的空值仅在同一 origin 下提交 null
  - `isA2AOriginWarningVisible(origin: string, rows: A2ACredentialHeaderRow[]): boolean`：该 origin 使用 `http` 且有 header 行时为 true
  - `A2AEndpointFields` 组件，内容见设计文档 11.1
  - `ContextTarget.externalAgentKind` 增加 `"a2a"`；`ENGINE_KINDS[4] = "a2a"`

- [ ] **步骤 1：更新 OpenAPI 类型**

按全局约束中的步骤启动 Host、下载 `openapi.json` 并运行 `pnpm gen:api`。

- [ ] **步骤 2：编写失败的测试**

`a2a-endpoint.test.ts`：
- `getA2ACardUrlError`：`https://a.example/.well-known/agent-card.json` 返回 null；`ftp://a.example/x`、`/x`、`https://u:p@a.example/x`、空字符串返回错误文字。
- `normalizeA2AOrigin`：scheme 与 host 大小写、默认 port 的不同写法得到相同 origin；非默认 port 保留；拒绝 path、query、fragment、userinfo 与非 HTTP scheme。
- `getA2ACredentialsError`：名称 `Host`、重复名称（大小写不同）、新增行的值为空、每组 21 行、超过 2 个 origin 返回错误文字；已保存且为空值的 header 仅在原 origin 下合法。
- `toA2AEndpointRequest`：card origin 与 interface origin 的 header 分别组成两组；已保存的 `Authorization` 提交 null，新加的 `X-Tenant` 提交具体值；两个 origin 的 header 不合并。
- `isA2AOriginWarningVisible`：`http` origin 加一行时为 true，`https` 为 false，`http` 没有行为 false。
- `credentialGroups_AfterPreview_CardAndInterfaceShareOriginUsesOneGroup`：同 origin 只显示一个凭据组。
- `credentialGroups_AfterPreview_InterfaceOriginChangesShowsIndependentGroup`：跨 origin 时显示独立的新组，不从 card origin 复制 header 名或值。

`agent-form-fields.test.ts` 增加：选择 `A2A` 后 Model provider、Extra Settings、Environment variables、Response schema 不显示，`A2AEndpointFields` 显示。

`agents-table.test.ts` 增加：`externalAgentKind` 为 4 的行显示 `A2A`。

`copy-requests.test.ts` 增加：A2A Agent 的复制数据含有 `cardUrl`、每个 origin 与 header 名称，所有 header 的值为空，`saved` 为 false。

`context-service.test.ts` 增加：`externalAgentKind` 为 4 的 Agent 得到 `"a2a"`。

- [ ] **步骤 3：运行测试，确认失败**

运行（在 `src/clients` 下）：`pnpm --filter @agw/agents exec tsx --test src/ui-web/pages/agents/components/a2a-endpoint.test.ts`
预期：失败，模块不存在。

- [ ] **步骤 4：实现**

- 创建时 preview 成功后，Display name 与 Description 为空则用 card 的 `name` 与 `description` 填入。
- 保存按钮不依赖 preview。
- preview 返回新的 interface origin 时，表单显示该 origin 的独立凭据组；同 origin 的 card 与 interface 共用一组。
- 复制 A2A Agent 时 `copy-agent-dialog.tsx` 显示 `A2AEndpointFields`，按 origin 展示 header 名称，由用户填写各组的值后提交创建请求。
- 错误消息使用 Server 返回的消息。

- [ ] **步骤 5：运行测试，确认通过**

运行（在 `src/clients` 下）：`pnpm exec turbo run typecheck test --filter=@agw/agents --filter=@agw/extension-sdk --filter=@agw/desktop`、`pnpm fmt:check`、`pnpm lint`
预期：全部通过。

- [ ] **步骤 6：在运行中的应用里核对**

按全局约束的方式启动 Standalone Host 与 `pnpm dev:web`，再启动 `.scratch/a2a-acceptance/` 下的两个 `A2A.AspNetCore` 回显 agent：card origin A 要求 `Authorization: Bearer card`，interface origin B 要求 `Authorization: Bearer interface`。在 Agents 页面创建 A2A Agent，核对：Fetch card 显示摘要与 B 的凭据组；保存后列表显示 `A2A`；编辑对话框只显示各 origin 的 header 名称；在 Chat 中选择它并得到回复，A 只收到 card 凭据，B 只收到 interface 凭据；修改 B 的 header 为错误值后发送得到 `5020011` 的错误消息。

- [ ] **步骤 7：提交**

```bash
git add src/clients
git commit -m "feat(agents): add A2A agent form"
```

---

### 任务 12：文档与回归

**文件：**
- 修改：`docs/2.Architecture.md`、`src/server/Agw.Agents.Execution/README.md`、`src/server/Agw.Agents.Execution/Runtimes/README.md`、`src/server/Agw.Agents.Execution/Persistence/README.md`

- [ ] **步骤 1：更新文档**

- `docs/2.Architecture.md`：Entity Relationships 中 `Agent.ExternalAgentKind` 一行加入 A2A，并增加 `Agent → AgentA2AEndpoint (A2A Agents only)`。
- `Agw.Agents.Execution/README.md`：External Agent 的小节加入 A2A Agent 的 Engine、会话保存方式（SDK session）、权限与输入能力；Distributed 的说明改为支持 System Agent 与 A2A Agent。
- `Runtimes/README.md` 与 `Persistence/README.md`：`DurableAgentSegmentRunner` 与 Agent SDK Session 的说明改为按 Engine 描述。

- [ ] **步骤 2：运行全部回归**

运行：`dotnet build Agw.slnx`、`dotnet test Agw.slnx`、`dotnet csharpier check .`；在 `src` 下运行 `pnpm test`。
预期：全部通过。

- [ ] **步骤 3：提交**

```bash
git add docs src/server
git commit -m "docs(agents): describe A2A remote agents"
```
