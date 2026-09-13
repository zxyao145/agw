# 当前代码技术债审查与偿还方案

审查日期：2026-09-12。基线：`c20fdaf6`，审查开始时工作区干净。

结论：当前最值得偿还的是**运行时状态、资源生命周期和故障恢复的技术债**。模块分层已有有效约束，但结构测试通过不足以证明并发互斥、一次性授权、任务调度和资源回收正确。建议优先修复下述可复现缺陷，再完善生产环境验证；保持现有模块化单体和持久化所有权设计。

本次是按影响面抽样的仓库级审查，重点覆盖 Jobs、A2A、OAuth、架构守卫、Chat runtime 和 CI/依赖链。没有逐行审计全部客户端、工具、文件系统和运行时，也没有对真实部署做渗透或负载测试。P1 表示应优先修复的正确性问题，P2 表示应排入近期迭代的问题；未确认 P0。

## 后续修复状态（2026-09-12）

用户随后授权修复，本节记录实际实现；下文保留 `c20fdaf6` 基线上的审查证据。

| 项目 | 已实现 |
| --- | --- |
| TD-01、TD-06 | Jobs 异常安全释放项目运行标记；基础设施失败按现有 30 秒退避重新排期；停机等待队列清理；竞争等待取消并观察未完成分支。 |
| TD-02、TD-05 | A2A 复用引用计数 InMemoryApplicationLock；首次 snapshot 位于订阅 finally 范围内；每个订阅最多缓冲 128 条事件，每个 notifier 最多 64 个订阅；溢出显式结束订阅，容量直到订阅实际释放才归还，避免慢连接绕过限制。 |
| TD-03、TD-04 | 授权尝试使用现有加密 ConnectionCredential 的专用 slot 和 metadata 标识；回调在访问提供商前原子消费标识。授权、刷新、连接修改与安装配置修改按 owner/plugin 使用同一应用锁，取得锁后重读持久化状态，并传播锁丢失取消。瞬时提供商错误保留有效状态，明确 invalid_grant 才标记凭据失效。 |
| TD-07 | Server PR 触发范围增加 tests、solution、workflow 和 CI 脚本；增加 PostgreSQL 18 job；检查 TRX，缺失、跳过或失败的关键测试不能假绿。 |
| TD-08 | Microsoft.OpenApi 固定到 2.7.5，SQLitePCLRaw.bundle_e_sqlite3 固定到兼容的 2.1.13，并确认生产解析到了 lib.e_sqlite3 2.1.13。后端构建将 High/Critical NuGet 告警作为错误，CI 额外拒绝审计服务失败。 |

实现与初始方案的区别：授权状态复用已有凭据存储，**无需新增数据库表、字段或迁移**；锁粒度采用 owner/plugin，让安装配置批量失效与该插件的所有连接刷新保持一致。代价是同一用户、同一插件的凭据/配置写操作会串行，读取和不同 owner/plugin 不受该锁串行限制。它没有把外部提供商与本地数据库变成原子事务；回调消费后若进程崩溃，需要重新发起授权。

OAuth state 的保护用途升级为 v4；部署前尚未完成的旧授权链接会被拒绝，需要重新发起。已保存的连接和 token 不因这次格式升级而被清空。

真实 PostgreSQL 验证还发现原批写测试存在死锁错误被 Npgsql 包装为 InvalidOperationException 后绕过恢复逻辑的问题。现已识别内层数据库瞬时失败，使用独立 scope 和最多五次指数退避重试，重试耗尽时映射 DurableExecutionUnavailable；新增测试验证成功重试和预算耗尽。

最终验证：Release 全量 **2,157 通过、0 失败、0 跳过**；包含真实 PostgreSQL fencing/并发批写、架构守卫、OAuth 双 DbContext 轮换刷新/锁丢失/旧回调/配置失效、Jobs 故障/定时唤醒/停机清理、A2A 互斥/提前退出/慢订阅容量、SQLite 原生版本下限检查。完整依赖审计通过，CI TRX 守卫已验证会拒绝失败或缺失结果。

