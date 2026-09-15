---
title: "配置与认证"
description: "了解各项设置的用途、默认值，以及如何配置服务和登录认证。"
weight: 30
lastmod: 2026-09-15
translationKey: docs/operations/configuration
---

本页说明 AGW Server 的运行设置：服务地址、数据存放位置、任务执行方式、日志和登录认证。模型、Agent、Project 和集成账号则在管理界面中设置。修改本页配置后，请重启相应的 Server 程序。

初次在本机使用时，大多数设置可以保留默认值。需要远程访问时，重点检查服务地址、客户端来源和代理设置；需要分离部署时，再配置 PostgreSQL 和 Distributed 模式。分布式执行的轮询、批量写入等参数，通常可以先保持默认值。

## 先看与当前任务有关的设置

只在本机开始使用时，可以先保留默认配置并完成初始化。准备长期运行前，确认数据库和数据目录在哪里，按[备份指南]({{< relref "/docs/operations/backup" >}})保存数据。

远程访问遇到问题时，优先检查“服务地址与数据目录”“初始化、来源与反向代理”和“认证与 Token”。分离部署则先看两端的必需配置；后面的轮询和批量参数是调优参考，无需在首次部署时逐一修改。

## 配置方法与优先级

同一项设置可以写在配置文件、环境变量或启动命令中。如果多处都设置了它，后面的来源优先：

程序默认值 → `appsettings.json` → 环境专用 JSON → 开发环境的 Secrets（机密配置）→ 环境变量 → 命令行。

例如，文件中设置为 SQLite，启动命令中指定 PostgreSQL，最终会使用 PostgreSQL；但连接字符串不会随之自动更换，需要一起修改。配置文件位于 Server 程序目录。日志工具 Serilog 的读取方式有所不同，见下方日志配置。

本页用冒号表示配置的分组，例如 `Database:Provider` 表示 Database 分组中的 Provider。它在环境变量中写成 `Database__Provider`，在启动命令中写成 `--Database:Provider postgres`。开关使用 `true`（开启）或 `false`（关闭）；需要从几个选项中选择时，请使用表格列出的名称。

表格中的“`appsettings.json`”指 Server 自带的 `appsettings.json`；“未设置时”指文件、环境变量和命令行中都没有这一项。两种情况下的默认值若有不同，会分别说明。

## 按部署方式选择配置

先选择部署方式，再查对应的配置组。**部署方式**决定启动哪些 Server 程序，**执行模式**决定任务如何运行，两者需要分别选择。

| 配置组 | 什么时候需要看 |
| --- | --- |
| 通用配置 | 所有部署都需要了解：服务地址、数据目录、数据库、认证和日志等 |
| Standalone 单机部署 | 一个 Server 同时负责管理、调度和执行；默认使用 SQLite 和 InProcess |
| Control/Data Plane 分离部署 | 控制面负责管理调度，数据面负责执行；必须使用 PostgreSQL 和 Distributed |
| Distributed 执行调优 | 使用 Distributed 时再看：包括分离部署，也包括主动启用 Distributed 的 Standalone |

## Standalone 单机部署

本机试用或单台 Server 部署，可以先保留下面的默认组合，通常无需填写这些配置：

| 完整配置名 | 默认选择 | 含义 |
| --- | --- | --- |
| `Database:Provider` | `sqlite` | 使用本地数据库文件 |
| `Database:ConnectionString` | `Data Source=agw.db` | SQLite 文件位置 |
| `Execution:Provider` | `InProcess` | 由当前 Server 直接运行任务 |
| `DistributedLock:Provider` | 未设置 | 随 SQLite 自动使用进程内锁 |
| `DistributedLock:ConnectionString` | 空 | 进程内锁无需连接数据库 |

Standalone 也可以使用 PostgreSQL，而继续保留 InProcess。若选择 Distributed，则需要同时满足下一节的 PostgreSQL 数据库和锁要求，并按分布式执行方式配置。单机部署方法见[单机与 Docker 部署]({{< relref "/docs/operations/standalone" >}})。

