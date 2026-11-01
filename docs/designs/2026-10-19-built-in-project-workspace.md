# Project Workspace 与 AdditionalDirectories 目录创建修复计划

关联：[issue #196](https://github.com/zxyao145/agw/issues/196)。

## 背景

Project 的主工作目录由 `Workspace` 或项目默认路径确定，附加目录由 `AdditionalDirectories` 配置。普通项目的创建和更新通过 `ProjectAppService` 处理；管理员内置项目由 `DbSeeder` 初始化，OIDC 用户的内置项目由 `UserProjectInitializer` 初始化。文件访问通过 `ProjectScopedFileSystemResolver`，InProcess 和 Durable 执行通过 `ProjectWorkspacePaths.EnsureAvailable` 准备目录。shell、外部 Agent 和 GitHub integration 也会使用项目目录。

本方案要求在创建或更新项目目录配置时，以及每次实际使用目录前，分别保证 `Workspace` 和 `AdditionalDirectories` 中对应目录存在，缺失时自动创建。该要求适用于内置项目、普通项目和自定义路径。

## 设计目标

### 功能目标

1. 创建或更新 project 的 `Workspace` 或 `AdditionalDirectories` 时，检查有效主目录和全部已配置附加目录，缺失时创建，全部成功后保存项目。
2. 每次实际使用主目录或附加目录前，检查对应目录，缺失时创建，包括文件系统缓存命中、后续工具调用、后续 Turn、Durable Segment、进程目录挂载和恢复执行。
3. 检查对象为通过现有路径格式与访问权限验证后得到的规范化绝对路径；空 `Workspace` 使用该项目现有的默认路径，附加目录使用其各自配置的路径。
4. 已有目录和文件保持完整；目录重新创建后，使用该路径的新目录。
5. 目录无法创建、权限不足或普通文件占用目录路径时，立即终止当前配置或使用操作，并通过现有异常处理流程报告失败。

### 技术目标

- 在现有 `ProjectWorkspacePaths` 中集中提供目录创建方法，主目录和附加目录的各个入口复用同一方法。
- 在验证项目归属和操作权限之后执行文件系统操作。
- 执行时使用该次执行捕获的 `ProjectWorkspaceSnapshot`。
- `ProjectBehavior` 负责路径格式、目录重复、主目录与附加目录冲突等规则验证；Application 和 Infrastructure 负责目录创建。
- 测试使用生产实现、真实 SQLite 和项目目录内的真实文件系统。

## 详细设计

### 1. 共用目录创建方法

修改 `src/server/Agw.Shared/Utils/ProjectWorkspacePaths.cs`，增加：

```csharp
public static string EnsureCreated(string directoryPath)
{
    var path = Normalize(directoryPath);
    Directory.CreateDirectory(path);
    return path;
}
```

`Directory.CreateDirectory` 具有幂等语义，目录存在时保留目录和内容，缺失时创建需要的上级目录和目标目录。创建失败直接向上传播。

主目录和每个附加目录均调用该方法。空 `Workspace` 的调用者先解析对应项目的默认路径，再调用该方法。返回值用于实际文件系统访问、进程工作目录或附加目录配置及挂载。

### 2. 创建和更新项目目录配置时保证目录存在

配置入口分别处理：

- **普通项目**：`src/server/Agw.Projects/Application/ProjectAppService.cs` 的 `CreateAsync` 和 `UpdateAsync` 在项目归属与目录配置规则验证完成后，对主目录和每个附加目录调用共用方法，再执行保存。修改路径、新增附加目录以及原路径缺失三种情况均需覆盖。
- **管理员内置项目**：`src/server/Agw.Infrastructure/Data/DbSeeder.cs` 在创建或更新内置项目目录配置时准备有效主目录和配置中的附加目录。处理本次初始化实际使用的路径，包含 `default-built-in` 和 `a2a`。
- **OIDC 用户内置项目**：`src/server/Agw.Projects/Application/UserProjectInitializer.cs` 在创建每个项目后，通过该项目 ID 解析有效默认路径，在保存前创建主目录及配置中的附加目录。数据库中的 `Workspace` 可以继续为空，目录准备使用其有效值。

`src/server/Agw.Projects/Domain/Behaviors/ProjectBehavior.cs` 保留路径格式、绝对路径要求、目录重复和目录冲突验证。缺失的合法附加目录进入 Application 的目录创建步骤。移除 `EnsureAdditionalDirectoriesExist` 及其在 `ProjectAppService` 中的调用和 `ResolveExistingAdditionalDirectoryPaths` 辅助方法；配置规则验证通过后，由 Application 对所有有效目录调用 `EnsureCreated`。

全部路径准备成功后才保存新配置。目录准备保留项目 ID、用户归属和用户指定的各个路径。附加目录 ID 遵循现有规则：相同 ID 与规范化路径组合保留 ID，路径改变时生成新 ID。

### 3. 每次文件访问前保证目录存在

修改 `src/server/Agw.Files/Infrastructure/Storage/ProjectScopedFileSystemResolver.cs` 的 `ResolveCoreAsync`：

1. 查询或重新验证项目归属。
2. 按当前配置或当前执行 snapshot 解析访问路径。
3. 校验选择的 `directoryId` 属于当前项目配置或本次执行 snapshot；有效附加目录 ID 对应其配置路径，主目录使用有效 Workspace。
4. 对实际选中的主目录或附加目录，每次调用 `EnsureCreated`，检查位置在返回缓存文件系统之前。
5. 返回缓存前验证相关目录的物理身份；目录重新创建或身份变化时，使受影响的缓存文件系统失效，并建立新的 `LocalFileSystem` 实例。身份检查复用现有 native 文件系统状态读取方法。

`ProjectAgentFileStore` 根据 resolver 返回的实例缓存 confined 文件系统。构建或复用 confined 文件系统前，准备其允许访问的主目录及附加目录，并核对整个允许目录集合的物理身份。任一目录重新创建后，刷新受影响的 source 实例和 confined 缓存，使访问边界对应当前目录。Unix 侧使用 `UnixNative.Stat` 的 device/inode 身份，Windows 侧使用 `WindowsNative.GetStatus` 返回的卷序列号和文件索引身份。目录身份检查与现有符号链接、`GetFinalPath` 和路径访问检查共同执行。即使执行入口已经提前创建目录，也需要识别缓存是否仍然适用。

此入口覆盖 Files API、Agent 文件工具和 ProjectMemory 的每次文件系统解析。

### 4. 每次 Agent 执行前保证目录存在

修改 `ProjectWorkspacePaths.EnsureAvailable`，对 snapshot 的主目录和每个附加目录统一执行 `EnsureCreated`：

```csharp
public static void EnsureAvailable(ProjectWorkspaceSnapshot snapshot)
{
    EnsureCreated(snapshot.Workspace);
    foreach (var directory in snapshot.AdditionalDirectories)
    {
        EnsureCreated(directory.Path);
    }
}
```

更新以下调用者以使用该签名：

- `src/server/Agw.Agents.Execution/Runtimes/InProcess/InProcessExecutionCoordinator.cs`：每次 `StartAsync` 使用本次请求的 snapshot 检查目录。
- `src/server/Agw.Agents.Execution/Runtimes/Durable/DurableExecutionCoordinator.cs`：每次 `ExecuteSegmentAsync` 使用 manifest 中的 snapshot 检查目录，覆盖恢复和后续 Segment。

项目配置变化后，已有执行继续检查其捕获的 Workspace 和附加目录集合，新执行使用新配置。snapshot 中的合法目录即使已经从当前配置移除，已有执行也按其捕获的配置准备目录。

### 5. 直接使用项目目录的入口

以下入口在实际使用主目录或附加目录前执行同一检查：

- `src/server/Agw.Tools/Impl/ContextualTools/Shell/ShellContextualTool.cs`：创建 Local/Docker executor 前准备主目录和本次使用的附加目录；通过现有工具调用边界在每次 shell 调用前再次检查。Docker 附加目录在挂载前创建。任一被绑定目录重新创建或物理身份变化时，刷新对应的 persistent executor 和挂载绑定，使后续命令使用当前目录。
- `src/server/Agw.Agents.Execution/Agents/Runtime/AgentRuntimeFactory.CreateExternalAgents.cs`：Claude Code、Codex 和 Pi 的启动、继续执行入口在使用 `WorkingDirectory` 和传递附加目录前，准备本次执行的主目录及附加目录。复用会话或 runtime 时，每次进入执行也需检查；与旧物理目录绑定的进程需要刷新对应目录绑定。
- `src/server/Agw.Projects/Application/Facades/ProjectRuntimeFacade.cs` 的 `GetWorkspaceAsync`：取得当前用户可见项目后，准备调用方即将使用的有效主目录，并返回有效绝对路径。该入口覆盖 `src/server/Agw.Integrations/Tools/GitHub/ProjectWorkspaceResolver.cs`。通过目录配置或 snapshot 使用附加目录的调用方，在使用对应目录前执行同一准备方法。

已有数据查询和 snapshot 构造保持各自职责，目录检查放在已授权的目录使用入口。检查与实际操作之间发生外部目录变动时，实际操作的文件系统错误直接向上传播。

### 6. 初始化路径和测试隔离

在 `src/server/Agw.Shared/Runtime/AgwDataPaths.cs` 保留 `Resolve` 已接收的绝对 `userHome`，提供只读属性 `UserHomeDirectory`。`DbSeeder` 与 `UserProjectInitializer` 通过注入的路径上下文展开初始化路径，再调用共用目录创建方法。

生产路径上下文来自系统 `UserProfile`。测试将 data root 和 home 放在 `Path.Combine(AppContext.BaseDirectory, ".scratch", ...)` 下，并通过现有 `AgwDataPaths.Resolve(configuredRoot, userHome)` 传入。更新直接构造 `UserProjectInitializer` 的测试 fixture，使用同一隔离路径上下文。`.scratch/` 已在仓库 `.gitignore` 中。

使用阶段的测试提供隔离目录中的绝对 Workspace 和附加目录，验证真实 resolver、snapshot 和工具入口。初始化测试单独验证保存的 `~` 路径与有效目录之间的对应关系。

## 实施步骤

- [ ] **编写回归用例并确认失败。** 覆盖主目录和附加目录在配置时、使用时以及缓存命中后缺失的情况；失败断言检查具体目录与实际操作结果。
- [ ] **实现共用目录创建方法。** 增加 `EnsureCreated`，更新 `EnsureAvailable` 及其调用者，验证目录存在、重复调用和真实创建失败。
- [ ] **覆盖所有配置入口。** 更新 `ProjectBehavior` 的目录规则验证、`ProjectAppService`、`DbSeeder`、`UserProjectInitializer` 和初始化路径上下文，运行对应真实数据库测试。
- [ ] **覆盖所有使用入口。** 更新文件 resolver、`ProjectAgentFileStore` 的 confined 缓存准备、shell、外部 Agent 和 Workspace facade；验证任一允许目录重新创建后的缓存对象、persistent executor 和挂载绑定。
- [ ] **完成集成测试与架构检查。** 检查所有 `Project.Workspace`、`AdditionalDirectories`、`ProjectWorkspaceSnapshot`、`WorkingDirectory` 和附加目录挂载的直接消费者，确认实际使用前执行目录检查。
- [ ] **执行构建与主流程验收。** 单元、集成和组件测试全部通过后，执行首次安装以及运行中目录缺失后的文件和 Agent 操作。

## 测试计划

### 共用目录创建

新增 `tests/Agw.Shared.Tests/Utils/ProjectWorkspacePathsTests.cs`：

- `EnsureCreated_MissingDirectory_CreatesDirectoryAndParents`：对主目录和附加目录分别验证父目录及目标目录缺失时的创建结果。
- `EnsureCreated_ExistingDirectory_PreservesFiles`：对主目录和附加目录重复调用，断言已有文件内容保持完整。
- `EnsureCreated_FileOccupiesPath_FailsWithoutChangingFile`：真实文件占用目录路径，断言操作失败且文件完整。
- `EnsureAvailable_CustomWorkspaceMissing_CreatesPrimaryDirectory`：snapshot 使用自定义绝对主目录，断言目录创建成功。
- `EnsureAvailable_AdditionalDirectoriesMissing_CreatesAllDirectories`：snapshot 的主目录和多个附加目录均缺失，断言全部目录创建成功。
- `EnsureAvailable_AdditionalDirectoryOccupiedByFile_FailsWithoutChangingFile`：真实文件占用附加目录路径，断言操作失败且文件保持完整。

### 创建和更新集成测试

新增 `tests/Agw.Projects.Tests/ProjectWorkspaceProvisioningTests.cs`，使用真实 `ProjectAppService` 与 SQLite：

- 创建普通项目时，缺失的自定义主目录和多个附加目录均被创建。
- 更新 Workspace 或新增、修改附加目录路径时，所有有效目标目录被创建，原目录已有文件保持完整。
- 原 Workspace 或附加目录被移走后，再次保存相同配置，缺失目录重新创建，附加目录 ID 保持一致。
- 附加目录的相对路径、空值、目录重复和主目录冲突等无效配置在创建目录前被拒绝。
- 主目录或任一附加目录的真实创建失败时，新配置没有保存，已有文件保持完整。
- 使用其他用户身份更新项目时，项目不可见，目标目录没有创建。

扩展 `tests/Agw.Setup.Tests/DbSeederTests.cs`，验证管理员两个内置项目的创建、重复初始化和已有文件保留。新增 `tests/Agw.Projects.Tests/UserProjectInitializerWorkspaceTests.cs`，使用真实初始化器和 SQLite，验证 OIDC 两个默认目录在保存前创建，以及数据库 `Workspace` 为空时的有效默认目录。

调整 `tests/Agw.Projects.Tests/ProjectAppServiceTests.cs` 中 `CreateAsync_InvalidAdditionalDirectory_RejectsConfiguration` 的 `missing` 场景，使其成为目录创建成功的用例，其他无效配置继续验证拒绝行为。扩展 `UpdateAsync_AdditionalDirectories_PreserveIdentityUntilPathChanges`，覆盖附加目录新增、修改和路径相同但目录缺失的情况，验证 ID 规则。`tests/Agw.Projects.Tests/ProjectBehaviorTests.cs` 继续验证路径格式、规范化、目录 ID 和重复及冲突规则。

### 文件访问和缓存集成测试

新增 `tests/Agw.Files.Tests/ProjectWorkspaceRecreationTests.cs`，通过生产 DI、真实项目记录和 resolver 验证：

- 缺失的内置项目主目录、自定义主目录和通过合法 `directoryId` 选择的附加目录，在文件列表请求前创建。
- 首次访问建立缓存后，分别将主目录和附加目录移动到 fixture 的另一路径；后续访问创建对应目录，返回该目录的空列表，随后可以完成真实写入和读取。
- 首次访问建立缓存后，移动主目录或附加目录，再由 `EnsureAvailable` 提前创建对应目录；文件 resolver 仍能识别物理身份变化并使用新目录。
- 同一 `ProjectAgentFileStore` 再次调用时使用当前允许目录集合；仅附加目录发生身份变化时，主目录入口持有的 confined 缓存也得到刷新。
- snapshot 使用捕获的主目录和附加目录集合；当前配置修改路径或移除附加目录后，已有执行仍按其捕获路径检查并创建目录，新请求使用当前配置。
- 未配置的 `directoryId` 和未授权项目访问不会创建目录；访问已配置附加目录时，实际使用对应路径。

调整 `tests/Agw.Files.Tests/ProjectDirectoryTests.cs` 中 `ResolveAsync_UnavailableAdditionalDirectory_FailsWithoutPrimaryFallback` 的测试名称和预期，验证选中的附加目录被创建且文件操作使用该目录。扩展 `ResolveAsync_RemovedDirectory_IsAvailableOnlyToOriginalExecutionSnapshot`，覆盖捕获目录缺失时的创建行为及新旧配置的可见性。

运行现有 `ConfinedFileSystemTests` 和 `LocalFileSystemPathSecurityTests` 中的目录边界与符号链接测试，验证目录身份检查与访问控制共同生效；相应 fixture 使用项目内部目录。

### 执行和工具组件测试

新增 `tests/Agw.Agents.Tests/ExecutionWorkspaceProvisioningTests.cs`，验证 InProcess 启动、Durable 首个和后续 Segment、恢复执行，以及外部 Agent 的启动和继续执行都能创建缺失的主目录及附加目录。

在 `tests/Agw.Tools.Tests/ShellContextualToolTests.cs` 中补充连续 shell 调用之间主目录或附加目录被移走的用例，使用生产 executor 和实际命令，验证目录重新创建后进程工作目录及附加目录绑定正确，旧目录内容没有被当作新目录内容使用。Docker 用例验证附加目录在首次挂载前创建，以及后续目录重新创建后的实际挂载结果。

在 `tests/Agw.Projects.Tests/ProjectWorkspaceProvisioningTests.cs` 中验证生产 `ProjectRuntimeFacade.GetWorkspaceAsync` 能创建目录、返回规范化绝对路径，并在主目录被移走后再次完成目录准备。空 Workspace 使用该项目的有效默认目录。

对初始化入口中存在附加目录配置的场景，验证全部配置目录在保存前准备完成；没有配置附加目录时，按当前有效主目录执行同一流程。

所有新增用例使用真实实现和项目目录内的独立 fixture。外部 Agent 的执行测试使用配置好的实际 provider；测试环境无法运行某个 provider 时，记录该项验证限制，不使用替代实现宣称通过。

### 验证命令与顺序

在仓库根目录执行：

```bash
dotnet test tests/Agw.Shared.Tests --filter "FullyQualifiedName~ProjectWorkspacePathsTests"
dotnet test tests/Agw.Projects.Tests --filter "FullyQualifiedName~ProjectWorkspaceProvisioningTests|FullyQualifiedName~UserProjectInitializerWorkspaceTests"
dotnet test tests/Agw.Projects.Tests --filter "FullyQualifiedName~ProjectAppServiceTests|FullyQualifiedName~ProjectBehaviorTests"
dotnet test tests/Agw.Setup.Tests --filter "FullyQualifiedName~DbSeederTests"
dotnet test tests/Agw.Files.Tests --filter "FullyQualifiedName~ProjectWorkspaceRecreationTests"
dotnet test tests/Agw.Files.Tests --filter "FullyQualifiedName~ProjectDirectoryTests"
dotnet test tests/Agw.Files.Tests --filter "FullyQualifiedName~ConfinedFileSystemTests|FullyQualifiedName~LocalFileSystemPathSecurityTests"
dotnet test tests/Agw.Agents.Tests --filter "FullyQualifiedName~ExecutionWorkspaceProvisioningTests"
dotnet test tests/Agw.Tools.Tests --filter "FullyQualifiedName~ShellContextualToolTests"
dotnet test tests/Agw.Architecture.Tests
dotnet build Agw.slnx
```

执行现有测试前，确认其 fixture 使用项目内部目录；本次涉及的 fixture 统一改为项目内的 `.scratch` 路径。格式检查只处理本次修改的 C# 文件。

### 主流程验收

在单元、集成和组件检查全部通过后执行：

1. 完成首次 Setup，登录并访问默认项目文件列表，验证有效主目录已经存在。
2. 创建和更新使用自定义 Workspace 和多个附加目录的项目，检查全部目录并分别完成文件读写。
3. 在独立验收环境分别将主目录和附加目录移动到备份路径，保持 Server 运行；再次访问对应目录，验证目录创建，再完成文件读写和 Agent 工具调用。
4. 在连续 Turn、Durable 恢复和连续 shell 调用之间重复主目录和附加目录缺失场景，验证实际工作目录、附加目录访问和 Docker 挂载结果。
5. 在使用独立 Hosts 的部署中，由真正执行操作的 Host 在使用前检查主目录及附加目录，完成文件和 Agent 请求。

## 交付范围

实现覆盖 `Agw.Shared` 的目录方法与路径上下文、Projects 的目录规则验证、创建更新和初始化入口、`DbSeeder`、Files resolver 和 confined 缓存、InProcess/Durable 调用点、shell 与外部 Agent 的目录使用及挂载入口，以及 Workspace facade。测试同时覆盖 `Workspace` 和 `AdditionalDirectories` 在配置时及每次使用前的目录准备。
