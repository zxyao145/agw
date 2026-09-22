# 服务端架构审查与修复方案

审查日期：2026-09-22。基线：`41310555`，分支 `refactor/architecture-code-clean`，审查开始时工作区存在未提交的客户端与文档改动，服务端源码与基线一致。

## 审查范围与验证方式

覆盖 `src/server/` 全部 33 个项目目录（其中 `Agw.Sandbox`、`Agw.Sandbox.Launcher` 只剩构建输出，Git 未跟踪任何源码），排除 `obj`、`bin` 与 EF Migrations。

判定依据来自四类检查：`AGENTS.md`、[docs/rules.md](../rules.md)、[docs/2.Architecture.md](../2.Architecture.md)、[docs/3.Module Organization.md](../3.Module%20Organization.md) 声明的规则与实际代码的对照；`tests/Agw.Architecture.Tests` 守卫覆盖范围的阅读；项目引用图与源码 `using` 图的逐模块比对；单点符号的调用者枚举。

已运行 `dotnet test --project tests/Agw.Architecture.Tests`，49 项全部通过。`DateTime`、`new HttpClient()`、C# primary constructor、Controller 裸 `IActionResult` 四项全仓库扫描没有实例。[2026-09-21 审查](2026-09-21-architecture-duplication-review.md) 已记录并关闭的条目（中心 Infrastructure 项目、Controller 目录命名、`AgentRuntimeService` 构造与转发、JSON 序列化约定等）不再重复。

所有条目以基线源码为准，标注的行号对应上述基线。编号 AD 表示架构设计，RS 表示职责边界。每条给出证据、后果与完整修复方案。

## 架构设计

### ~~AD-01 · Files 模块维护独立于 `AgwException` 的错误体系，并把 `/api/files` 下的全部异常改写为 500~~

已修复（2026-09-23）。`AgwFilesException`、`FilesErrorCode`、`FileEndpointExceptionMappingMiddleware` 及其 Host 注册与测试已删除。`ErrorCodes` 新增 `FilePathOutsideRoot = 403_0005` 与 `FileOperationFailed = 500_0027`；`LocalFileSystem` 抛出 `AgwException`，`FilesController.MapError` 使用 `ErrorCodes.ResourceNotFound`、`InvalidParam`、`FileOperationFailed` 构造信封。`ExecutionHub` 与 `ToolInvocationExceptionHandler` 只处理 `AgwException`。新增 `tests/Agw.Files.Tests/FilesEndpointErrorEnvelopeTests.cs`，通过 TestServer 验证越界路径返回 403 信封、目录不可用返回 404 信封。

**证据：**

- [AgwFilesException.cs](../../src/server/Agw.Files/Exceptions/AgwFilesException.cs) 第 5 行直接继承 `Exception`，自带 `FilesErrorCode` 枚举与状态码映射。
- [FilesErrorCode.cs](../../src/server/Agw.Files/Exceptions/FilesErrorCode.cs) 第 3 至 9 行的四个数值中，`400_0001`、`404_0007` 与 [ErrorCodes.cs](../../src/server/Agw.Shared/Exceptions/ErrorCodes.cs) 第 7 行的 `InvalidParam`、第 381 行的 `ResourceNotFound` 重复；`FileOperationFailed = 500_0025` 与第 637 行的 `RemoteSkillConfigurationInvalid` 是同一个数字、不同含义。[docs/rules.md](../rules.md) 第 6 节要求错误码稳定且唯一。
- [Program.cs](../../src/server/Agw.Host/Program.cs) 第 428 行注册 `AgwApiExceptionMiddleware`，第 437 行在它内侧注册 `FileEndpointExceptionMappingMiddleware`。后者在 [FileEndpointExceptionMappingMiddleware.cs](../../src/server/Agw.Files/Api/FileEndpointExceptionMappingMiddleware.cs) 第 46 行捕获 `/api/files` 下的所有 `Exception`，除 `UnauthorizedAccessException` 映射为 403 外，第 77 行一律写出 500 与 `{ error, details }` 裸 JSON。
- [ProjectScopedFileSystemResolver.cs](../../src/server/Agw.Files/Infrastructure/Storage/ProjectScopedFileSystemResolver.cs) 第 84 行抛出的 `AgwException(ResourceNotFound)`，以及 [LocalFileSystem.cs](../../src/server/Agw.Files/Infrastructure/Storage/LocalFileSystem.cs) 第 42、52 行抛出的 `PathOutsideRoot`（枚举里定义为 403），到客户端都成为 500 且没有 Bens.Results 信封。[FileAppService.cs](../../src/server/Agw.Files/Application/Files/FileAppService.cs) 与 [FilesController.cs](../../src/server/Agw.Files/Api/FilesController.cs) 都没有 `catch`，`AgwApiExceptionMiddleware` 收不到这些异常。
- [ExecutionHub.cs](../../src/server/Agw.Agents.Execution/Inbound/SignalR/ExecutionHub.cs) 第 87 至 113 行因此为两种异常各写一份 `catch` 与 `CreateHubException` 重载。