CI 配置已修改并完成本地对应验证，未在 GitHub 触发远程 workflow。所有修改留在工作区，未创建 commit。

## 判断依据：从必须成立的事实出发

| 第一性约束 | 对抗性问题 | 对应发现 |
| --- | --- | --- |
| 同一资源在任意时刻只能有一个有效写入者 | 最后一个订阅退出时，旧锁持有者还在工作怎么办？ | TD-02 |
| 内存中的运行标记必须有真实执行者负责释放 | 锁服务、数据库或结果记录器抛异常后，谁继续处理项目队列？ | TD-01 |
| 旧请求不能覆盖新状态 | 授权成功后再次访问旧回调；两个节点同时刷新凭据会怎样？ | TD-03、TD-04 |
| 每项资源都有清晰的释放条件和容量上限 | 只消费一个事件就断开，或消费者持续慢于生产者会怎样？ | TD-05 |
| 竞争等待结束后，失败分支不能继续消费未来事件 | 定时器先完成后，原信号等待者是否还活着？ | TD-06 |
| 质量证据必须覆盖声称支持的部署方式 | SQLite 测试通过能证明 PostgreSQL 锁丢失和并发批写正确吗？ | TD-07 |
| 交付风险由实际解析的完整依赖树决定 | 直接包已更新，传递依赖是否仍停留在有告警版本？ | TD-08 |

## 已执行的验证

| 验证 | 结果 |
| --- | --- |
| `dotnet test tests/Agw.Architecture.Tests --no-restore --verbosity quiet` | 43 通过 |
| `dotnet test Agw.slnx --no-restore --verbosity quiet` | 16 个测试程序集，2,136 通过、0 失败、2 跳过；包含上述架构测试 |
| `pnpm test:boundaries` | 17 个必需包的边界检查通过 |
| `pnpm --filter @agw/chat-runtime test` | 55 通过 |
| 独立 .NET 复现程序，调用原生产类型和既有 OAuth 测试夹具 | TD-01、TD-02、TD-03、TD-05 的提前断开、TD-06 均复现 |
| `dotnet nuget why`，针对 Standalone Host | 确认两项告警包属于生产依赖链 |

独立复现只使用模拟锁服务、模拟 HTTP 提供商和内存 SQLite，不运行真实 Agent CLI，不访问真实 OAuth 账号。程序位于 `/private/tmp/agw-review-20260912/Program.cs`，运行命令为 `dotnet run --project /private/tmp/agw-review-20260912/Review.csproj`。临时文件不属于仓库交付物；其关键输出保存在下文。临时程序通过反射访问内部调度/订阅方法，只用于构造确定的交错条件，后续应将这些场景转为常规行为测试。

## TD-01 · P1 · Jobs 一次基础设施异常可永久阻塞当前实例上的项目队列

**证据：** [JobHostedService.cs](../../src/server/Agw.Jobs/Scheduling/Coordination/JobHostedService.cs)，`DispatchOrQueueByProject` 第 140–154 行、`StartProjectExecution` 第 171–185 行、`ExecuteProjectQueueAsync` 第 188–224 行；[JobAttemptRunner.cs](../../src/server/Agw.Jobs/Scheduling/Attempts/JobAttemptRunner.cs) 第 46–55 行。

调度时先把项目加入 `_runningProjects`，只有正常处理完队列才移除。获取项目锁失败、claim 数据库失败或结果写入失败都会让执行任务异常退出。外层 continuation 只删除 `_runningExecutions` 并记录日志，不清理 `_runningProjects`，也不重新派发 backlog。后续该项目任务不断入队，却没有消费者。

独立复现：连续派发同一项目的两个任务，第一次模拟锁服务异常；实际获取锁次数为 **1**，项目运行标记仍为 **true**。这证明当前实例无法自愈；其他健康实例是否接管取决于部署方式，不能将其当成本地正确性的保证。