## Control/Data Plane 分离部署

两端必须使用同一套业务数据库，并采用下面的组合。先完成 Control Plane 初始化，再启动 Data Plane。

| 完整配置名 | 必需设置 | 配置在哪一端 |
| --- | --- | --- |
| `Database:Provider` | `postgres` | 两端 |
| `Database:ConnectionString` | 指向同一个 PostgreSQL 数据库 | 两端 |
| `Execution:Provider` | `Distributed` | 两端 |
| `DistributedLock:Provider` | `postgres`，或不填以跟随数据库 | 两端 |
| `DistributedLock:ConnectionString` | 留空复用数据库连接，或指向相同的锁服务 | 两端保持一致 |

通用配置也要按各自职责填写：

- **Control Plane**：配置初始化密码、管理页面使用的地址，以及集成 OAuth 的公开地址。
- **Data Plane**：准备 Agent 所需的 CLI、Shell、工作目录和文件。工作节点的并发数、检查间隔在这一端影响实际执行。
- **两端共同检查**：各自的监听地址、客户端来源和代理、日志与监控；需要解密共享数据的节点应使用一致的加密密钥。所有执行节点都必须能访问任务使用的工作目录。

### 执行模式

下表配置项的完整名称都以 `Execution:` 开头。

| 配置项 | 默认值 | 用途与可选值 |
| --- | --- | --- |
| `Provider` | InProcess | `InProcess`：由当前 Server 直接运行任务，适合简单的单机部署。`Distributed`：将执行状态保存到 PostgreSQL，由负责执行的 Server 领取并运行任务，支持多个执行节点协作。选择 Distributed 时，数据库和分布式锁都必须使用 PostgreSQL。 |

这里的 Host 就是运行中的 Server 程序。启动 Standalone 时，一个程序同时负责管理、调度和执行；分离部署时，Control Plane 负责管理和调度，Data Plane 负责执行。分离部署的两端都需要设置 PostgreSQL 数据库、Distributed 执行模式和 PostgreSQL 锁。请按部署方式启动对应程序，Execution:Provider 只决定任务如何执行。

### 分布式锁

多台 Server 协作时，需要确认“这项工作现在由谁处理”。锁用来保证同一时刻只有一个执行者取得这项工作的处理权，避免相互冲突。

下表配置项的完整名称都以 `DistributedLock:` 开头。

| 配置项 | 默认值 | 用途与可选值 |
| --- | --- | --- |
| `Provider` | 未指定，跟随数据库 | `inmemory`：在当前 Server 内记录谁正在执行，适合单节点。`postgres`：让多个 Server 通过 PostgreSQL 共同确认执行权。未设置或填 null 时会自动选择：SQLite 使用 inmemory，PostgreSQL 使用 postgres。 |
| `ConnectionString` | 空 | postgres 锁使用的连接字符串；空值复用 `Database:ConnectionString`。inmemory 不使用连接字符串。 |

## Distributed 执行调优

下面的设置用于 Distributed 执行模式。分离部署需要使用它；Standalone 只有启用 Distributed 后才需要关注。先保留默认值，出现明确的性能问题后再逐项调整。

### 分布式工作节点

工作节点是负责实际运行任务的 Server。下面这些设置决定它多久检查新任务、同时处理多少任务，以及何时检查中断的任务。

下表配置项的完整名称都以 `Execution:Distributed:` 开头。

| 配置项 | 默认值 | 用途与可选值 |
| --- | --- | --- |
| `WorkerPollingMilliseconds` | 250 | 每隔多久检查一次是否有新任务，单位毫秒。默认 250 毫秒，即每秒约检查 4 次。调小后可能更快开始任务，但数据库也会更忙。 |
| `MaxConcurrentExecutions` | 4 | 每个执行 Server 最多同时处理多少次任务。默认 4，表示这一台 Server 最多同时运行 4 次任务；部署多台时，每台分别计算。 |
| `RecoveryProbeSeconds` | 30 | 一项标记为运行中的任务，状态多久没有更新后，可以检查是否需要接手恢复，单位秒。默认 30 秒。检查时仍会确认原执行者是否持有任务锁；正常运行的长任务不会仅因超过 30 秒就被中断或重复执行。 |
| `LockAcquireTimeoutMilliseconds` | 500 | 领取任务前，最多等待多久来确认自己拥有独占执行权，单位毫秒。默认 500 毫秒。这样可以避免多个 Server 同时运行同一次任务。 |