**后果：** 外部 Project 的文件请求与越界路径请求都返回 500，客户端无法区分参数错误、权限错误与服务端故障；错误码数字与共享目录冲突，日志与前端按码检索会得到错误含义。

**修复：**

1. 删除 `AgwFilesException`、`FilesErrorCode`、`FileEndpointExceptionMappingMiddleware`，同时删除 `Program.cs` 第 437 行的注册、[FileEndpointExceptionMappingMiddlewareTests.cs](../../tests/Agw.Files.Tests/FileEndpointExceptionMappingMiddlewareTests.cs)，以及 [Agw.Files/README.zh-CN.md](../../src/server/Agw.Files/README.zh-CN.md) 第 19 行对中间件的引用。
2. 在 `ErrorCodes` 中新增 `403_0005 FilePathOutsideRoot` 与一个新的 `500_00xx FileOperationFailed`；参数错误与资源不存在复用 `InvalidParam`、`ResourceNotFound`。`LocalFileSystem` 改为抛出 `AgwException`。
3. `ExecutionHub` 只保留 `AgwException` 的一份 `catch`。
4. 在 `Agw.Files.Tests` 补一条端到端测试：外部 Project 的 `/api/files/list` 返回 404 信封，越界路径返回 403 信封。

### ~~AD-02 · `Agw.Setup` 的两条项目引用不成立~~

已修复（2026-09-23）。`IDatabaseBootstrapper.InitializeAsync` 只接收 `CancellationToken`；`Agw.Infrastructure.Data.DatabaseBootstrapper` 自行读取 `IOptionsMonitor<DatabaseSettings>` 并用 `DatabaseConnectionStringResolver` 解析连接字符串，与 `AgwDbContext` 注册使用同一份配置。`SetupInitializationService` 只依赖 `IInitializationStateStore`、`IDatabaseBootstrapper`、`IPasswordHasher<object>`；`Agw.Setup.csproj` 只保留 `Agw.Auth` 与 `Agw.Shared`。依赖矩阵、`docs/2.Architecture.md` 依赖图与 `tests/Agw.Setup.Tests` 的项目引用已同步。

**证据：**

- [Agw.Setup.csproj](../../src/server/Agw.Setup/Agw.Setup.csproj) 第 14 行引用 `Agw.Skills`，Setup 的全部源码中没有任何 `Skill` 标识符。
- 第 12 行引用 `Agw.Infrastructure`，唯一用途是 [SetupInitializationService.cs](../../src/server/Agw.Setup/Services/SetupInitializationService.cs) 第 1、16、23 行读取 `IOptionsMonitor<DatabaseSettings>`。`IDatabaseBootstrapper` 已经位于 `Agw.Shared/Contracts/Persistence`，`DatabaseProvider` 已经位于 `Agw.Shared/Configuration`，只有 `DatabaseSettings` 留在 `Agw.Infrastructure/Configuration`。