**修复：** 把项目运行标记、backlog 交接和执行 task 的生命周期放进同一个异常安全的流程。异常/取消退出时在 `_queueLock` 下释放标记，明确重排待执行项并唤醒调度器；基础设施失败采用有界退避，业务重试继续由现有 attempt 机制负责。关闭服务时等待已跟踪执行结束或到达停机期限。避免仅在任意位置添加 `TryRemove`，否则可能与新消费者竞态。

**验收：** 分别注入锁获取、claim、结果持久化故障；恢复依赖后同项目下一任务能执行，无需重启。不同项目仍能推进；任意时刻同项目只有一个本地队列消费者；停机后没有遗留执行。

## TD-02 · P1 · A2A 订阅清理能破坏同任务互斥

**证据：** [AgwChannelEventNotifier.cs](../../src/server/Agw.A2A/AgwChannelEventNotifier.cs) 第 79–85、96–112 行；[AgwA2ARequestHandler.cs](../../src/server/Agw.A2A/AgwA2ARequestHandler.cs) 第 531–533、632–669 行。

`RemoveChannel` 在最后一个订阅退出时移除 `_taskLocks[taskId]`，但调用它的 `finally` 没有持有对应 task lease。此时 `ApplyEventAsync` 可能已经取得旧信号量，正在读取或写入任务。新请求随后创建另一把信号量并进入临界区。`AcquireTaskLockAsync` 的重试只处理“等待期间被移除”，无法保护已经通过检查、正在工作的持有者。

独立复现：持有 lease A → 移除最后一个 channel → 在 A 尚未释放时成功取得同任务 lease B。生产代码的“读状态 → 投影 → 保存”因此可能并发执行，造成覆盖更新或终态竞态；本次复现确认互斥被破坏，未进一步运行数据库覆盖写入实验。

**修复：** 将锁生命周期与订阅数量解耦。优先复用经过验证的 keyed-lock 实现；若保留注册表，必须把等待者和持有者计数、条目退休纳入原子协议，只有不存在任何使用者时才能回收。同一 task key 始终只有一个有效同步对象。

**验收：** 固定上述三步交错，第二个 lease 必须等待第一个释放；增加取消、重新订阅、最后订阅退出与终态写入并发测试，同时验证回收后不会产生两个有效锁。

## TD-03 · P2 · OAuth state 可重复使用，旧回调会回退成功状态

**证据：** [OAuthStateProtector.cs](../../src/server/Agw.Integrations/Application/OAuth/OAuthStateProtector.cs) 第 43–54、65–84 行；[OAuthAuthorizationAppService.cs](../../src/server/Agw.Integrations/Application/OAuth/OAuthAuthorizationAppService.cs) 第 143–172、204–210、624–650 行。

state 有加密保护和十分钟有效期，但没有持久化的一次性消费记录，也没有与当前授权尝试的版本绑定。回调成功后，再使用同一 state 提交 `error=access_denied`，仍会重新加载 Connection 并写入 `PendingAuthorization`。浏览器重放旧成功回调时，已用过的 code 被提供商拒绝，也可能将 Connection 标为 Invalid。

独立复现：正常授权后的状态是 **Ready**；重放相同 state、附带模拟提供商错误后，数据库状态变为 **PendingAuthorization**。这是旧请求破坏连接可用性的问题，本次没有证实跨用户越权或凭据泄露。

**修复：** 为 Connection 的当前授权尝试增加持久化 attempt 标识/版本及一次性消费语义，state 绑定该标识。回调先原子认领该尝试，再执行外部交换；成功和失败落库均检查尝试版本。重复、过期、已被新授权替代的请求不得再修改状态。不要只增加进程内 nonce 缓存，split Host 和重启需要共享一致性。

**验收：** 成功回调重复提交、旧错误回调晚到、两次授权乱序到达、两个 Host 同时消费、消费后重启等场景，都不能回退或覆盖较新状态。

## TD-04 · P2 · OAuth refresh 缺少跨请求并发协调

