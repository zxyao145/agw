# Desktop 导入本机 Skills 目录 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**目标：** Desktop Skills 页面扫描本机 `~/.agents/skills`，把选中的 Skill 导入或更新到服务器，服务器保存 sourceDigest；更新或删除 Skill 时，执行中的 Agent 继续使用它构建时的内容目录。

**架构：** Desktop 主进程扫描、计算摘要并打包 ZIP，renderer 通过现有的 `POST /api/skills`、`PUT /api/skills/{id}` 上传。服务器把 sourceDigest 存进 `skill.extra`。Agent 构建时对内容目录取共享锁（内容租约），更新与删除只写退役标记，没有内容租约时删除旧目录。

**技术栈：** ASP.NET Core / EF Core（.NET 10）、Medallion DistributedLock.Postgres、Nito.AsyncEx.Coordination、Electron、`vfile` + `vfile-matter`、`yazl`/`yauzl`、React（`@agw/components`）、node:test + tsx、xUnit v3。

**设计文档：** `docs/designs/2026-11-26-Desktop导入本机Skills目录.md`。每个任务都以设计文档对应章节为准，本计划只写设计文档没有确定的实现决定。

## 全局约束

- 注释使用中文与英文两种语言，技术术语保持英文，不写过多注释。
- C#：只用 `DateTimeOffset` 与 `TimeProvider`；预期失败抛出 `AgwException(ErrorCodes.X)`；日志使用结构化模板；不用 `#region`；不写空 `catch`；写法遵循 `docs/human/2.csharp-coding-rules.md`。
- 失败时立即抛出，不加兜底逻辑。
- 测试使用真实实现，不使用 mock、fake 或只为通过测试的写法。C# 测试名为 `Method_Condition_ExpectedResult`；TypeScript 测试沿用设计文档中的用例名。
- 临时文件放在 `.scratch/` 下（已经列在 `.gitignore` 中），不读写 `/tmp`。
- 不用 Git 回滚代码；不用 `sed`、脚本或 heredoc 修改代码。
- 只在用户要求时提交，本计划不包含提交步骤。
- 格式化：`dotnet csharpier format <改动的 .cs 文件>`；客户端在 `src` 下执行 `pnpm fmt`。
- 手动或在测试中启动 Host 时，传入 `--environment=Production`（任务 4 导出 OpenAPI 例外）、空闲端口与 `.scratch` 下的 `--AgwDataDir`，并移除继承的 `ASPNETCORE_ENVIRONMENT`、`ASPNETCORE_URLS`；运行 `dotnet test` 时使用 `env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS`。
- 只运行单个 C# 测试：`dotnet test --project tests/<Project> --filter-method "*<Name>"`；输出 "Zero tests ran" 时，先 `dotnet build tests/<Project>`，再运行 `dotnet tests/<Project>/bin/Debug/net10.0/<Project>.dll -method "*<Name>"`。
- 每次运行 `pnpm --filter @agw/desktop test:extensions` 之前先请用户确认。
- 固定值：
  - 上传上限 `104857600` 字节；nginx `client_max_body_size 100m`。
  - sourceDigest 格式 `^sha256:[0-9a-f]{64}$`。
  - 错误码：`SkillSourceDigestInvalid` 为 `400_0081`，`SkillSourceDigestRequiresArchive` 为 `400_0082`（实现前确认 400 段最大值仍是 `400_0080`，否则顺延）。
  - 锁资源名 `skill-content:<ContentPath>`；退役标记为 `<内容目录>.retired`。
  - 扫描跳过的名称：`.git`、`.DS_Store`、`Thumbs.db`、`desktop.ini`。
  - IPC 通道：`agw:scan-local-skills`、`agw:pack-local-skill`。
  - 界面文字与 `problem` 文字使用设计文档第 1、5、6 节给出的英文原文。

## Review Focus

1. Windows 上 `relativePath` 使用 `\` 会使同一目录在不同系统上得到不同摘要；期望各系统摘要相同。任务 5 用固定期望值测试 `scanLocalSkills_FixedTree_MatchesGoldenDigest`，Windows CI 上同样执行。
2. Windows 编辑过的 `SKILL.md` 带 BOM 或 CRLF；期望正常解析 `name` 与 `description`（服务器 `File.ReadAllText` 会去掉 BOM，所以服务器接受这种文件）。任务 5 测试 `scanLocalSkills_BomAndCrlfFrontMatter_ParsesMetadata`。
3. Skill 中有非 ASCII 文件名（例如 `参考/说明.md`）；期望 ZIP 条目名与服务器解压后的文件名都保持原样。任务 5 测试 `packLocalSkill_NonAsciiFileName_PreservesEntryName`，任务 7 检查服务器内容目录中的文件。
4. 本机 Skills 目录中有指向 Skills 目录自身的符号链接；期望扫描正常结束，并把这一项报告为 `Invalid`。任务 5 测试 `scanLocalSkills_TopLevelSymlinkToRoot_ReportsProblemWithoutLooping`。
5. 扫描之后、导入之前，另一台电脑已经导入了同名 Skill；期望 `gamma` 行显示服务器返回的 `Skill 'gamma' already exists.`，批量导入在这一行停止。任务 8 的界面场景覆盖。

---

### 任务 1：服务器记录 sourceDigest，Skill 上传上限 100 MiB

**文件：**
- 新增：`src/server/Agw.Data/Entities/AgentCapabilities/Skills/SkillExtra.cs`
- 修改：`src/server/Agw.Data/Entities/AgentCapabilities/Skills/Skill.cs`、`SkillConfiguration.cs`
- 修改：`src/server/Agw.Shared/Exceptions/ErrorCodes.cs`
- 修改：`src/server/Agw.Skills/Domain/Behaviors/SkillBehavior.cs`
- 修改：`src/server/Agw.Skills/Application/SkillAppService.cs`
- 修改：`src/server/Agw.Skills/Contracts/Manager/SkillRequests.cs`、`src/server/Agw.Skills/Controllers/SkillsController.cs`
- 新增（生成）：`src/server/Agw.Migrations.Sqlite/Migrations/*_AddSkillExtra.cs`、`src/server/Agw.Migrations.Postgres/Migrations/*_AddSkillExtra.cs` 及各自的 Designer 与 ModelSnapshot 变更
- 修改：`deploy/nginx.split.conf.example`、`docs/4.Deployment.md`
- 测试：`tests/Agw.Skills.Tests/SkillBehaviorTests.cs`、`tests/Agw.Skills.Tests/SkillAppServicePersistenceTests.cs`

**接口：**
- 产出：
  - `public sealed record SkillExtra { public string? SourceDigest { get; init; } }`（namespace `Agw.Shared.Data.Entities.Skills`）；`Skill.Extra`（`SkillExtra`，默认 `new()`）。
  - `SkillBehavior.EnsureCreatable(bool archiveSupplied, string? sourceDigest)`；`SkillBehavior.EnsureUpdatable(string name, bool archiveSupplied, string? remoteUrl, string? sourceDigest)`；`SkillBehavior.DefineLocalContent(string? sourceDigest)`。
  - `SkillAppService.CreateAsync(Skill skill, IFormFile? archive, string user, string? remoteUrl = null, string? sourceDigest = null, CancellationToken cancellationToken = default)`；`UpdateAsync(Guid id, string name, string description, IFormFile? archive, string user, string? remoteUrl = null, string? sourceDigest = null, CancellationToken cancellationToken = default)`。
  - `SkillCreateRequest.SourceDigest`、`SkillUpdateRequest.SourceDigest`（`string?`）；`SkillResponse` 在 `RemoteUrl` 之后增加 `string? SourceDigest`。

- [x] **步骤 1：在 `SkillBehaviorTests` 中写失败的测试**

```csharp
[Fact]
public void EnsureCreatable_DigestWithoutArchive_Throws()
{
    var behavior = new SkillBehavior(new Skill { Kind = SkillKind.Local });
    var exception = Assert.Throws<AgwException>(() => behavior.EnsureCreatable(false, ValidDigest));
    Assert.Equal(ErrorCodes.SkillSourceDigestRequiresArchive.Code, exception.Code);
}

[Theory]
[InlineData("sha256:FD5DB01F805C3FA8EAEC5B16AFE3D3BC9C02EBF1B9E7081ADE4674D50BA3384E")]
[InlineData("sha256:fd5db01f")]
[InlineData("fd5db01f805c3fa8eaec5b16afe3d3bc9c02ebf1b9e7081ade4674d50ba3384e")]
public void EnsureCreatable_InvalidDigest_Throws(string digest) { /* 断言 SkillSourceDigestInvalid */ }