只有选择 Distributed 模式时才需要关注这些设置。所有数值都必须是大于 0 的整数；没有明确的性能问题时，建议先使用默认值。

### 执行事件与回放

Agent 执行时会不断产生回复和状态消息。系统保存这些消息后，客户端重新连接时可以补读执行过程，这就是“回放”。这里设置消息保存在哪里，以及读写的频率。

下表配置项的完整名称都以 `Execution:Distributed:EventStream:` 开头。

| 配置项 | 默认值 | 用途与可选值 |
| --- | --- | --- |
| `Provider` | Postgres | `Postgres`：把执行过程中的消息保存在 PostgreSQL 中，无需额外安装 Redis。`Redis`：使用 Redis Stream 保存这些消息。即使选择 Redis，任务状态和分布式锁仍需要 PostgreSQL。 |
| `ReadPollingMilliseconds` | 250 | 暂时没有新消息时，隔多久再检查一次，单位毫秒，必须大于 0。 |
| `ReadBatchSize` | 100 | 一次最多读取多少条执行消息，必须大于 0。 |
| `WriteIntervalMilliseconds` | 250 | 收到第一条待保存消息后，最多等待多久把消息一起写入，单位毫秒。默认 250；填 `0` 表示立即写入，不能为负数。 |
| `WriteBatchSize` | 100 | 待保存消息达到多少条时，就触发一次批量写入，必须大于 0。默认积累到 100 条时写入。 |
| `Redis:ConnectionString` | 空 | 选择 Redis 时必填，所有相关 Server 使用相同 Redis 服务，例如 `redis:6379,password=...`。 |
| `Redis:StreamTtlMinutes` | 1440 | 执行消息在 Redis 中保留多久，单位分钟。默认 1440 分钟，即 24 小时；必须大于 0。过期后，客户端无法再从这里补读这些消息。 |

## 通用配置

以下设置适用于两种部署方式。分离部署时，根据每个 Server 承担的职责配置相应项目。

### 服务地址与数据目录

| 配置项 | 默认值 | 用途与可选值 |
| --- | --- | --- |
| `ASPNETCORE_URLS / --urls` | 本地默认端口 30816；容器由运行配置指定 | Server 接收请求的地址。用 `--urls http://127.0.0.1:30816` 仅供本机访问，或按部署需要绑定其他地址。多个地址用分号分隔。 |
| `ASPNETCORE_ENVIRONMENT` | Production | 选择环境专用 JSON，例如 `appsettings.Production.json`。常用名称为 Development（开发）、Staging（预发布）、Production（生产）；这是环境名称，不是只接受三个值的枚举。 |
| `AgwDataDir` | ~/agw | AGW 数据根目录，包含运行数据、Skills 和加密密钥等。支持 `~`；相对路径以启动程序时的工作目录为起点。 |
| `AGW_DATA_DIR` | 未设置 | 数据目录的环境变量别名。在环境变量这一层与 `AgwDataDir` 同时存在时，以 `AgwDataDir` 为准；命令行仍可覆盖。 |
| `AgwLogDir` | ./logs | 独立的日志目录，不随数据目录移动；支持 `~`，相对路径从工作目录解析。 |
| `AllowedHosts` | * | 允许用哪些域名访问 Server。`*` 表示不限制；也可以填写 `agw.example.com;localhost` 这样的域名列表，用分号分隔。客户端从哪个页面连接，则由 AllowedOrigins 设置。 |

### 数据库

下表配置项的完整名称都以 `Database:` 开头。