**后果：** 一个业务模块直接依赖技术适配层，[docs/2.Architecture.md](../2.Architecture.md) 依赖图中 `SETUP --> INFRA`、`SETUP --> SKILLS` 两条边把这个状态记录为设计。

**修复：**

1. 把 `DatabaseSettings` 移到 `Agw.Shared/Configuration/DatabaseSettings.cs`，与 `DatabaseProvider` 同目录；Infrastructure、Host、Setup 统一从该命名空间引用。
2. `Agw.Setup.csproj` 只保留 `Agw.Auth` 与 `Agw.Shared`。
3. 同步更新 [BackendArchitectureTests.cs](../../tests/Agw.Architecture.Tests/BackendArchitectureTests.cs) 的 `AllowedProjectDependencies["Agw.Setup"]` 与 `docs/2.Architecture.md` 依赖图。

### ~~AD-03 · `Agw.Projects` 引用具体模块 `Agw.Integrations`、`Agw.Skills`，实际只使用它们的 Contracts 程序集~~

已修复（2026-09-23）。`Agw.Projects.csproj` 改为引用 `Agw.Integrations.Contracts` 与 `Agw.Skills.Contracts`；依赖矩阵与 `docs/2.Architecture.md` 依赖图同步，依赖图补充了 `Agw.Integrations.Contracts`、`Agw.Providers.Contracts`、`Agw.Skills.Contracts` 节点及其现有引用边。

**证据：** [Agw.Projects.csproj](../../src/server/Agw.Projects/Agw.Projects.csproj) 第 10、12 行。整个 Projects 模块对这两个模块的引用只有 [ProjectAppService.cs](../../src/server/Agw.Projects/Application/ProjectAppService.cs) 第 5 行的 `Agw.Integrations.Contracts.References` 与第 11 行的 `Agw.Skills.Contracts.References`，两个命名空间分别位于 `Agw.Integrations.Contracts` 与 `Agw.Skills.Contracts` 项目。审查时用替换为 Contracts 引用的 csproj 编译 `Agw.Projects`，构建成功，随后已恢复 csproj。

**后果：** Projects 在编译图上携带 Integrations 的 OAuth/MCP 与 Skills 的脚本执行代码；依赖矩阵允许的范围大于实际需要，后续任何越界引用都不会被守卫发现。

**修复：** `Agw.Projects.csproj` 改为引用 `Agw.Integrations.Contracts` 与 `Agw.Skills.Contracts`；更新依赖矩阵 `["Agw.Projects"]` 与 `docs/2.Architecture.md` 中 `PROJECTS --> INTEGRATIONS`、`PROJECTS --> SKILLS` 两条边。

### AD-04 · `IocUtil` 是没有任何调用者的静态 Service Locator

处理方式（2026-09-23）：为 `IocUtil` 添加 `[Obsolete]` 注解，类与 `Program.cs` 的两处注册保留；两处使用产生 CS0618 警告。

**证据：** [IocUtil.cs](../../src/server/Agw.Shared/Utils/IocUtil.cs) 通过静态属性暴露 `IServiceProvider` 与 `ILoggerFactory`；[Program.cs](../../src/server/Agw.Host/Program.cs) 第 284 行注册、第 381 行解析它以触发静态赋值；全仓库没有任何 `IocUtil.` 调用。`ErrorCodes.LoggerFactoryNotSet`、`ServiceProviderNotSet` 只被这个类使用。

**后果：** 共享层保留了一个可以绕过构造函数注入的全局入口，与显式构造函数注入的编码规则相反。

**修复：** 删除 `IocUtil.cs` 与 `Program.cs` 的两处引用。两个错误码按规则保留在目录中，不重新编号。

### ~~AD-05 · `Agw.<Module>.Contracts` 命名空间同时存在于 Contracts 程序集与具体程序集，守卫无法区分~~