**证据：** [OAuthAuthorizationAppService.cs](../../src/server/Agw.Integrations/Application/OAuth/OAuthAuthorizationAppService.cs) 第 231–277、608–620 行；[OAuthRefreshAppService.cs](../../src/server/Agw.Integrations/Application/OAuth/OAuthRefreshAppService.cs)；[OauthController.cs](../../src/server/Agw.Integrations/Controllers/OauthController.cs) 第 142–150 行。

两个作用域可以读取同一个 refresh token，分别调用提供商，再各自写入凭据和状态。对于轮换 refresh token 的提供商，请求 A 成功得到新 token 后，请求 B 使用旧 token 失败，其错误处理仍可将 A 已经写成 Ready 的连接标为 Invalid。可接受重复刷新但返回不同 token 的提供商还存在乱序覆盖风险。

这项是由源码执行顺序支持的**条件性风险**：需要支持 refresh 且具有相应轮换/重用行为的提供商。本次没有运行双数据库上下文的并发刷新复现，不能将它等同于 TD-03 的动态验证结果。

**修复：** 按 owner 和 connection 获取跨实例应用锁，取得锁后重新读取最新凭据，并用凭据/配置版本防止迟到结果覆盖新状态。锁丢失必须停止提交。错误处理区分明确失效的凭据和网络瞬时失败，后者不应无条件覆盖新成功状态。复用 TD-03 的版本控制设计，保持在 Integrations Application/Infrastructure 内完成。

**验收：** 两个独立 DbContext 和两个服务实例同时刷新；覆盖 A 成功/B 失败、响应乱序、安装配置变更以及锁丢失。最终凭据必须是有效的新版本，旧失败不能将新版本标为 Invalid。提供商已轮换而进程在落库前崩溃的恢复路径需明确，不宣称外部 HTTP 与本地数据库具有原子事务。

## TD-05 · P2 · A2A 订阅缺少完整释放边界和容量约束

**证据：** [AgwA2ARequestHandler.cs](../../src/server/Agw.A2A/AgwA2ARequestHandler.cs) 第 517–533 行；[AgwChannelEventNotifier.cs](../../src/server/Agw.A2A/AgwChannelEventNotifier.cs) 第 13–14、34–35、50–60、82–85、102 行；[DependencyInjection.cs](../../src/server/Agw.A2A/Extensions/DependencyInjection.cs) 第 35 行。

首个 snapshot 的 `yield return` 位于 `try/finally` 之前。消费者读取首个事件后立即 Dispose，清理代码不会执行。独立复现中，Dispose 后订阅集合数仍为 **1**，应为 0。

同时 channel 使用 `CreateUnbounded`。慢消费者或遗留订阅能持续积压事件。notifier 是 singleton；没有订阅的任务也会在事件处理时创建锁，但锁删除仅发生于 `RemoveChannel`，因此正常任务路径也缺少统一退休机制。这些问题共同把历史请求数量、消费者速度变成进程内存风险。未执行长期堆内存或 OOM 压测。

**修复：** 让 `try/finally` 覆盖注册后的首次 yield 和整个订阅生命周期；结合 TD-02 安全回收 task 状态。为每个订阅设置容量/字节预算和订阅数上限，溢出时显式终止慢订阅，并提供查询快照或 durable cursor 恢复路径。不能静默丢弃终态事件，也不能在持有任务锁时无限等待慢消费者。

**验收：** 首个事件后 Dispose、首次发送失败、取消、正常终态均释放资源；无订阅任务完成后状态可回收；慢消费者压力测试中队列有界，快消费者仍能收到终态。

## TD-06 · P2 · Jobs 的失败等待分支会吞掉后续唤醒

**证据：** [JobHostedService.cs](../../src/server/Agw.Jobs/Scheduling/Coordination/JobHostedService.cs) 第 117–124 行；同文件第 88–97 行的 prefetch loop 已使用局部取消。