| 配置项 | 默认值 | 用途与可选值 |
| --- | --- | --- |
| `Provider` | sqlite | `sqlite`：本地 SQLite 文件，适合单机；`postgres`：PostgreSQL 服务，支持分离和分布式部署。只有这两种值。 |
| `ConnectionString` | Data Source=agw.db | 所选数据库的连接字符串。SQLite 使用 `Data Source=...`；PostgreSQL 使用 `Host=...;Port=5432;Database=...;Username=...;Password=...`，Host 不能为空。切换 Provider 时必须一起修改。 |

### 初始化、来源与反向代理

| 配置项 | 默认值 | 用途与可选值 |
| --- | --- | --- |
| `Setup:AdminPassword` | 未设置 | 首次初始化的管理员密码，8–256 个字符。通过环境或 Secrets 注入可完成无人值守初始化；已有认证配置时不会覆盖密码。分离部署在 Control Plane 初始化。 |
| `Auth:AllowedOrigins` | agw://app、http://localhost:3000、http://127.0.0.1:3000 | 允许哪些客户端地址连接 Server。Origin 是地址的“协议 + 主机名 + 端口”，例如 `http://localhost:3000`。请填写实际地址，协议和端口都要一致；完全未设置时列表为空。 |
| `ReverseProxy:TrustedProxies` | `appsettings.json`含 127.0.0.1、172.16.0.0/12、10.0.0.0/8 | 如果请求经过反向代理，填写代理的实际 IP，让 Server 能识别原始访问地址和 HTTP/HTTPS 协议。当前只接受单个 IP；`10.0.0.0/8` 这样的网段写法不会生效。程序读取 X-Forwarded-For、X-Forwarded-Host、X-Forwarded-Proto，最多处理一层转发。 |

数组通过数字下标配置，例如 `Auth__AllowedOrigins__0=agw://app`。配置覆盖按下标合并；只覆盖第 0 项不会自动删除`appsettings.json`中的第 1、2 项，需要完整核对最终列表。

### 集成 OAuth 地址

下表配置项的完整名称都以 `Integrations:OAuth:` 开头。

| 配置项 | 默认值 | 用途与可选值 |
| --- | --- | --- |
| `PublicBaseUrl` | http://localhost:30816 | 用户在 GitHub 等服务中完成授权后，浏览器返回 AGW Server 时使用的地址。程序会在它后面加上 `api/integrations/oauth/callback`。远程部署时，请填写浏览器实际能访问的 Server 地址。 |
| `WebBaseUrl` | http://localhost:3001 | 授权流程结束后，用户返回 AGW 页面时使用的地址。请填写实际打开 Web 界面的地址。 |

两项都填写以 `http://` 或 `https://` 开头的完整地址，不要附带用户名、密码、`?` 后的查询参数或 `#` 后的内容。未设置或留空时，使用当前请求的基础地址。

### 对话历史写入

下表配置项的完整名称都以 `ConversationHistory:` 开头。

| 配置项 | 默认值 | 用途与可选值 |
| --- | --- | --- |
| `Mode` | Interval | `Immediate`：有需要保存的内容就立即写入数据库。`Interval`：先暂存在内存中，隔一段时间一起保存。`TurnEnd`：主要等这一回合结束后再保存。暂存内容达到大小上限时，也会提前保存。 |
| `FlushIntervalSeconds` | 10 秒 | Server 自带的 `appsettings.json` 设为 10 秒；如果所有配置来源都没有设置这一项，程序使用 5 秒。Interval 模式下，隔多少秒把暂存内容保存到数据库。必须大于 0，且不能超过程序计时器支持的范围。 |
| `MaxBufferedBytes` | 16777216（16 MiB） | 允许暂存在内存中的内容大小，单位字节。默认 16 MiB；达到上限会提前保存到数据库，必须大于 0。 |

这些设置决定聊天记录什么时候保存到数据库，页面仍可实时显示回复。先暂存、后保存可以减少数据库写入，但程序意外退出时，尚未保存的部分可能丢失。如果更看重记录及时保存，可以选择 Immediate。

