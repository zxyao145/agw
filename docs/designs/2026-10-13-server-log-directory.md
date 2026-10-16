# Server 日志目录统一方案

## 背景

[Issue #187](https://github.com/zxyao145/agw/issues/187) 记录了 macOS Desktop 0.4.1 随附 Server 在 launchd 下重复启动失败的问题：默认日志目录使用相对路径，进程从 `/` 启动时尝试创建 `/logs`，文件系统拒绝该操作。

日志属于 Server 的运行数据，其默认位置需要由当前用户的数据目录确定。使用默认目录配置时，通过终端、Desktop 或系统服务启动 Server，应得到一致的默认日志目录。

当前实现已经提供系统数据目录、`<AgwDataDir>/logs` 默认值和 macOS plist 的 `WorkingDirectory`。通用日志和错误日志的 File sink 仍分别配置固定目录，Serilog 也单独从 JSON 创建配置。本方案统一内置日志的目录和配置来源，并完成 Server 启动失败处理及发布包验收。

## 设计目标

### 功能目标

1. 默认日志位于各系统 Agw 数据目录下的 `logs` 子目录，并支持显式指定日志目录。
2. 通过明确的配置节点识别内置 File sink，统一目录并保留文件名、日志等级、格式和轮转策略。
3. 新建目录具有明确的权限，预先存在的目录保留当前用户设置的权限。
4. Server 启动失败时通过 stderr 提供明确的错误阶段和退出码。

### 技术目标

- 数据目录和日志目录在启动时解析为绝对路径，供目录创建、日志初始化和运行信息发布共同使用。
- 默认数据目录及基于绝对数据目录生成的默认日志目录与进程 cwd 无关。
- Unix 系统新建目录使用 `0700`；已经存在的目录保留 mode、ACL、属主和用户组。
- 日志初始化使用 Host 已加载的配置，保持配置优先级和日志参数一致。
- 启动异常统一写入 stderr，包含阶段、路径、异常类型和消息，退出码为 `1`。
- 测试使用真实的配置加载、Serilog、文件系统和子进程。测试产生的文件全部位于仓库内已忽略的 `.scratch` 目录。

## 数据库设计

本方案没有数据库结构变更。

## 详细设计

### 默认日志目录与显式配置

`AgwDataPaths` 统一负责目录解析。默认目录为：

| 系统    | `AgwDataDir` 默认值                                      | `AgwLogDir` 默认值                                    |
| ------- | -------------------------------------------------------- | ----------------------------------------------------- |
| macOS   | `~/Library/Application Support/Agw`                      | `~/Library/Application Support/Agw/logs`              |
| Linux   | 系统 XDG 数据目录下的 `agw`，通常为 `~/.local/share/agw` | `<AgwDataDir>/logs`，通常为 `~/.local/share/agw/logs` |
| Windows | `%LOCALAPPDATA%\Agw`                                     | `%LOCALAPPDATA%\Agw\logs`                             |

表中的 `~` 和 `%LOCALAPPDATA%` 用于说明平台目录；运行时使用系统目录 API 取得实际绝对路径。

配置规则如下：

| 配置项       | 默认行为                                                      | 显式配置行为         |
| ------------ | ------------------------------------------------------------- | -------------------- |
| `AgwDataDir` | 缺失、空字符串或空白字符串时使用系统应用数据目录              | 使用指定的数据根目录 |
| `AgwLogDir`  | 缺失、空字符串或空白字符串时使用 `Path.Combine(Root, "logs")` | 使用指定的日志目录   |

两个配置项支持 JSON 和命令行配置。沿用已有的 `~` 展开规则；显式相对路径按照进程 cwd 解析，服务部署使用绝对路径。目录在启动时确定，修改目录配置后需要重启 Server。

例如，自定义数据目录并使用默认日志目录：

```json
{
  "AgwDataDir": "/srv/agw"
}
```

对应日志目录为 `/srv/agw/logs`。需要独立日志目录时：

```json
{
  "AgwDataDir": "/srv/agw",
  "AgwLogDir": "/var/log/agw"
}
```

`AgwDataPaths.Resolve()` 已具备上述默认计算。实现阶段复用现有代码，通过回归测试确认行为。

### 内置 File sink 的统一初始化

在 `Agw.Host` 中提供 `public static class AgwLoggerFactory`，其公开方法 `CreateLogger(ConfigurationManager configuration, AgwDataPaths dataPaths, AgwHostProfile profile)` 返回 `Serilog.Core.Logger`。生产代码传入 `builder.Configuration`，测试创建真实的 `ConfigurationManager` 并直接调用该方法。

应用附带的 `Serilog:WriteTo` 使用命名对象，内置节点固定为：

| 配置节点                                     | 用途                                |
| -------------------------------------------- | ----------------------------------- |
| `Serilog:WriteTo:Console`                    | Console 的 Async 包装节点           |
| `Serilog:WriteTo:Files`                      | 通用日志和错误日志的 Async 包装节点 |
| `Serilog:WriteTo:Files:Args:configure:Info`  | 通用日志 File sink                  |
| `Serilog:WriteTo:Files:Args:configure:Error` | 错误日志 File sink                  |

`AgwLoggerFactory` 只为 `Info`、`Error` 两个命名节点计算路径，并在代码中注册 Host 日志。用户增加的其他 sink 按 Serilog 自身配置加载，其路径保持用户指定的含义。

三类内置文件日志统一使用 `dataPaths.LogsDirectory`：

| 日志      | 文件名模式                   | 写入与轮转参数                                                                        |
| --------- | ---------------------------- | ------------------------------------------------------------------------------------- |
| 通用日志  | `agw-info-.log`              | 使用有效日志等级；按小时轮转；保留 168 个文件；文件大小限制 2 GiB；达到大小限制时轮转 |
| 错误日志  | `agw-error-.log`             | 最低等级为 Error；按小时轮转；保留 168 个文件；文件大小限制 2 GiB；达到大小限制时轮转 |
| Host 日志 | `application-{profile}-.log` | `{profile}` 使用现有小写名称；按小时轮转；保留 30 个文件；保持一秒刷新间隔            |

File sink 的初始化过程为：

1. 在 Host 完成配置加载后读取上述命名节点，校验 `Files.Name` 为 `Async`、`Info.Name` 和 `Error.Name` 为 `File`。`WriteTo` 和内置 `Files.Args.configure` 使用非数字节点名；合并结果出现数字节点时，报告对应配置键并终止启动。
2. 校验 `Info` 和 `Error` 的 `Args:path` 为非空的单个文件名模式。拒绝绝对路径、目录分隔符、`.` 和 `..`，使内置日志的目录统一由 `AgwLogDir` 决定。错误使用 `AgwException(ErrorCodes.InvalidParam, message)` 报告，并指出具体配置键。
3. 通过 `Path.Combine(dataPaths.LogsDirectory, filename)` 生成两个绝对路径，并作为内存配置 Provider 加到同一个 `ConfigurationManager`。该 Provider 只覆盖两个 `Args:path` 键，其生命周期由 Host 管理。
4. 通过 `ReadFrom.Configuration(configuration)` 加载有效配置，保留 Console 输出、日志等级覆盖、格式、Enrichers 和现有 Async 参数。
5. Host 日志使用同一个 `dataPaths.LogsDirectory` 构造路径，注册其现有 File sink 参数。

内存覆盖基于合并后的有效配置执行。JSON 中节点的书写顺序、添加其他 sink，以及环境专用 JSON 或命令行对单个参数的覆盖，都不会改变内置 sink 的身份。JSON 由现有配置 Provider 解析，Serilog 配置由现有依赖加载。

随应用提供的配置文件统一采用命名节点。曾通过数组下标覆盖内置 sink 的部署，在升级时将覆盖配置迁移到上述命名节点；部署说明列出完整键名。此规则同时适用于环境专用 JSON 和命令行配置。

`Serilog.Settings.Configuration` 支持命名节点及嵌套的 Async/File 配置，相关接口见其[官方说明](https://github.com/serilog/serilog-settings-configuration/blob/dev/README.md)。

### 目录创建、权限与可写验证

`AgwDataPaths.EnsureCreated()` 使用创建时权限管理：Unix 系统通过带 `UnixFileMode` 的 `Directory.CreateDirectory` 创建目录，新建目录使用 `0700`；Windows 使用系统继承的访问控制。

已经存在的目录保留原有 mode、ACL、属主和用户组。该规则覆盖数据根目录及其子目录，因此当显式日志目录与已有数据目录重合时，也不会重复修改该目录的权限。

目录创建完成后，在 `LogsDirectory` 中以随机文件名执行一次真实的创建和写入检查，使用自动删除的文件句柄完成清理。检查失败直接进入启动失败处理。文件已经占据目录路径时，同样终止启动。

预先存在的目录由当前用户准备。Server 沿用目录已有权限，并通过写入检查确认可用。

### Server 启动与失败处理

外层启动异常处理覆盖命令参数、配置加载、目录解析、目录初始化、logger 创建、部署配置校验及 Host 启动。启动顺序为：

1. 识别开头的 `serve` 命令，将后续参数完整交给 Host 配置。支持 `serve --AgwLogDir=...` 和 `serve --AgwDataDir=...`。
2. 使用 `AppContext.BaseDirectory` 作为 `ContentRootPath`，加载程序附带的配置及资源。
3. 从 Host 配置解析一次 `AgwDataPaths`，完成已有数据迁移、目录创建和日志目录可写验证。
4. 在创建 logger 前将 Serilog SelfLog 接入 stderr，并记录构造期间的诊断；校验内置日志配置并创建 logger，使依赖内部的日志失败可以被观察到。
5. 完成部署配置校验和 Host 服务初始化，启动 HTTP 服务并发布运行信息。

每个步骤记录明确的 `stage`，例如 `Configuration`、`DataPaths`、`Directories`、`Logger`、`Deployment` 和 `HostStart`。发生启动异常时，始终通过 `Console.Error` 输出一条由 `System.Text.Json` 序列化的诊断记录，包含：

- `event: "server-startup-failed"`。
- `timestamp`：UTC 时间，采用 RFC 3339 格式。
- `stage`、`hostProfile` 和 `processId`：错误阶段、Host 角色及进程 ID。
- `path`：相关绝对路径；路径尚未解析成功时为 null。
- `exceptionType` 和 `message`：异常类型和消息。
- `exitCode: 1`。

启动失败固定以退出码 `1` 结束，HTTP 服务不会进入可用状态。logger 已创建时，同时记录 Fatal 日志并在退出时刷新、释放；logger 尚未创建时，stderr 诊断同样有效。正常关闭沿用正常退出行为。

构造 logger 期间的配置异常进入同一错误出口；同步产生的 SelfLog 构造错误也作为 logger 初始化失败处理。运行期间的异步日志失败经 SelfLog 写入 stderr。验收同时检查实际日志文件和诊断输出。

默认路径测试使用独立子进程设置 cwd，避免在并行测试进程中修改全局工作目录。子进程使用仓库内的绝对测试数据目录，并读取与生产相同的 Host 配置。

## 接口文档

本方案没有新增或修改 HTTP API。发布包验收使用已有 `GET /api/server-info`，确认实际 Server 返回 HTTP 200。启动失败诊断通过 Server 的 stderr 输出。

## 实施步骤

1. 在 `Agw.Shared.Tests` 中保留已有目录解析用例，补充权限与实际可写验证测试，并调整 `AgwDataPaths.EnsureCreated()` 的目录权限处理。
2. 在 `Agw.Host/Logging/AgwLoggerFactory.cs` 增加公开的日志创建函数，将应用附带的 File 配置改为命名节点，完成路径校验和统一配置。
3. 调整 `Agw.Host/Program.cs`，支持 `serve` 后的配置参数，接入 SelfLog，统一启动异常处理、stderr 诊断记录和退出码。
4. 构建后端，运行目录解析、当前用户目录权限、实际日志写入、文件轮转和启动异常测试；相关单元及集成测试全部通过后执行发布包验收。
5. 更新 `docs/4.Deployment.md`：默认示例只设置 `AgwDataDir`，独立日志示例使用绝对 `AgwLogDir`；说明命名 sink 配置、既有目录权限及启动失败输出。完成格式与最终变更检查。

## 测试计划

### 配置测试

| 覆盖状态与测试用例                                                              | 前置条件与操作                                                                         | 预期结果                                                       |
| ------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------- | -------------------------------------------------------------- |
| 已有：`ResolveFromConfiguration_LogDirectory_UsesConfiguredOrDataRootDirectory` | 保留 null、空字符串、空白字符串、显式相对日志目录用例；增加缺失键与绝对数据目录用例    | 默认日志目录为 `<AgwDataDir>/logs`，显式配置按现有路径规则解析 |
| 已有：`Resolve_LogDirectoryWithTilde_UsesUserHome`                              | 保留原有 `~` 展开的纯路径解析用例                                                      | 返回正确的绝对路径                                             |
| 新增：`ResolveFromConfiguration_JsonAndCommandLine_UsesEffectiveLogDirectory`   | JSON 和命令行指定不同的 `AgwLogDir`                                                    | 使用 Host 配置中最终生效的值                                   |
| 新增：`CreateLogger_NamedSinkOverrides_UsesEffectiveSettings`                   | 使用基础 JSON、环境专用 JSON、命令行依次覆盖命名 sink 的参数，并改变 JSON 节点书写顺序 | Info 和 Error 身份稳定，目录正确，其他有效参数继续生效         |
| 新增：`CreateLogger_AdditionalFileSink_PreservesConfiguredPath`                 | 在命名配置中增加一个使用独立测试路径的 File sink                                       | 额外 sink 使用其指定路径，内置 sink 使用 `AgwLogDir`           |
| 新增：`CreateLogger_InvalidBuiltInFileName_ThrowsInvalidParam`                  | 为内置 sink 配置绝对路径、目录分隔符、`.`、`..` 或空文件名                             | 构造 logger 前报告具体配置键及 `ErrorCodes.InvalidParam`       |
| 新增：`CreateLogger_NumericSinkKeys_ThrowsInvalidParam`                         | 将数组下标覆盖与命名配置合并                                                           | 启动配置校验失败，并指明需要迁移的键                           |

### 单元测试

在 `tests/Agw.Shared.Tests/Runtime/AgwDataPathsTests.cs` 中保留并扩展以下测试：

- 已有 `Resolve_WhenOverrideIsMissing_UsesSystemApplicationDataDirectory`：确认默认根目录和 `Root/logs`，补充绝对路径断言。
- 已有 `EnsureCreated_WhenCalledRepeatedly_PreservesExistingFiles`：保留文件内容、目录结构和新建目录权限断言。
- 新增 `Resolve_LogDirectoryContainsSpacesAndUnicode_PreservesPath`：检查包含空格、中文和 `&` 的目录。
- 新增 `EnsureCreated_ExistingDirectoryPermissions_PreservesPermissions`：由当前测试用户预建其拥有的 `0770` 日志目录并重复初始化，确认 mode、属主和用户组保持原值；覆盖日志目录与数据根目录重合的情况。
- 新增 `EnsureCreated_NewDirectory_UsesPrivatePermissions`：Unix 下检查新建目录为 `0700`。
- 新增 `EnsureCreated_LogDirectoryIsRegularFile_Throws`：使用普通文件占据目标目录位置，确认初始化失败且文件内容完整。
- 新增 `EnsureCreated_LogDirectoryWriteProbe_CleansUpProbeFile`：执行可写验证后确认检查文件已经清理。

### 集成测试

在 `tests/Agw.Host.Tests` 中直接调用公开的 `AgwLoggerFactory.CreateLogger()`。每个实例使用独立的真实 `ConfigurationManager`，并在使用结束后释放 logger 和配置对象。新增用例包括：

| 测试用例                                                        | 前置条件与操作                                                                                                          | 预期结果                                                                                                                  |
| --------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------- |
| `CreateLogger_DefaultLogDirectory_WritesBuiltInFiles`           | 加载应用附带的日志配置，使用绝对测试数据目录，创建生产日志实例，写入唯一的 Information 和 Error 标记，刷新并释放 logger | 三类日志均位于 `<AgwDataDir>/logs`；通用日志和 Host 日志包含两类标记；错误日志包含 Error 标记并过滤 Information 标记      |
| `CreateLogger_ExplicitLogDirectory_WritesBuiltInFiles`          | 指定独立日志目录，执行相同的真实日志写入                                                                                | 三类日志均位于显式指定的日志目录                                                                                          |
| `CreateLogger_HostProfiles_UsesExpectedFileNames`               | 分别使用 Standalone、ControlPlane 和 DataPlane profile，在独立目录中创建日志实例                                        | Host 日志使用相应的小写 profile 文件名，目录遵循同一规则                                                                  |
| `CreateLogger_ConfiguredLevelsAndFormats_PreservesSettings`     | 使用配置覆盖日志等级和输出格式，再写入不同等级的事件                                                                    | 过滤结果和输出格式符合有效配置                                                                                            |
| `CreateLogger_RollsAndRetainsFiles`                             | 使用一个日志实例和小文件大小限制触发轮转，分别验证完整事件写入及有限文件保留                                            | 轮转期间事件可读取；释放 logger 后文件数量符合配置；保留策略测试按其保留窗口检查事件                                      |
| `Start_RootWorkingDirectory_UsesDataRootLogs`                   | 在独立子进程中将 cwd 设为 `/`，使用实际 Host 配置及仓库内绝对测试数据目录，不设置 `AgwLogDir`                           | Server 成功启动，文件日志位于测试数据目录下的 `logs`                                                                      |
| `Start_ServeWithDirectoryArguments_UsesConfiguredDirectories`   | 使用 `serve` 加显式目录参数启动实际 Host                                                                                | 参数被正确加载，日志及运行信息位于指定目录                                                                                |
| `Start_LogDirectoryIsRegularFile_ReportsDirectoriesFailure`     | 使用普通文件占据日志目录位置，启动实际 Host 子进程                                                                      | 退出码准确为 `1`；stderr 含可解析的 `server-startup-failed` 记录，stage 为 `Directories`，包含目标路径及异常；HTTP 未就绪 |
| `Start_InvalidLoggerConfiguration_ReportsLoggerFailure`         | 使用无效的内置 logger 配置，以及一个会在 logger 构造期间报告 SelfLog 错误的配置                                         | 退出码为 `1`；诊断 stage 为 `Logger`；HTTP 未就绪                                                                         |
| `Start_InvalidDeploymentConfiguration_ReportsDeploymentFailure` | 目录和 logger 初始化成功后，使用无效部署配置启动                                                                        | 退出码为 `1`；stderr 诊断 stage 为 `Deployment`，同时存在相应 Fatal 文件日志                                              |
| `Start_InvalidJsonConfiguration_ReportsConfigurationFailure`    | 使用无法解析的应用 JSON 启动真实 Host 子进程                                                                            | 退出码为 `1`；stderr 含可解析的 `server-startup-failed` 记录，stage 为 `Configuration`；HTTP 未就绪                       |

日志测试调用生产日志创建入口，使用真实 File sink，等待异步日志完成刷新后检查文件内容。真实 Host 测试使用独立端口，结束后关闭子进程并清理本用例创建的目录。

启动诊断测试使用标准 JSON 解析器校验 `event`、`stage` 和 `exitCode` 等字段。断言明确的退出码与结构化事件，使测试能够验证设计中的统一错误出口。

涉及 `Log.Logger` 或 SelfLog 全局状态的启动失败测试在独立子进程中执行，分别捕获该进程的 stderr 和退出码。

### 回归测试

执行范围：

- `Agw.Shared.Tests`：目录解析、当前用户目录权限、可写验证及已有数据迁移测试。
- `Agw.Host.Tests`：Host 组合、配置、实际日志写入、文件轮转和启动失败测试。
- 对修改的 C# 文件执行 CSharpier。

相关单元、组件及集成测试全部通过后，执行部署验收。macOS 在独立测试环境中使用实际 full 发布包，Desktop 与 Server 使用同一用户运行：

1. 首次启动 Desktop，确认默认日志目录为 `~/Library/Application Support/Agw/logs`。
2. 通过 launchd 启动随包提供的 Server，确认 `/api/server-info` 返回 HTTP 200，进程持续运行且日志持续写入。
3. 重启 Desktop 和 Server，确认服务持续运行，默认日志目录保持一致。
4. 通过命令行使用独立的 `AgwLogDir` 启动发布 Server，确认三类内置日志均写入该目录。
5. 直接启动发布 Server，分别制造目录初始化失败、logger 配置失败及部署配置失败，检查 stderr 错误阶段和退出码。

文档验收检查 `docs/4.Deployment.md` 的默认示例、独立日志示例、命名配置键、目录权限说明及启动失败输出。验收完成需要具备日志写入、文件轮转和实际服务管理器启动的验证结果。