[Fact]
public void EnsureUpdatable_RemoteWithDigest_Throws() { /* Kind = Remote，archiveSupplied = false，断言 SkillSourceDigestRequiresArchive */ }

[Fact]
public void DefineLocalContent_BlankDigest_StoresNull() { /* "  " 保存为 null；ValidDigest 原样保存 */ }

private const string ValidDigest = "sha256:fd5db01f805c3fa8eaec5b16afe3d3bc9c02ebf1b9e7081ade4674d50ba3384e";
```

断言错误码沿用 `SkillBehaviorTests` 现有的 `Assert.Equal(expected.Code, exception.Code)`。现有用例中 `EnsureCreatable(archiveSupplied)`、`EnsureUpdatable(...)` 的调用改为传入 `sourceDigest: null`。

- [x] **步骤 2：在 `SkillAppServicePersistenceTests` 中写失败的测试**

使用现有的 SQLite 内存数据库与 `CreateProductionSkillService` 写法，每个用例上传一个含 `SKILL.md` 的 ZIP：

- `CreateAsync_LocalWithDigest_PersistsDigest`：用新的 `AgwDbContext` 重新读取，`Extra.SourceDigest == ValidDigest`；返回的 `SkillDetails.Skill.Extra.SourceDigest` 相同。
- `CreateAsync_LocalWithoutDigest_LeavesDigestNull`。
- `UpdateAsync_ArchiveWithDigest_ReplacesDigest`：先以 digest A 创建，再以 ZIP 与 digest B 更新，重新读取为 B。
- `UpdateAsync_ArchiveWithoutDigest_ClearsDigest`：先以 digest A 创建，再上传 ZIP 不带 digest，重新读取为 null。
- `UpdateAsync_DescriptionOnly_KeepsDigest`：只改描述，不带 ZIP，重新读取仍为 A。
- `UpdateAsync_SaveFails_RestoresExtra`：按现有保存失败用例的结构（`ResolveDirectoryAsync_LegacyMigrationDatabaseFailure_PreservesOriginalDirectoryAndMetadata` 使用的拦截方式）让保存抛出异常，之后内存中实体的 `Extra.SourceDigest` 仍为 A。

- [x] **步骤 3：运行测试，确认失败**

运行：`env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS dotnet test --project tests/Agw.Skills.Tests`
期望：编译失败，提示 `SkillSourceDigestRequiresArchive`、`DefineLocalContent`、`Extra` 不存在。

- [x] **步骤 4：实现实体、错误码、Behavior、AppService、Controller**

- `SkillConfiguration`：`builder.Property(e => e.Extra).HasConversion(ExtraJson.Converter<SkillExtra>()).HasDefaultValueSql("'{}'");`
- `ErrorCodes`：两个错误码放在其他 Skill 错误码附近，消息使用设计文档第 8 节的原文，`HttpStatusCode.BadRequest`。
- `SkillBehavior`：格式用 `^sha256:[0-9a-f]{64}$` 的 compiled `Regex`；sourceDigest 为空白时视为没有提交。先检查归档包再检查格式。
- `SkillAppService`：`CreateAsync`、`UpdateAsync` 把 sourceDigest 传给 Behavior；`CreateLocalAsync` 发布目录时、`PublishArchiveAsync` 中调用 `DefineLocalContent`；`LocalSkillState` 增加 `SkillExtra Extra`，`RestoreLocalSkillAsync` 恢复它。
- `SkillsController`：传递 `request.SourceDigest`；`Map` 输出 `skill.Extra.SourceDigest`；`CreateAsync`、`UpdateAsync` 增加 `[RequestSizeLimit(104857600)]` 和 `[SkillUploadLimit]`。`SkillUploadLimitAttribute` 将表单绑定期间的 Kestrel 413 转换为 `FileTooLarge`。
- 更新所有调用 `EnsureCreatable`、`EnsureUpdatable`、`CreateAsync`、`UpdateAsync` 的现有代码与测试（`git grep -n "EnsureCreatable\|EnsureUpdatable"`）。

- [x] **步骤 5：生成 migration**

在仓库根目录执行（`MigrationName` 为 `AddSkillExtra`）：

```bash
dotnet ef migrations add AddSkillExtra -p src/server/Agw.Migrations.Sqlite -s src/server/Agw.Standalone.Host -- --provider sqlite
dotnet ef migrations add AddSkillExtra -p src/server/Agw.Migrations.Postgres -s src/server/Agw.Standalone.Host -- --provider postgres
```

检查两个 migration 都只有 `AddColumn<string>(name: "extra", table: "skill", type: "text", nullable: false, defaultValueSql: "'{}'")` 与对应的 `DropColumn`。

- [x] **步骤 6：运行测试，确认通过**

运行：
- `env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS dotnet test --project tests/Agw.Skills.Tests`
- `env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS dotnet test --project tests/Agw.Shared.Tests`
- `env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS dotnet test --project tests/Agw.Architecture.Tests`
- `env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS dotnet test --project tests/Agw.Projects.Tests --filter-method "*InitialMigration*"`

期望：全部通过；`InitialMigrationTests` 中 `HasPendingModelChanges()` 为 false。

- [x] **步骤 7：修改部署示例与部署文档**

- `deploy/nginx.split.conf.example`：在 `location /` 之前加入设计文档第 7 节的 `location /api/skills` 块。
- `docs/4.Deployment.md`「Distributed execution」：把 "All replicas must share the database, Data Protection key ring, and host-visible Project Workspace paths." 改为所有副本共享数据库、`<AgwDataDir>`（包括 `keys/` 与 `skills/`）与 Project Workspace 路径，并说明入口需要允许 100 MiB 的 `/api/skills` 请求（示例见 `deploy/nginx.split.conf.example`）。

- [x] **步骤 8：格式化**

运行：`dotnet csharpier format <本任务改动的 .cs 文件>`，生成的 migration 文件除外。

---

### 任务 2：`IApplicationLock` 的共享锁与独占锁

**文件：**
- 修改：`src/server/Agw.Shared/Contracts/Coordination/IApplicationLock.cs`
- 修改：`src/server/Agw.Shared/Coordination/InMemoryApplicationLock.cs`、`src/server/Agw.Shared/Agw.Shared.csproj`
- 修改：`src/server/Agw.Infrastructure/Coordination/ApplicationLockRouter.cs`
- 测试：`tests/Agw.Shared.Tests/InMemoryApplicationLockTests.cs`；新增 `tests/Agw.Jobs.Tests/ApplicationLockRouterPostgresTests.cs`

**接口：**
- 产出：
  - `Task<IApplicationLockLease> IApplicationLock.AcquireSharedAsync(string resourceName, CancellationToken cancellationToken)`
  - `Task<IApplicationLockLease?> IApplicationLock.TryAcquireExclusiveAsync(string resourceName, CancellationToken cancellationToken)`

- [x] **步骤 1：在 `InMemoryApplicationLockTests` 中写失败的测试**

```csharp
[Fact]
public async Task TryAcquireExclusiveAsync_SharedLeaseHeld_ReturnsNull()
{
    var applicationLock = new InMemoryApplicationLock();
    await using var shared = await applicationLock.AcquireSharedAsync("resource", Token);
    Assert.Null(await applicationLock.TryAcquireExclusiveAsync("resource", Token));
}