### Shell 工具

下表配置项的完整名称都以 `Agents:Shell:` 开头。

| 配置项 | 默认值 | 用途与可选值 |
| --- | --- | --- |
| `Backend` | local | `local`：直接在运行 Agent 的主机上执行命令，使用项目工作目录。`docker`：在 Docker 容器中执行命令，需要主机已安装并能使用 Docker。容器中的项目目录为 `/workspace`，附加目录为 `/project-directories/{id}`；当前容器禁用网络，命令超时为 30 秒。 |

此项只选择 AGW Shell 工具的执行后端，不改变整个 Server 的部署模式，也不为外部 Agent 安装 CLI。

### OpenTelemetry

OpenTelemetry 用于把运行指标和调用追踪等信息发送到监控系统，帮助排查慢请求和错误。已有监控服务时，填写它的接收地址；刚开始使用时，先了解下面的默认行为即可。

下表配置项的完整名称都以 `OpenTelemetry:` 开头。

| 配置项 | 默认值 | 用途与可选值 |
| --- | --- | --- |
| `ServiceName` | `appsettings.json` Agw | 监控系统中显示的服务名称。分离 Host 遇到`appsettings.json`值 Agw 时会改为 `Agw.ControlPlane` 或 `Agw.DataPlane`；未设置时使用 `Agw.{Host角色}`。 |
| `ServiceVersion` | 1.0.0 | 监控系统中显示的版本号，方便区分不同版本的运行情况。 |
| `OtlpEndpoint` | `appsettings.json`空；实际回退 http://localhost:4317 | 接收运行指标、调用追踪等数据的监控服务地址。留空或不填时，仍会尝试发送到 `http://localhost:4317`；留空不会关闭发送。 |

### 日志配置

日志记录 Server 运行中发生的事情。级别越详细，越有利于排查问题，但产生的日志也越多。下面保留完整配置名，日常调整通常只需关注日志级别和保存目录。

| 配置项 | 默认值 | 用途与可选值 |
| --- | --- | --- |
| `Logging:LogLevel:Default` | Information | Microsoft 日志默认记录到什么详细程度；可用 `Logging:LogLevel:{类别}` 单独调整某个模块。 |
| `Logging:LogLevel:Microsoft.AspNetCore` | Warning | Web 请求处理相关日志的详细程度。 |
| `Logging:LogLevel:Microsoft.EntityFrameworkCore` | Warning | 数据库访问相关日志的详细程度。 |
| `Serilog:Using` | Console、File、Async sinks | 启用控制台、文件和异步输出功能所需的日志组件。一般无需修改。 |
| `Serilog:MinimumLevel:Default` | Debug | Serilog 默认保留的最低日志级别。低于该级别的日志不会输出。 |
| `Serilog:MinimumLevel:Override:Microsoft.AspNetCore` | Warning | 单独设置 Web 请求相关日志的最低级别。 |
| `Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore` | Warning | 单独设置数据库访问日志的最低级别。 |
| `Serilog:MinimumLevel:Override:System` | Warning | 单独设置 System 系统组件日志的最低级别；其他类别可按同样方式设置。 |
| `Serilog:WriteTo:0:Name` | Async | 让日志在后台输出，减少写日志对请求处理的影响。一般保留 Async。 |
| `Serilog:WriteTo:0:Args:configure:0:Name` | Console | 将日志输出到运行 Server 的终端或容器日志中。 |
| `Serilog:WriteTo:0:Args:configure:0:Args:outputTemplate` | 见下方 | 控制台行格式，包含时间、级别、来源、TraceId、SpanId、线程、消息与异常。 |
| `Serilog:Enrich` | FromLogContext、WithMachineName、WithThreadId、WithOpenTelemetryTraceId、WithOpenTelemetrySpanId | 给日志补充主机名、线程和请求追踪信息，方便把同一次操作的记录联系起来。 |