已修复（2026-09-23）。`BackendArchitectureTests` 新增 `ModuleSource_ReferencingSiblingOwnerLocalContractsNamespace_HasNoViolations`：对存在独立 `*.Contracts` 项目的模块，引用的 `Agw.<Module>.Contracts.*` 命名空间若只在具体程序集内声明，则跨模块引用视为违规；`AllowedOwnerLocalContractsConsumers` 固定了 `Agw.Skills.Contracts.Registration`（Agents、Jobs）、`Agw.Skills.Contracts.Remote`（Agents）、`Agw.Integrations.Contracts.Capabilities`（Agents）三条可执行扩展注册例外，`Agw.Infrastructure` 维持现有豁免。根命名空间 `Agw.Projects.Contracts` 由两个程序集共同声明，其中的 `IProjectAppService`、`ITaskAppService`、`ITaskSessionBindingService` 继续由既有的类型名守卫覆盖。守卫已通过临时注入 `using Agw.Providers.Contracts.Manager;` 验证能够定位到文件与行号。

**证据：** Projects、Jobs、Skills、Integrations、Providers 五个模块的 `Contracts/` 目录都声明 `Agw.<Module>.Contracts.*` 命名空间，例如 [ProviderRequests.cs](../../src/server/Agw.Providers/Contracts/Manager/ProviderRequests.cs)、[IAgentSkillRegistration.cs](../../src/server/Agw.Skills/Contracts/Registration/IAgentSkillRegistration.cs)，与独立的 `Agw.<Module>.Contracts` 项目共用前缀。[BackendArchitectureTests.cs](../../tests/Agw.Architecture.Tests/BackendArchitectureTests.cs) 第 245 行的 `ModuleSource_ReferencingSiblingInternalLayer_HasNoViolations` 经第 605 行的 `ContainsInternalLayer` 只按 `Application`、`Domain`、`Infrastructure` 路径段判定，任何模块导入 `Agw.Providers.Contracts.Manager` 这类 HTTP DTO 都会被放行。当前没有越界实例（`Agw.Agents.Execution` 与 `Agw.Infrastructure` 的使用在依赖矩阵允许范围内）。

**后果：** [docs/3.Module Organization.md](../3.Module%20Organization.md) 声明"HTTP DTO 与可执行扩展注册保持所有者本地"，这条边界没有守卫。

**修复：** 在该测试中使用已经构建好的 `namespaceOwners`（命名空间到声明项目的映射）判定：当命名空间前缀属于存在独立 `*.Contracts` 项目的模块，而实际声明位于具体程序集内时，视为内部命名空间，跨模块引用即报告违规；`Agw.Agents.Execution` 引用 `Agw.Agents`、`Agw.Infrastructure` 实现各模块 seam 两种情况维持现有豁免。

## 职责边界

### ~~RS-01 · Settings 模块的 DI 组合点位于 `Agw.Infrastructure`~~

已修复（2026-09-23）。`Agw.Host/Program.cs` 的模块组合链显式调用 `.AddSettings()`；`Agw.Infrastructure/DependencyInjection.cs` 只保留 `ISettingsPersistence`、`ISettingsDbContext` 的适配器注册。`EfSettingsPersistence` 的构造函数参数改为 `ISettingsDbContext`，实体状态通过 `Settings.Entry(setting)` 处理。`BackendArchitectureTests` 新增两条守卫：`InfrastructureSource_DoesNotComposeModuleRegistrations` 禁止 `Agw.Infrastructure` 源码调用任何模块的 `Add<Module>()`；`ModuleDbContextSeams_HaveConsumersBeyondRegistration` 要求每个 `I<Module>DbContext` 在声明、`AgwDbContext` 与 Infrastructure DI 之外至少有一个使用者。手工组合 Infrastructure 的测试（`SetupInitializationIntegrationTests`、`HostModuleCompositionTests`、`SettingsStoreTests`、`DatabaseInitializationStateStoreTests`）同步补齐 `AddSettings()` 或 `ISettingsDbContext` 注册。