[Fact]
public async Task AcquireSharedAsync_ExclusiveLeaseHeld_WaitsForRelease()
{
    var applicationLock = new InMemoryApplicationLock();
    var exclusive = Assert.IsAssignableFrom<IApplicationLockLease>(
        await applicationLock.TryAcquireExclusiveAsync("resource", Token)
    );
    var sharedTask = applicationLock.AcquireSharedAsync("resource", Token);
    Assert.False(sharedTask.IsCompleted);
    await exclusive.DisposeAsync();
    await using var shared = await sharedTask;
}
```

另外写 `TryAcquireExclusiveAsync_ExclusiveLeaseHeld_ReturnsNull`、`TryAcquireExclusiveAsync_AllLeasesReturned_ReturnsLease`（两个共享租约都归还后返回非 null）、`AcquireSharedAsync_MultipleShared_AllGranted`（同一资源的两个 `AcquireSharedAsync` 都立即完成）。

- [x] **步骤 2：新增 `ApplicationLockRouterPostgresTests`**

类加 `[Trait("Category", "Postgres")]`。按 `tests/Agw.Files.Tests/FilePathLocksPostgresTests.cs` 的 `RequireConnectionString` 与 `CreateRouter` 写法，用两个 `ApplicationLockRouter` 实例模拟两个节点，资源名每个用例使用新的 GUID：

- `TryAcquireExclusiveAsync_SharedOnOtherRouter_ReturnsNull`：路由器 A 持有共享租约时，B 的 `TryAcquireExclusiveAsync` 返回 null；A 归还后 B 返回租约。
- `AcquireSharedAsync_ExclusiveOnOtherRouter_WaitsForRelease`：A 持有独占租约时，B 的 `AcquireSharedAsync` 任务没有完成；A 释放后完成。

- [x] **步骤 3：运行测试，确认失败**

运行：`env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS dotnet test --project tests/Agw.Shared.Tests`
期望：编译失败，`AcquireSharedAsync` 不存在。

- [x] **步骤 4：实现**

- `IApplicationLock`：加入两个方法，注释写明一个资源名只通过 `AcquireAsync` 使用，或者只通过这两个方法使用。
- `InMemoryApplicationLock`：另一个 `ConcurrentDictionary<string, ReaderWriterEntry>`，每个条目包含一个 Nito `AsyncReaderWriterLock`，引用计数与回收方式与现有 `Entry` 相同。`AcquireSharedAsync` 调用 `ReaderLockAsync(cancellationToken)`；`TryAcquireExclusiveAsync` 用 `new CancellationToken(canceled: true)` 调用 `WriterLockAsync`，捕获 `OperationCanceledException` 后释放条目引用并返回 null。
- `Agw.Shared.csproj` 增加 `<PackageReference Include="Nito.AsyncEx.Coordination" />`。
- `ApplicationLockRouter`：`inmemory` 转发到 `InMemoryApplicationLock`；`postgres` 时把 provider 转换为 `IDistributedReaderWriterLockProvider`（转换失败抛出 `AgwException(ErrorCodes.UnsupportedDistributedLockProvider)`），`CreateReaderWriterLock($"agw:application:{resourceName}")`，共享用 `AcquireReadLockAsync(cancellationToken: cancellationToken)`，独占用 `TryAcquireWriteLockAsync(TimeSpan.Zero, cancellationToken)`，句柄包装为 `DistributedSynchronizationLease`。

- [x] **步骤 5：运行测试，确认通过**

运行：
- `env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS dotnet test --project tests/Agw.Shared.Tests`
- `env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS AGW_TEST_POSTGRES_CONNECTION_STRING="Host=localhost;Database=postgres;Username=postgres;Pooling=false" dotnet test --project tests/Agw.Jobs.Tests --filter-method "*ApplicationLockRouterPostgresTests*"`

期望：全部通过；PostgreSQL 用例实际执行，没有跳过（`FilePathLocksPostgresTests` 读取 `.scratch/postgres-tests.json`，没有这个文件时按它的格式创建）。

- [x] **步骤 6：格式化**

运行：`dotnet csharpier format <本任务改动的 .cs 文件>`。

---

### 任务 3：内容租约、退役与启动清理

**文件：**
- 修改：`src/server/Agw.Skills.Contracts/ILocalSkillContentResolver.cs`（加入 `ILocalSkillDirectoryLease`）
- 新增：`src/server/Agw.Skills/Application/Content/ISkillContentRetirement.cs`（含 `SkillContentLocation`）
- 新增：`src/server/Agw.Skills/Infrastructure/Content/SkillContentRetirement.cs`、`SkillContentSweepService.cs`
- 修改：`src/server/Agw.Skills/Application/SkillAppService.cs`、`Application/Facades/LocalSkillContentFacade.cs`、`DependencyInjection.cs`
- 修改：`src/server/Agw.Agents.Execution/Agents/Runtime/AgentRuntimeFactory.Skills.cs`、`Agents/Composition/AgentCapabilityComposition.cs`
- 修改：`src/server/Agw.Agents.Execution/README.md`
- 测试：新增 `tests/Agw.Skills.Tests/SkillContentRetirementTests.cs`、`tests/Agw.Skills.Tests/SkillTestArchives.cs`；修改 `tests/Agw.Skills.Tests/SkillAppServicePersistenceTests.cs`、`tests/Agw.Agents.Tests/AgentRuntimeFactorySystemCompositionTests.cs`，以及所有 `new SkillAppService(` 的位置（`git grep -n "new SkillAppService(" -- tests src`，当前 7 处）

**接口：**
- 使用：任务 2 的 `AcquireSharedAsync`、`TryAcquireExclusiveAsync`。
- 产出：
  - `Task<ILocalSkillDirectoryLease?> ILocalSkillContentResolver.AcquireDirectoryAsync(Guid skillId, CancellationToken cancellationToken = default)`；`public interface ILocalSkillDirectoryLease : IAsyncDisposable { string Path { get; } }`。
  - `Task<ILocalSkillDirectoryLease?> SkillAppService.AcquireLocalDirectoryAsync(Guid skillId, CancellationToken cancellationToken = default)`（替换 `PrepareLocalDirectoryAsync`）。
  - `ISkillContentRetirement`、`SkillContentLocation(string ContentPath, string Directory)` 的签名见设计文档第 9.3 节。
  - `SkillAppService` 构造函数在 `ICurrentUser currentUser` 之后增加 `ISkillContentRetirement contentRetirement`。
  - `internal void AgentCapabilityComposition.Own(IAsyncDisposable resource)`。
  - `AgentRuntimeFactory.ResolveLocalSkillPathsAsync(IReadOnlyList<SkillReferenceSnapshot> skills, AgentCapabilityComposition capabilities, CancellationToken cancellationToken)`，返回 `Task<string[]>`。

- [x] **步骤 1：写 `SkillContentRetirementTests` 的失败测试**

使用 SQLite 内存数据库、`.skill-test-data` 下的数据目录、`new InMemoryApplicationLock()` 与真实的 `SkillContentRetirement`，建库方式与 ZIP 辅助方法 `CreateArchive` 都与 `SkillAppServicePersistenceTests` 相同（把 `CreateArchive` 的两个重载移到两个测试类共用的 `internal static class SkillTestArchives`）。用例名与断言按设计文档「测试计划 / 服务器」中的 8 个用例（`UpdateAsync_NoLease_DeletesOldDirectory` 到 `RetireAsync_LegacyContentPath_KeepsSkillsRoot`）。其中：

```csharp
[Fact]
public async Task UpdateAsync_LeaseHeld_KeepsOldDirectoryUntilReturned()
{
    // 用 CreateArchive() 创建 test-skill，取得租约，再用 CreateArchive("Updated instructions") 更新。
    // Create test-skill with CreateArchive(), acquire a lease, then update it with CreateArchive("Updated instructions").
    var lease = Assert.IsAssignableFrom<ILocalSkillDirectoryLease>(await service.AcquireLocalDirectoryAsync(skillId, Token));
    var oldDirectory = lease.Path;
    await service.UpdateAsync(
        skillId, "test-skill", "description", CreateArchive("Updated instructions"), "tester", cancellationToken: Token
    );

    Assert.Equal(
        "---\nname: test-skill\ndescription: description\n---\nInstructions",
        (await File.ReadAllTextAsync(Path.Combine(oldDirectory, "SKILL.md"), Token)).Replace("\r\n", "\n")
    );
    Assert.True(File.Exists(oldDirectory + ".retired"));

    await lease.DisposeAsync();

    Assert.False(Directory.Exists(oldDirectory));
    Assert.False(File.Exists(oldDirectory + ".retired"));
}
```

`RetireAsync_LegacyContentPath_KeepsSkillsRoot` 直接调用 `SkillContentRetirement.RetireAsync`，`ContentPath` 为 `skills/legacy-skill`：之后 `skills/legacy-skill` 与 `skills/legacy-skill.retired` 不存在，`paths.SkillsDirectory` 仍然存在。

- [x] **步骤 2：修改现有测试**

- `SkillAppServicePersistenceTests` 中 `ResolveDirectoryAsync_*` 用例改名为 `AcquireDirectoryAsync_*`，改为取得租约后读取 `lease.Path`，用 `await using` 归还；断言不变。
- 所有 `new SkillAppService(` 传入 `new SkillContentRetirement(paths, applicationLock, NullLogger<SkillContentRetirement>.Instance)`，`applicationLock` 与同一个调用中传给 `SkillAppService` 的锁相同。
- `AgentRuntimeFactorySystemCompositionTests.ResolveLocalSkillPathsAsync_CancellationToken_CancelsSkillLockWait`：反射查找的参数类型加入 `AgentCapabilityComposition`，调用时传入 `AgwAgentTestHost.Capabilities(new ToolContribution())`。
- 新增 `CreateRuntimeAsync_LocalSkillUpdated_OldDirectoryKeptUntilRuntimeDisposed`：沿用该文件中带 Local Skill 的 Runtime 构造方式；Runtime 构建后通过 `SkillAppService.UpdateAsync` 上传新 ZIP；断言旧目录存在；`await runtime.DisposeAsync()` 后旧目录不存在。

- [x] **步骤 3：运行测试，确认失败**

运行：`env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS dotnet test --project tests/Agw.Skills.Tests`
期望：编译失败，`AcquireLocalDirectoryAsync`、`SkillContentRetirement` 不存在。

- [x] **步骤 4：实现 `SkillContentRetirement` 与 `SkillContentSweepService`**

- `SkillContentRetirement`（singleton，依赖 `AgwDataPaths`、`IApplicationLock`、`ILogger<SkillContentRetirement>`），按设计文档第 9.3 节实现 `AcquireAsync`、`RetireAsync`、`SweepAsync` 与内部的尝试删除。内容租约是一个私有 sealed class，`DisposeAsync` 先释放共享锁，再对同一个 `SkillContentLocation` 尝试删除，重复调用只执行一次。
- 删除空的版本父目录：父目录的父目录等于 `Path.GetFullPath(paths.SkillsDirectory)` 并且父目录为空时才删除。
- `SweepAsync`：在 `paths.SkillsDirectory` 顶层和名称符合 `<skillId:N>-<version:N>` 格式的直接子目录中查找 `*.retired`；`ContentPath` 由标记路径去掉 `.retired` 后相对 `paths.Root` 计算，分隔符换成 `/`。
- `SkillContentSweepService : BackgroundService`，`ExecuteAsync` 调用一次 `SweepAsync(stoppingToken)`。
- `DependencyInjection.AddSkills`：`services.AddSingleton<ISkillContentRetirement, SkillContentRetirement>();`、`services.AddHostedService<SkillContentSweepService>();`。

- [x] **步骤 5：修改 `SkillAppService`、Facade 与 Execution**

- `AcquireLocalDirectoryAsync`：在 `SkillMutationLock` 内确定当前内容目录（保留现有的 `NormalizeLocalDirectoryAsync`），目录不存在时返回 null，否则返回 `await _contentRetirement.AcquireAsync(location, mutation.Token)`。`location.ContentPath` 取 `skill.ContentPath`，为空时取 `GetSkillAbsolutePath` 实际使用的 `skills/<name>`。
- 把 `UpdateLocalAsync`、`NormalizeLocalDirectoryAsync`、`RestoreLocalSkillAsync`（数据库已经提交新 `ContentPath` 的分支）中删除旧目录的调用，以及 `DeleteCoreAsync` 中对 Local 当前目录的删除，都改为 `RetireAsync`。发布失败时删除新目录的调用保持不变。
- `LocalSkillContentFacade.AcquireDirectoryAsync` 转发到 `AcquireLocalDirectoryAsync`。
- `AgentCapabilityComposition.Own` 调用 `_lease.Add(resource)`。
- `AgentRuntimeFactory.ResolveLocalSkillPathsAsync` 每取得一个租约立即 `capabilities.Own(lease)`，再记录 `lease.Path`；`CreateSkillsProviderAsync` 传入 `capabilities`。
- `src/server/Agw.Agents.Execution/README.md` 中 Skill provider 一节（"Definition Agent 的 Skill provider" 一段之后）加入：Local Skill 的内容目录在构建 Agent 时取得内容租约，随 Agent 释放；更新或删除 Skill 后，旧目录在最后一个内容租约归还时删除。

- [x] **步骤 6：运行测试，确认通过**

运行：
- `env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS dotnet test --project tests/Agw.Skills.Tests`
- `env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS dotnet test --project tests/Agw.Agents.Tests --filter-method "*AgentRuntimeFactorySystemCompositionTests*"`
- `env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS dotnet test --project tests/Agw.Architecture.Tests`
- `dotnet build Agw.slnx`

期望：全部通过，构建没有错误。

- [x] **步骤 7：格式化**

运行：`dotnet csharpier format <本任务改动的 .cs 文件>`。

---

### 任务 4：OpenAPI 与 `@agw/skills` 的状态判断

**文件：**
- 修改：`src/clients/packages/api/openapi.json`、`src/clients/packages/api/src/openapi.d.ts`
- 新增：`src/clients/packages/skills/src/local-skills.ts`、`src/clients/packages/skills/src/local-skills.test.ts`
- 修改：`src/clients/packages/skills/src/index.ts`
- 新增（不提交）：`.scratch/local-skills/export-openapi.sh`

**接口：**
- 使用：任务 1 的 `SkillResponse.sourceDigest`。
- 产出（`@agw/skills` 入口导出）：
  - 类型 `LocalSkill`、`LocalSkillScan`、`LocalSkillPackage`（字段与设计文档第 4 节相同）、`LocalSkillStatus`、`LocalSkillsSource`（第 5 节）。
  - `export type ServerSkillSummary = { id: string; name: string; kind: "BuiltIn" | "Local" | "Remote"; isBuiltIn: boolean; sourceDigest?: string | null };`
  - `resolveLocalSkillStatus(skill: LocalSkill, serverSkills: readonly ServerSkillSummary[]): LocalSkillStatus`
  - `buildLocalSkillFormData(skill: LocalSkill, status: Extract<LocalSkillStatus, { kind: "New" | "Changed" }>, pkg: LocalSkillPackage): FormData`

- [x] **步骤 1：导出 OpenAPI 并生成类型**

创建 `.scratch/local-skills/export-openapi.sh`：用 `dotnet build src/server/Agw.Standalone.Host` 后的 `agw-server.dll` 启动 Host，参数为 `--environment=Development --urls=http://127.0.0.1:30931 --AgwDataDir=<仓库>/.scratch/local-skills/openapi-data --AgwLogDir=<仓库>/.scratch/local-skills/openapi-logs --Database:Provider=sqlite --Database:ConnectionString= --Execution:Provider=InProcess --DistributedLock:Provider=inmemory --Setup:AdminPassword=<随机值>`（命令行参数覆盖 Development 配置中的开发数据库），等待 `/openapi/v1.json` 返回 200 后保存到 `src/clients/packages/api/openapi.json`，然后结束进程。运行脚本后，在 `src` 下执行 `pnpm gen:api`。
期望：`openapi.d.ts` 的 `SkillResponse` 出现 `sourceDigest`，`SkillCreateRequest`、`SkillUpdateRequest` 出现 `SourceDigest`；其他差异只来自本计划的改动。

- [x] **步骤 2：写失败的测试 `local-skills.test.ts`**

用例名按设计文档「测试计划 / `@agw/skills`」。关键断言：

```ts
const digest = "sha256:fd5db01f805c3fa8eaec5b16afe3d3bc9c02ebf1b9e7081ade4674d50ba3384e";
const local: LocalSkill = {
  directoryName: "demo", name: "demo", description: "Demo skill.",
  sourceDigest: digest, fileCount: 2, totalBytes: 60, problem: null,
};

test("resolveLocalSkillStatus_SameDigest_ReturnsSynced", () => {
  assert.deepEqual(
    resolveLocalSkillStatus(local, [{ id: "s1", name: "demo", kind: "Local", isBuiltIn: false, sourceDigest: digest }]),
    { kind: "Synced", skillId: "s1" },
  );
});

test("buildLocalSkillFormData_New_IncludesKindAndDigest", () => {
  const data = buildLocalSkillFormData(local, { kind: "New" }, { ...pkgOf(local), bytes: new Uint8Array([80, 75]) });
  assert.equal(data.get("Kind"), "Local");
  assert.equal(data.get("SourceDigest"), digest);
  const archive = data.get("Archive") as File;
  assert.equal(archive.name, "demo.zip");
  assert.equal(archive.type, "application/zip");
});
```

`pkgOf` 是测试文件内的辅助函数，返回与给定 `LocalSkill` 的 `directoryName`、`name`、`description`、`sourceDigest` 相同，`fileName` 为 `<name>.zip` 的 `LocalSkillPackage`。`Conflict` 的原因文字为 `A built-in skill already uses this name.` 与 `A remote skill already uses this name.`；扫描后改名时抛出的消息为 `SKILL.md of demo changed after the scan. Rescan and try again.`。

- [x] **步骤 3：运行测试，确认失败**

运行：`pnpm --filter @agw/skills exec tsx --test src/local-skills.test.ts`
期望：失败，`local-skills` 模块不存在。

- [x] **步骤 4：实现 `local-skills.ts` 并在 `index.ts` 导出**

`buildLocalSkillFormData` 不包含 HTTP 调用；`Changed` 的提交地址由调用方使用 `status.skillId`。

- [x] **步骤 5：运行测试与类型检查，确认通过**

运行：`pnpm --filter @agw/skills exec tsx --test src/local-skills.test.ts`、`pnpm --filter @agw/skills typecheck`、`pnpm --filter @agw/api typecheck`
期望：全部通过。

---

### 任务 5：Desktop 主进程扫描、摘要与打包

**文件：**
- 新增：`src/clients/desktop/src/shared/contracts/local-skills.ts`；修改 `src/clients/desktop/src/shared/contracts/index.ts`
- 新增：`src/clients/desktop/src/main/local-skills/local-skills.ts`、`local-skills.test.ts`
- 修改：`src/clients/desktop/package.json`（`dependencies` 增加 `"vfile": "catalog:"`、`"vfile-matter": "catalog:"`）

**接口：**
- 产出：
  - `scanLocalSkills(homeDirectory: string): Promise<LocalSkillScan>`
  - `packLocalSkill(homeDirectory: string, directoryName: string): Promise<LocalSkillPackage>`
  - 类型 `LocalSkill`、`LocalSkillScan`、`LocalSkillPackage`（设计文档第 4 节）。

- [x] **步骤 1：写失败的测试 `local-skills.test.ts`**

每个用例用 `createScratchDirectory("local-skills")` 作为 home 目录，在 `<home>/.agents/skills` 下建立文件；用例名与断言按设计文档「测试计划 / Desktop 主进程」的 17 个用例，另外加入：

```ts
test("scanLocalSkills_FixedTree_MatchesGoldenDigest", async (t) => {
  // demo/SKILL.md 与 demo/scripts/run.py 的内容固定，期望值由设计文档第 2 节的算法计算。
  // Fixed contents for demo/SKILL.md and demo/scripts/run.py; the expected value follows section 2 of the design.
  await writeSkill(home, "demo", {
    "SKILL.md": "---\nname: demo\ndescription: Demo skill.\n---\nUse it.\n",
    "scripts/run.py": "print('hi')\n",
  });
  const scan = await scanLocalSkills(home);
  assert.equal(scan.skills[0].sourceDigest, "sha256:fd5db01f805c3fa8eaec5b16afe3d3bc9c02ebf1b9e7081ade4674d50ba3384e");
});

test("scanLocalSkills_BomAndCrlfFrontMatter_ParsesMetadata", async (t) => {
  // SKILL.md 为 "﻿---\r\nname: demo\r\ndescription: Demo skill.\r\n---\r\nUse it.\r\n"
  // 断言 name === "demo"、description === "Demo skill."、problem === null
});

test("scanLocalSkills_TopLevelSymlinkToRoot_ReportsProblemWithoutLooping", async (t) => {
  // <home>/.agents/skills/loop -> <home>/.agents/skills；断言扫描完成，loop 的 problem 不为 null
});

test("packLocalSkill_NonAsciiFileName_PreservesEntryName", async (t) => {
  // demo/参考/说明.md；用 yauzl 读回 ZIP，条目名包含 "参考/说明.md"
});
```

`writeSkill` 是测试文件内的辅助函数，按相对路径创建目录并写入 UTF-8 文本。符号链接用 `node:fs/promises` 的 `symlink` 创建，与 `src/main/extensions/extension-catalog.test.ts` 相同。

- [x] **步骤 2：运行测试，确认失败**

运行：`pnpm --filter @agw/desktop exec tsx --test src/main/local-skills/local-skills.test.ts`
期望：失败，`./local-skills` 模块不存在。

- [x] **步骤 3：安装依赖**

在 `src` 下执行：`pnpm install`。
期望：`pnpm-lock.yaml` 中 `@agw/desktop` 增加 `vfile`、`vfile-matter`，版本与 catalog 一致。

- [x] **步骤 4：实现 `local-skills.ts`**

- 按设计文档第 1 至 3 节实现。`relativePath` 用 `path.relative` 计算后把 `path.sep` 换成 `/`。
- 读取 `SKILL.md` 后去掉开头的 `﻿`，再交给 `vfile-matter`；`matter(file)` 之后从 `file.data.matter` 读取 `name`、`description`。没有 front matter 的判断沿用 `src/extensions/markdown/src/markdown/front-matter.ts` 的方式（去掉 front matter 前后长度相同）。YAML 无效时 `vfile-matter` 抛出 `YAMLParseError`，转换为 `Front matter isn't valid YAML: <message>`。
- 扫描时用 `createReadStream` 与 `createHash("sha256")` 流式计算；打包时 `readFile` 后对 `Buffer` 计算；两条路径共用把 `[relativePath, sha256Hex]` 列表转换为 sourceDigest 的函数。
- 打包：`new yazl.ZipFile()`，`addBuffer(bytes, relativePath)`，`end()` 后把 `outputStream` 收集为一个 `Buffer`，返回 `new Uint8Array(buffer)`。

- [x] **步骤 5：运行测试与类型检查，确认通过**

运行：`pnpm --filter @agw/desktop exec tsx --test src/main/local-skills/local-skills.test.ts`、`pnpm --filter @agw/desktop typecheck`
期望：全部通过。

---

### 任务 6：Desktop bridge、扫描对话框与页面

**文件：**
- 修改：`src/clients/desktop/src/shared/contracts/bridge.ts`、`src/clients/desktop/src/preload/index.ts`、`src/clients/desktop/src/main/index.ts`
- 新增：`src/clients/desktop/renderer/src/runtime/local-skills-source.ts`；修改 `src/clients/desktop/renderer/src/runtime/index.ts`
- 修改：`src/clients/desktop/renderer/src/app/(app)/skills/page.tsx`
- 新增：`src/clients/packages/skills/src/ui-web/pages/skills/local-skills-dialog.tsx`
- 修改：`src/clients/packages/skills/src/ui-web/pages/skills/page.tsx`
- 修改：`site/content/docs/guides/tools-skills.zh.md`、`site/content/docs/guides/tools-skills.en.md`

**接口：**
- 使用：任务 4 的 `LocalSkillsSource`、`resolveLocalSkillStatus`、`buildLocalSkillFormData`；任务 5 的 `scanLocalSkills`、`packLocalSkill`。
- 产出：
  - `AgwDesktopBridge.scanLocalSkills(): Promise<LocalSkillScan>`、`AgwDesktopBridge.packLocalSkill(directoryName: string): Promise<LocalSkillPackage>`。
  - `export const desktopLocalSkills: LocalSkillsSource`（`@/runtime` 导出）。
  - `SkillsPage` 属性 `{ localSkills?: LocalSkillsSource }`；`SkillDto` 增加 `sourceDigest?: string | null`。
  - `LocalSkillsDialog` 属性 `{ source: LocalSkillsSource; open: boolean; onOpenChange(open: boolean): void }`。

- [x] **步骤 1：bridge、preload 与 IPC**

- `bridge.ts` 增加两个方法（设计文档第 4 节的注释）；`preload/index.ts` 映射到两个通道。
- `main/index.ts` 在其他 `ipcMain.handle` 附近注册：

```ts
ipcMain.handle("agw:scan-local-skills", (event) => {
  assertTrustedSender(senderUrl(event));
  return scanLocalSkills(os.homedir());
});
ipcMain.handle("agw:pack-local-skill", (event, directoryName: string) => {
  assertTrustedSender(senderUrl(event));
  return packLocalSkill(os.homedir(), directoryName);
});
```

- [x] **步骤 2：renderer 适配与路由**

- `local-skills-source.ts`：`scan` 与 `pack` 调用 `window.agwDesktop`；bridge 不存在时抛出 `new Error("Agw Desktop bridge is unavailable.")`。
- 路由文件改为设计文档第 6 节的 `DesktopSkillsPage`。

- [x] **步骤 3：`LocalSkillsDialog` 与 `SkillsPage`**

- 按设计文档第 6 节实现，组件使用 `@agw/components` 的 `Dialog`、`StaticTable`/`Table*`、`Checkbox`、`Badge`、`Button`、`Empty`；查询使用 `@agw/components/query` 的 `useQuery`、`useQueryClient`。
- 导入逻辑：对勾选的行依次执行 `source.pack(directoryName)` → `buildLocalSkillFormData` → `apiPost("/api/skills", { body })` 或 `apiPut("/api/skills/{id}", { params: { path: { id } }, body })`（与 `page.tsx` 现有的更新调用写法相同）；第一个失败时用 `getApiErrorMessage(error)` 写入这一行的 Result 并停止。
- `SkillsPage` 工具栏在 `Add Skill` 左侧加入 `Scan ~/.agents/skills` 按钮，只在 `localSkills` 存在时显示。

- [x] **步骤 4：站点文档**

`tools-skills.zh.md` 与 `tools-skills.en.md` 在「Local 与 Remote Skill」之后加入「从 ~/.agents/skills 导入」（英文为 "Import from ~/.agents/skills"）一节：只在 Desktop 中提供；五种状态的含义；`Changed` 会替换服务器文件；服务器保存的是上传时的文件，本机修改后需要重新扫描并更新。

- [x] **步骤 5：检查**

在 `src` 下运行：`pnpm --filter @agw/skills typecheck`、`pnpm --filter @agw/skills lint`、`pnpm --filter @agw/web typecheck`（Web 使用同一个 `SkillsPage`）、`pnpm --filter @agw/desktop typecheck`、`pnpm --filter @agw/desktop lint`、`pnpm --filter @agw/desktop test`、`pnpm --filter @agw/skills test`、`pnpm test:boundaries`、`pnpm fmt`。
期望：全部通过，没有新的 lint 警告；`test:boundaries` 没有报告 `@agw/skills` 引用 Desktop 代码。

---

### 任务 7：单节点集成测试

**文件：**
- 新增：`src/clients/desktop/src/main/local-skills/local-skills.integration.ts`

**接口：**
- 使用：任务 5 的 `scanLocalSkills`、`packLocalSkill`；任务 1、3 的服务器行为。

- [x] **步骤 1：编写集成测试**

- 用 `spawn("dotnet", [<repo>/src/server/Agw.Standalone.Host/bin/Debug/net10.0/agw-server.dll, "--environment=Production", "--urls=http://127.0.0.1:<空闲端口>", "--AgwDataDir=<scratch>", "--Setup:AdminPassword=<随机值>"])` 启动 Host，环境变量去掉 `ASPNETCORE_ENVIRONMENT`、`ASPNETCORE_URLS`；结束时 `SIGTERM` 并等待退出（与 `renderer/src/runtime/server-connection.integration.ts` 相同）。
- 登录按 `scripts/test-extensions.mjs` 第 563–567 行：`GET /api/auth/antiforgery` → `POST /api/auth/login` → `POST /api/auth/tokens`，之后用 Bearer Token。
- 用例 `localSkills_ImportUpdateAndLimits_MatchServer`，依次断言：
  1. scratch home 下的 `demo`（含 `参考/说明.md`）扫描后打包，`POST /api/skills` 成功；`GET /api/skills` 中 `demo` 的 `sourceDigest` 等于扫描结果。
  2. 服务器内容目录（`<AgwDataDir>/<contentPath>`）中存在 `参考/说明.md`，内容与本机相同。
  3. 修改 `scripts/run.py` 后重新扫描，摘要与服务器不同；`PUT /api/skills/{id}` 后 `sourceDigest` 相同，旧内容目录与 `.retired` 都不存在。
  4. 包含 40 MB 随机字节资源文件的 Skill 导入成功。
  5. 包含 110 MB 随机字节资源文件的 Skill 返回 HTTP 413。
  6. 同名 Skill 再次 `POST` 返回 `SkillAlreadyExists` 的错误码。

- [x] **步骤 2：运行**

在仓库根目录先执行 `dotnet build src/server/Agw.Standalone.Host`，再在 `src/clients/desktop` 下执行 `pnpm exec tsx --test src/main/local-skills/local-skills.integration.ts`。
期望：通过；`.scratch` 下没有残留的 Host 进程（`pgrep -f agw-server.dll` 没有本测试的进程）。

---

### 任务 8：Desktop 界面场景

**文件：**
- 修改：`src/clients/desktop/scripts/test-extensions.mjs`
- 修改：`src/clients/desktop/src/main/extensions.integration.ts`

**接口：**
- 使用：任务 6 的页面与对话框。

- [x] **步骤 1：增加 scenario `local-skills`**

- `test-extensions.mjs`：允许的 scenario 列表加入 `local-skills`，phase 为 `["local-skills"]`；运行这个 phase 前在 `<root>/local-skills-home/.agents/skills` 下写入 `alpha`（有效）、`beta`（有效，服务器上预先用同名 Local Skill 创建、内容不同）、`broken`（没有 `SKILL.md`）；Electron 以 `env: { ...process.env, HOME: <local-skills-home>, USERPROFILE: <local-skills-home> }` 启动。
- `extensions.integration.ts`：`phase === "local-skills"` 时导航到 `/skills`，用 `clickText("Scan ~/.agents/skills")` 打开对话框，`untilHost` 等待表格出现，断言 `alpha` 为 `New` 并且已经勾选、`beta` 为 `Changed` 并且没有勾选、`broken` 为 `Invalid` 并且显示 `SKILL.md is missing.`。勾选 `beta` 后点击 `Import (2)`：`alpha` 显示 `Imported`、`beta` 显示 `Updated`，之后状态都为 `Synced`；`hostApi("/api/skills")` 中两者的 `sourceDigest` 与扫描结果相同。
- Review Focus 第 5 项：重新写入 `gamma`，扫描后用 `hostSend("POST", ...)` 在服务器上先创建同名 Skill，再在对话框中导入 `gamma` 与之后的一项：`gamma` 行显示 `Skill 'gamma' already exists.`，之后的一项保持未处理。

- [ ] **步骤 2：运行**

先请用户确认，然后在 `src/clients/desktop` 下执行：`pnpm test:extensions -- --scenario local-skills`。
期望：输出每个断言的 `pass` 行，退出码为 0。

---

### 任务 9：多节点验收

**文件（不提交）：**
- 新增：`.scratch/local-skills/multi-node/run.sh` 及其使用的配置

**接口：**
- 使用：任务 1–7 的全部行为。

- [x] **步骤 0：全量测试**

在仓库根目录运行 `env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS dotnet test Agw.slnx`，在 `src` 下运行 `pnpm test`。
期望：全部通过（集成与组件测试全部通过之后才运行下面的验收）。

- [x] **步骤 1：编写验收脚本**

按记忆中的本机 data-plane 与 e2e 配方：
- PostgreSQL 使用本机 `localhost:5432`，新建独立的 `agw_local_skills_accept_<时间>` 库，结束时删除。
- 一个 Control Plane 与两个 Data Plane（`dotnet` 直接运行已经构建的 dll，`--environment=Production`，`Execution:Provider=Distributed`、`Database:Provider=postgres`、`DistributedLock:Provider=postgres`）。三个进程使用同一个 `AgwDataDir`，各自配置稳定的 `Extensions:HostInstanceName`。
- podman 运行 nginx，配置由 `deploy/nginx.split.conf.example` 改为 HTTP 监听并把 upstream 指向 `host.containers.internal`。
- 模型 Provider 使用把 system 消息原样作为回复的 OpenAI 兼容回显服务。

- [x] **步骤 2：执行并检查**

运行 `.scratch/local-skills/multi-node/run.sh`，检查设计文档「测试计划 / 集成测试」第 2 项的全部断言：
- 经 nginx 导入 40 MiB 的 Skill 成功。
- 两个 Data Plane 回复的 `# Skills` 段落包含导入的 Skill 名称与描述。
- 更新 `description` 后两个 Data Plane 都返回新描述，旧内容目录已经删除。
- 手动写入一个 `.retired` 标记，重启一个 Data Plane 后对应目录被删除。

期望：脚本以 0 退出，并打印每项检查的结果。