当前 Host 的 Serilog 配置从 `appsettings.json` 及 `appsettings.{ASPNETCORE_ENVIRONMENT}.json` 单独读取；不能假设 `Serilog__...` 环境变量会覆盖这条管线。修改日志输出或级别时编辑相应 JSON 并重启。`Logging` 与 `Serilog` 是两套级别配置，当前主要输出使用 Serilog。

Microsoft 日志级别全部为：`Trace`（最细追踪）、`Debug`（调试）、`Information`（正常运行）、`Warning`（异常征兆）、`Error`（操作失败）、`Critical`（严重故障）、`None`（关闭）。Serilog 对应支持 `Verbose`、`Debug`、`Information`、`Warning`、`Error`、`Fatal`；其中 Verbose 对应最细追踪，Fatal 对应严重故障，Serilog 最小级别没有 None。

WriteTo 的 Name、Using 和 Enrich 是插件名称，不是固定枚举；上表列的是当前`appsettings.json`。Host 还额外写入 `AgwLogDir/application-{角色}-.log`，每小时生成一个新日志文件，保留 30 个文件，每秒将日志写入磁盘；这些规则由程序固定，不能通过本页配置修改。

```text
[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] [{SourceContext}] [TraceId:{TraceId}] [SpanId:{SpanId}] [ThreadId:{ThreadId}] {Message:lj}{NewLine}{Exception}
```

### 认证与 Token

在浏览器中使用远程 Web 时，用管理员密码登录，浏览器会通过 Cookie 记住登录状态。Desktop、Mobile 和自动化程序则使用 Token（访问令牌），它相当于这些客户端连接 Server 的钥匙。请求格式为：

```http
Authorization: Bearer agw_<your-token>
```

创建 Token 时为它起一个便于识别的名称，并保存当时显示的完整值；之后不会再次显示。自动化程序可以从环境变量或机密配置中读取它。不再使用时撤销该 Token。Server 会按创建者的身份判断它能访问哪些资源。当前不提供多账号登录、角色、Token 权限范围（scopes）或 JWT 的配置。

密码和 Token 都以用于验证的哈希值保存在数据库中，不保存原文。管理员认证信息位于 `setting` 表的 `auth` 分组，Token 信息位于 `api_token` 表，由管理功能自动维护，无需在 appsettings 中填写。修改管理员密码后，各 Server 会检查新的登录状态版本；检查每秒进行一次，读取失败时不再沿用缓存的认证信息。忘记密码可先停止 Server，再运行 `agw-server auth reset-password`。

## 配置示例与验证

下面的示例只展示设置方式，连接字符串应通过环境或 Secrets 注入实际值：

```bash
export Database__Provider=postgres
export Database__ConnectionString='Host=db;Port=5432;Database=agw;Username=agw;Password=REPLACE_ME'
export Execution__Provider=Distributed
export DistributedLock__Provider=postgres
agw-server --urls http://127.0.0.1:30816
```

分离部署将相同数据库和执行配置传给各 Host，先初始化 Control Plane，再启动 Data Plane。上面的 `agw-server` 是 Standalone 程序，分离部署使用相应 Host 程序。

重启后检查启动日志、访问地址、数据库连接和客户端登录。调整执行参数后，先运行一个小任务，观察开始速度、完成时间和资源占用。启动报错时，先检查选项名称是否拼写正确、数值是否在允许范围内、数据库地址和凭据是否正确，以及 Distributed 所需的数据库和锁是否已配置。排查日志时不要公开密码或 Token。

## 实现与参考

- [Host template](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Host/appsettings.json)
- [Host configuration readers](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Host/Program.cs)
- [Deployment defaults](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Host/Hosting/ServerDeploymentConfiguration.cs)
- [Execution settings](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Agents.Execution/Configuration/ExecutionRuntimeOptions.cs)
- [History settings](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Projects/Infrastructure/ConversationHistoryOptions.cs)
- [OAuth URLs](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Integrations/Application/OAuth/OAuthRedirectUriResolver.cs)
- [Shell backend](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Tools/Impl/ContextualTools/Shell/ShellContextualTool.cs)
- [Authentication](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Auth/README.md)