**证据：** [Agw.Infrastructure/DependencyInjection.cs](../../src/server/Agw.Infrastructure/DependencyInjection.cs) 第 115 行调用 `services.AddSettings()`；[Program.cs](../../src/server/Agw.Host/Program.cs) 第 324 至 354 行的模块组合链没有 `AddSettings`。第 117 行注册的 `ISettingsDbContext`，全仓库除 `AgwDbContext` 实现外没有任何消费者，[EfSettingsPersistence.cs](../../src/server/Agw.Infrastructure/Settings/EfSettingsPersistence.cs) 注入的是具体的 `AgwDbContext`。

**后果：** `AGENTS.md` 要求模块在自己的 DI seam 注册、由 Host 组合；Settings 是唯一由技术适配层决定是否启用的模块。

**修复：**

1. Host 组合链中显式加入 `.AddSettings()`，Infrastructure 只保留 `ISettingsPersistence` 的实现注册。
2. `EfSettingsPersistence` 的构造函数参数改为 `ISettingsDbContext`，使接缝有真实使用者；`PersistenceOwnershipArchitectureTests.RawPersistenceAccessInventory` 中该文件的原始访问计数同步归零。

### ~~RS-02 · Host 的 OpenAPI 规则内嵌了 Agents 模块 DTO 的字段语义~~

已修复（2026-09-23）。schema 规则移入 `Agw.Host/OpenApi/AgwOpenApiSchemaTransformer.cs`，只保留与模块无关的三条：int 格式、`ToolValueObject`/`ToolDefinition` 的多态判别字段、值类型与 `string` 属性按 `JsonPropertyInfo.IsGetNullable` 决定 required。`string?` 与 `Guid?` 因此按声明成为可选，`AgentUpdateRequest`、`AgentCreateRequest` 的两条专用规则随之消失。新增 `tests/Agw.Host.Tests/OpenApiSchemaTransformerTests.cs`，通过 TestServer 生成 Agents Controller 的 OpenAPI 文档并断言这两个 schema 的 required 集合。从开发服务器重新导出 `src/clients/packages/api/openapi.json` 并运行 `pnpm gen:api`：与之前的导出相比只有 `required` 数组变化，共 90 个 schema 移除了 `string?` 属性（含所有 `ApiResult*` 的 `contentType`、`detail`），`openapi.d.ts` 对应 134 处属性改为可选；Web、Desktop 与全部业务包 `typecheck` 通过。

**证据：** [Program.cs](../../src/server/Agw.Host/Program.cs) 第 246 至 275 行的 schema transformer 对 `AgentUpdateRequest` 清空全部 required，对 `AgentCreateRequest` 移除 `extra` 的 required。

**后果：** Hosting 模块知道两个模块 DTO 的可选字段规则；DTO 新增字段时需要同时修改 Host。

**修复：** 把可选性表达在 DTO 声明本身。`AgentUpdateRequest` 的属性全部声明为可空类型，`AgentCreateRequest.Extra` 声明为可空；`Program.cs` 只保留与具体模块无关的通用规则（int 格式、`ToolValueObject`、`ToolDefinition` 的多态判别字段）。运行 `pnpm gen:api` 后 `openapi.d.ts` 应保持不变，以此验证等价。

## 与上一次审查的关系

[2026-09-21 审查](2026-09-21-architecture-duplication-review.md) 关闭了中心 Infrastructure 项目、目录命名、运行时服务组合等条目，本次不再涉及。本次 AD-05 与该文档的 AD-01、AD-02 属于同一类：问题位于守卫规则的字面覆盖之外。AD-02、AD-03 位于依赖矩阵允许范围内，矩阵把实际不需要的引用记录为设计，因此守卫通过与边界成立是两件事。