执行循环竞争 `Task.Delay` 和 `_wakeSignal.WaitAsync`，但 `WhenAny` 后没有取消未完成的分支。当定时器先到期，旧的信号等待者仍留在队列中。调度器执行任务并进入新的等待后，下一个 wake 可能被旧等待者消费，真正的循环继续睡眠。

独立复现：第一个定时任务到期执行后，再加入第二个已经到期的任务并发送一次 wake，锁获取次数仍为 **1**，应为 2。周期 prefetch 可能后续再次唤醒，因此不能把这一项单独描述成永久阻塞，但它破坏即时调度并积累无主等待者。

**修复：** 参照同文件 prefetch loop，用每轮 linked CancellationTokenSource 管理两个竞争分支，结束后取消并观察未获胜分支；或将信号与超时组合为单次可取消等待。保留清晰的停止语义。

**验收：** 使用受控 TimeProvider 覆盖 timer-first、signal-first、取消和连续重新排期。定时任务之后的新任务只需一个 wake 即可执行；多轮等待后不存在额外的信号消费者。

## TD-07 · P2 · CI 没有完整覆盖变更入口和 PostgreSQL 正确性

**证据：** [build-server.yml](../../.github/workflows/build-server.yml) 第 4–9、22–36 行；[PostgresExecutionFencingTests.cs](../../tests/Agw.Agents.Tests/PostgresExecutionFencingTests.cs) 第 23–28 行；[PostgresEventBatchingTests.cs](../../tests/Agw.Agents.Tests/PostgresEventBatchingTests.cs) 第 17–22 行。

Server PR workflow 的路径过滤没有包含 `tests/**`、根 `Agw.slnx` 和 workflow 自身。只修改这些文件的 PR 不会触发该 Server Build。主分支 push 会运行，但这不构成合并前验证。

两项 PostgreSQL 测试依赖 `AGW_TEST_POSTGRES_CONNECTION_STRING`。本次全量测试中它们被跳过；版本库内 workflow 没有 PostgreSQL 服务或该变量注入。未读取 GitHub 的分支保护、外部 CI 或线上设置，因此这里评价的是已提交的验证配置。

**修复：** 补齐 Server PR 路径过滤；增加有隔离数据库的 PostgreSQL 测试 job，显式配置连接串和建库权限，并要求 fencing/并发批写测试实际执行。保留默认快速 SQLite 测试，不要求开发者日常运行真实 Agent CLI。发布分布式版本前，再覆盖双 Host、锁连接中断、重启恢复和幂等重放。

**验收：** tests-only、solution-only、workflow-only PR 都触发 Server 检查；PostgreSQL job 无条件运行这两项测试且不允许因缺失环境而假绿。是否设为 required check 需结合仓库实际分支策略实施。

## TD-08 · P2 · 生产传递依赖存在高危告警，但没有阻断交付

**证据：** [Directory.Packages.props](../../src/server/Directory.Packages.props) 第 44、54 行；[Directory.Build.props](../../src/server/Directory.Build.props)；本次 `dotnet test` 输出 `NU1903`，仍以成功退出；`dotnet nuget why` 确认以下生产依赖链：

```text
Standalone Host → Agw.Host → Microsoft.AspNetCore.OpenApi 10.0.10
  → Microsoft.OpenApi 2.0.0

Standalone Host → Agw.Infrastructure → Microsoft.EntityFrameworkCore.Sqlite 10.0.10
  → SQLitePCLRaw.bundle_e_sqlite3 2.1.11 → SQLitePCLRaw.lib.e_sqlite3 2.1.11
```

Microsoft.OpenApi 2.0.0 落入循环 schema 引用导致解析进程栈溢出的受影响区间，2.x 的修复版本从 2.7.5 开始。来源：[Microsoft 官方公告](https://github.com/microsoft/OpenAPI.NET/security/advisories/GHSA-v5pm-xwqc-g5wc)。本次未发现业务代码使用所检索的 OpenAPI reader 入口解析不可信文档，因此不声称已有可从 Agw API 直接利用的攻击链。

SQLite 原生包 2.1.11 被 NuGet 标为高危，关联 CVE-2025-6965；对应 SQLite 引擎问题在 3.50.2 修复。来源：[NuGet 告警关联公告](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)、[SQLite 官方发布说明](https://www.sqlite.org/releaselog/3_50_2.html)。包版本与引擎版本不是同一编号，不能简单把 NuGet 包版本改成 3.50.2。未验证该漏洞在当前应用查询路径的可利用性。

**修复：** 对完整传递依赖树设置 High/Critical 门禁。先测试兼容的 OpenApi 修复版本；SQLite 选择包含已修复原生引擎的受支持 provider/bundle 组合，并在目标平台核对 `sqlite_version()`。检查 Web OpenAPI 生成、旧库读取、SQLite/PostgreSQL 既有迁移和 Desktop 打包兼容性。确实不可达且暂时无法升级的条目需要有负责人、证据和失效日期，不能全局关闭审计。

**验收：** 发布产物依赖树中不再包含上述未处理项；审计门禁能对新高危传递依赖失败。升级不自动附带数据库 schema 迁移。

## 建议的实施顺序

| 批次 | 范围与建议责任模块 | 交付结果 | 粗略工程量 |
| --- | --- | --- | --- |
| 1 | Jobs：TD-01、TD-06；A2A：TD-02、TD-05 的释放缺陷 | 将五个已复现情形转为失败测试，再用最小变更修复；确保停机与取消路径 | 2–3 人日 |
| 2 | Integrations：TD-03、TD-04 | 授权尝试版本、一次性消费、跨实例刷新协调；双上下文交错测试 | 2–4 人日 |
| 3 | CI/Host：TD-07、TD-08 | 合并前 PostgreSQL 验证、完整触发路径、依赖风险门禁与兼容性升级 | 2–3 人日 |
| 4 | A2A：TD-05 容量部分；Jobs/Execution 运维指标 | 有界缓冲、压力验证，暴露队列年龄、运行标记、订阅数及缓冲大小 | 1–2 人日 |

工程量是初步估算，按熟悉模块的工程师计，不含外部提供商差异和多平台发布排障。CI 缺口和依赖处理可以提前推进，无需等待业务修复全部完成。

批次 2 如果增加持久化字段，需要同时设计 SQLite/PostgreSQL 迁移；本次只制定方案，未生成或应用迁移。各修复保持原有模块所有权，不引入通用工作流框架，不扩大架构例外清单。

完成标准以行为为准：故障恢复后队列继续推进；旧授权请求不能改写新状态；同任务写入始终互斥；订阅资源有界且可释放；分布式正确性在实际 PostgreSQL 上有可重复证据。

## 应保留的现有设计与结论边界

- 架构守卫已有效约束模块依赖和客户端边界；本轮架构检查通过，原始持久化访问债务 inventory 已为空。这些约束应保留。
- 当前单一 DbContext、多模块 persistence seam 和无数据库外键是明确的项目设计。本轮没有证据支持把它们直接列为应推翻的技术债，也没有建议整体迁移富领域模型或拆分微服务。
- OAuth 已对 owner、state 完整性、回跳路径和敏感输出做了保护；这里识别的是一次性状态和并发提交的缺口。
- 不以文件长度、分层数量、没有采用某种框架等代理指标代替缺陷证据。若后续要进一步拆分复杂服务，应围绕上述状态机与生命周期的责任边界，而不是机械拆文件。
- 现有测试的绿色结果真实有效，但其覆盖范围不足以排除本次通过异常注入和固定交错发现的问题。五个复现用例的预期失败输出如下：

```text
JOB_FAILURE: lock attempts after two dispatches=1; project stuck running=True
JOB_WAKE: attempts after a second due job and one wake=1
A2A_LOCK: acquired a second lease while first lease is still held
A2A_INITIAL_DISPOSE: remaining subscriber sets=1
OAUTH_REPLAY: valid callback status=Ready; replayed state with error status=PendingAuthorization
```

初始审查任务只新增本报告；后续修复范围与验证结果见文首“后续修复状态”。
