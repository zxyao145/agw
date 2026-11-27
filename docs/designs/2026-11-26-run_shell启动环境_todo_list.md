# run_shell 使用 login shell 的 PATH：实施计划

> **给执行计划的 agent：** 必须使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 按任务逐个实施。步骤使用 checkbox（`- [ ]`）记录进度。

**目标：** 本机 `run_shell` 的 shell 使用运行 Server 的账号的 login shell 提供的 `PATH`，使 Desktop 通过 launchd 启动的 Server 上，System Agent 可以运行 Homebrew、npm 等位置安装的 CLI。

**架构：** `ShellContextualTool.MaterializeAsync` 在 backend 为 `local` 时生成一次执行器环境。执行环境变量没有 `PATH`，并且查询环境有受支持的 `SHELL` 时，新增的 `LoginShellPathReader` 启动 `$SHELL -il +m -c` 读取 `PATH`，结果写入 `LocalShellExecutorOptions.Environment["PATH"]`。`CleanEnvironment = true` 保持。查询失败抛出 `AgwException(ErrorCodes.ShellLoginPathUnavailable)`，本次 Agent 创建结束。

**技术栈：** .NET 10、Microsoft.Agents.AI.Tools.Shell 1.21.0-preview.260911.1、xUnit v3（Microsoft.Testing.Platform）、`/bin/zsh`。

**设计文档：** `docs/designs/2026-11-26-run_shell启动环境.md`。执行每个任务之前阅读设计文档的对应章节，下文用“设计 x.y”指代。

## 全局约束

- 没有兼容性要求。
- `CreateLocalExecutor` 的 `Mode`、`WorkingDirectory`、`ConfineWorkingDirectory`、`CleanEnvironment = true`、`Policy`、`Timeout`、`AcknowledgeUnsafe` 保持现值。
- 只在以下条件同时成立时查询 login shell：backend 为 `local`；`OperatingSystem.IsWindows()` 为 `false`；`context.EnvironmentVariables` 没有键 `PATH`；`LoginShellPathReader.HasLoginShell(查询环境)` 为 `true`。
- 查询环境：Server 进程环境中的 `PATH`、`HOME`、`USER`、`SHELL`（存在的项），再叠加 `context.EnvironmentVariables`。
- 查询命令为 `<SHELL> -il +m -c <脚本>`，其中 `+m` 关闭 job control。脚本固定为 `command -p printf '\0%s\0' "$PATH"`。
- 用查询环境中的非空 `HOME` 展开 `PATH` 中单独的 `~` 和以 `~/` 开头的项。其余项保留原值。执行器按命令运行时的工作目录解析相对目录。
- 查询超时 `LoginShellPathReader.DefaultTimeout` 为 3 秒。`PATH` 的长度上限为 64 KiB（65536 个字符）。进程退出后在查询时限内最多继续等待读取 1 秒。清理时最多等待 1 秒。
- 受支持的 shell 文件名：`sh`、`bash`、`zsh`、`dash`、`ksh`，按 `StringComparer.Ordinal` 匹配。
- 错误码（`src/server/Agw.Shared/Exceptions/ErrorCodes.cs`）：`ShellLoginPathUnavailable = new(500_0035, "The login shell search path could not be read.", HttpStatusCode.InternalServerError)`。各失败情况的消息文本与设计 1.1 的表格逐字相同。
- 预期失败抛出 `AgwException`；调用方取消抛出 `OperationCanceledException`。不写空的 `catch`。
- 项目引用不变：`Agw.Tools` 不引用 Pi SDK。
- 测试使用本机的 `/bin/zsh` 和 `LocalShellExecutor`，不使用 mock。需要 zsh 的用例通过 `SkipType = typeof(ZshTestEnvironment)` 共用 `HasZsh` 和 `RequiresZsh`。
- 测试创建的文件放在 `AppContext.BaseDirectory/.scratch/` 下，用例结束时删除。
- 注释用中英文书写；修改过的 `.cs` 文件用 `dotnet csharpier format <文件>` 处理。
- `docs/designs/` 下的文件提交后不可修改：设计文档和本清单在任务 4 的最后一步一起提交，之前的提交不包含这两个文件。
- 提交信息使用 Conventional Commits。

## 复核重点

1. 启动配置在后台启动进程并持有标准输出管道：查询在读取到完整的一帧后返回，不等到超时。测试 `ReadAsync_BackgroundProcessHoldsStdout_ReturnsPath`（任务 1）。
2. 启动配置把 `PATH` 设为空：以 `Login shell returned an empty search path.` 结束，shell 不以空的 `PATH` 启动。测试 `ReadAsync_EmptyPath_ThrowsShellLoginPathUnavailable`（任务 1）。
3. `PATH` 超过 64 KiB：以 `Login shell search path exceeds 64 KiB.` 结束，读取不无限占用内存。测试 `ReadAsync_PathExceedsLimit_ThrowsShellLoginPathUnavailable`（任务 1）。
4. 工作目录被移走后执行器重新创建：新执行器仍然使用 login shell 的 `PATH`，并且 login shell 只启动一次。测试 `RunShell_DirectoriesMovedAfterLoginShellQuery_KeepsLoginShellPath`（任务 2）。
5. 开发机的 shell 启动配置影响现有测试：创建本机 `run_shell` 的现有用例传入 `PATH`；任务 2 步骤 6 的探测运行找出通过完整执行流程创建 `run_shell` 的用例。

## 文件结构

| 文件 | 职责 |
| --- | --- |
| 【新增】`src/server/Agw.Tools/Impl/ContextualTools/Shell/LoginShellPathReader.cs` | 判断查询环境有没有受支持的 login shell；启动它并返回转换后的 `PATH` |
| 【修改】`src/server/Agw.Tools/Impl/ContextualTools/Shell/ShellContextualTool.cs` | 生成本机执行器的环境；把它传给 `LocalShellExecutor` |
| 【修改】`src/server/Agw.Shared/Exceptions/ErrorCodes.cs` | 新增 `ShellLoginPathUnavailable` |
| 【新增】`tests/Agw.Tools.Tests/LoginShellPathReaderTests.cs` | `LoginShellPathReader` 的测试 |
| 【新增】`tests/Agw.Tools.Tests/ZshTestEnvironment.cs` | 共用的 `/bin/zsh` 可用性检查和跳过原因 |
| 【修改】`tests/Agw.Tools.Tests/ShellContextualToolTests.cs` | login shell 相关用例；辅助方法接收执行环境变量 |
| 【修改】`tests/Agw.Tools.Tests/ToolRegistryServiceTests.cs`、`tests/Agw.Agents.Tests/ExecutionWorkspaceAuthorizationTests.cs` | 创建本机 `run_shell` 的用例传入 `PATH` |
| 【修改】`site/content/docs/operations/configuration.zh.md`、`configuration.en.md` | 说明本机 `run_shell` 的 `PATH` 来源 |

---

### 任务 1：错误码与 LoginShellPathReader

**文件：**
- 创建：`src/server/Agw.Tools/Impl/ContextualTools/Shell/LoginShellPathReader.cs`
- 修改：`src/server/Agw.Shared/Exceptions/ErrorCodes.cs`（`AcpInstallFailed`，`500_0034`，之后）
- 测试：`tests/Agw.Tools.Tests/LoginShellPathReaderTests.cs`

**接口：**
- 产出：
  - `ErrorCodes.ShellLoginPathUnavailable`（定义见全局约束）
  - `internal static class LoginShellPathReader`，命名空间 `Agw.Tools.Impl.ContextualTools.Shell`
  - `internal static readonly TimeSpan LoginShellPathReader.DefaultTimeout`，值为 3 秒
  - `public static bool HasLoginShell(IReadOnlyDictionary<string, string> environment)`
  - `public static Task<string> ReadAsync(IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancellationToken)`

- [x] **步骤 1：编写失败的测试**

`LoginShellPathReaderTests`（`IDisposable`）：构造时创建 `_directory = AppContext.BaseDirectory/.scratch/login-shell-path/{Guid:N}`，`Dispose` 时递归删除。辅助方法 `ShellEnvironment(string shell = "/bin/zsh")` 返回 `PATH=/usr/bin:/bin`、`HOME=_directory`、`USER=Environment.UserName`、`SHELL=shell`、`ZDOTDIR=_directory`；`WriteZshrcAsync(string content)` 写入 `_directory/.zshrc`。

不需要 zsh 的用例：
- `HasLoginShell_ShellMissing_ReturnsFalse`：环境中没有 `SHELL`；以及 `SHELL` 为 `"  "`。两者都返回 `false`。
- `HasLoginShell_UnsupportedShell_ReturnsFalse`：`SHELL=/usr/local/bin/fish`。
- `HasLoginShell_SupportedShell_ReturnsTrue`：`[Theory]`，`/bin/sh`、`/bin/bash`、`/bin/zsh`、`/bin/dash`、`/bin/ksh`。
- `ReadAsync_RelativeShellPath_ThrowsShellLoginPathUnavailable`：`SHELL=zsh`，消息为 `Login shell path is not absolute: zsh`。
- `ReadAsync_ShellExecutableMissing_ThrowsShellLoginPathUnavailable`：`SHELL` 为 `_directory/missing/zsh`，消息以 `Cannot start login shell:` 开头。

需要 zsh 的用例（每个用例先写入 `.zshrc`，`timeout` 未说明时为 `DefaultTimeout`）：
- `ReadAsync_StartupConfigurationSetsPath_ReturnsNormalizedEntriesInOrder`：
  - `.zshrc` 为 `printf 'shell startup\n'`、`cd "$ZDOTDIR"`、`export HOME="$ZDOTDIR/other-home"`、`export PATH="cli tools::./node tools:~/.dotnet/tools:~:~someone/tools:.:/opt/tool/link/../bin:/usr/bin"`。
  - 断言返回值等于 `cli tools:./node tools:{_directory}/.dotnet/tools:{_directory}:~someone/tools:.:/opt/tool/link/../bin:/usr/bin`。
- `ReadAsync_BackgroundProcessHoldsStdout_ReturnsPath`：`.zshrc` 为 `export PATH=/usr/bin` 和 `/bin/sleep 30 &`；断言返回 `/usr/bin`。
- `ReadAsync_EmptyPath_ThrowsShellLoginPathUnavailable`：`.zshrc` 为 `export PATH=`；消息为 `Login shell returned an empty search path.`。
- `ReadAsync_PathExceedsLimit_ThrowsShellLoginPathUnavailable`：`.zshrc` 为 `export PATH="${(l:70000::a:):-}"`；消息为 `Login shell search path exceeds 64 KiB.`。
- `ReadAsync_StartupConfigurationTimesOut_ThrowsShellLoginPathUnavailable`：`.zshrc` 为 `/bin/sleep 30`，`timeout` 为 200 毫秒；消息为 `Login shell lookup timed out.`。
- `ReadAsync_ShellExitsWithoutFrame_ThrowsShellLoginPathUnavailable`：`.zshrc` 为 `exit 7`；消息为 `Login shell exited with code 7 without returning a valid search path.`。
- `ReadAsync_ShellExitsWhileBackgroundProcessHoldsStdout_ThrowsShellLoginPathUnavailable`：`.zshrc` 为 `/bin/sleep 30 &` 和 `exit 7`，消息包含 `code 7`。
- `ReadAsync_CallerCancels_ThrowsOperationCanceledException`：`.zshrc` 为 `/bin/sleep 30`，100 毫秒后取消；`Assert.ThrowsAnyAsync<OperationCanceledException>`。

所有 `AgwException` 断言同时检查 `Code == ErrorCodes.ShellLoginPathUnavailable.Code`。

- [x] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Tools.Tests --filter-class "*LoginShellPathReaderTests"`
预期：编译失败，`LoginShellPathReader` 和 `ErrorCodes.ShellLoginPathUnavailable` 不存在。

- [x] **步骤 3：实现**

- `ErrorCodes.cs`：在 `AcpInstallFailed` 之后加入 `ShellLoginPathUnavailable`。
- `HasLoginShell`：`SHELL` 存在、非空白，并且 `Path.GetFileName` 的结果在受支持的文件名集合中。
- 按设计 1.1 的五个步骤实现 `ReadAsync`。
  - `environment` 是完整的查询环境，`SHELL` 从中读取，并要求 `Path.IsPathFullyQualified`。
  - 依次传入 `-il`、`+m`、`-c` 和固定脚本。
  - 每种失败情况抛出 `AgwException(ErrorCodes.ShellLoginPathUnavailable, message)`，`message` 与设计 1.1 的表格逐字相同。
  - 同时等待读取数据帧和进程退出。进程先退出时，在查询时限内最多继续等待读取 1 秒。没有收到完整的数据帧时，错误消息包含进程退出码。
  - 成功时按 `Path.PathSeparator` 分开 `PATH`，去掉空项，按全局约束展开当前用户的主目录，保持其余项的原值和顺序，再连接各项。结果为空字符串时抛出错误。
  - 清理时用 `process.Kill(entireProcessTree: true)` 终止仍在运行的进程，取消读取，再用 `WaitAsync(CleanupTimeout)` 限制等待任务完成的时间。
- 方法和关键步骤的注释用中英文书写。

- [x] **步骤 4：运行测试，确认通过**

运行：
- `dotnet test --project tests/Agw.Tools.Tests --filter-class "*LoginShellPathReaderTests"`
- `dotnet test --project tests/Agw.Shared.Tests`

预期：全部通过；本机有 `/bin/zsh`，没有被跳过的用例。

- [x] **步骤 5：格式化**

```bash
dotnet csharpier format src/server/Agw.Tools/Impl/ContextualTools/Shell/LoginShellPathReader.cs tests/Agw.Tools.Tests/LoginShellPathReaderTests.cs
```

---

### 任务 2：ShellContextualTool 使用 login shell 的 PATH

**文件：**
- 修改：`src/server/Agw.Tools/Impl/ContextualTools/Shell/ShellContextualTool.cs`（`MaterializeAsync`，第 38 行；`CreateLocalExecutor`，第 97 行）
- 测试：
  - `tests/Agw.Tools.Tests/ShellContextualToolTests.cs`
  - `tests/Agw.Tools.Tests/ToolRegistryServiceTests.cs`（`CreateMaterializationContext`，第 415 行；`MaterializeAsync_ContextualExecuteTool_RequiresApprovalAtRuntime`，第 446 行）
  - `tests/Agw.Agents.Tests/ExecutionWorkspaceAuthorizationTests.cs`（`CreateShellContext`，第 393 行）

**接口：**
- 使用：任务 1 的 `LoginShellPathReader.HasLoginShell`、`ReadAsync`、`DefaultTimeout`。
- 产出：
  - `internal static Task<Dictionary<string, string?>> ShellContextualTool.CreateLocalEnvironmentAsync(ToolMaterializationContext context, CancellationToken cancellationToken)`
  - `private static LocalShellExecutor CreateLocalExecutor(string workspace, IReadOnlyDictionary<string, string?> environment)`

- [x] **步骤 1：编写失败的测试**

`ShellContextualToolTests` 的辅助方法：
- `MaterializeAsync(string workspace, IReadOnlyList<ProjectWorkspaceDirectory> additionalDirectories, string backend, CancellationToken ct, IReadOnlyDictionary<string, string>? environmentVariables = null)`：把 `environmentVariables ?? HostPath()` 写入 `ToolMaterializationContext.EnvironmentVariables`。
- `HostPath()`：返回 `{ ["PATH"] = Environment.GetEnvironmentVariable("PATH")! }`。
- `CreateLoginShellDirectory(string root, string zshrc)`：创建 `{root}/zdotdir`，写入 `.zshrc`，创建 `tools/agw-shell-probe`（内容为 `#!/bin/sh` 和 `printf 'agw-shell-probe-ok'`，权限 `UserRead | UserWrite | UserExecute`），返回该目录。

新增用例（前四个需要 zsh，`{dir}` 为 `CreateLoginShellDirectory` 的返回值）：
- `RunShell_LoginShellAddsDirectory_RunsCommandFromThatDirectory`：
  - 使用 `[Theory]` 验证 `.zshrc` 分别把 `$ZDOTDIR/tools`、`~/tools` 或 `../zdotdir/tools` 加入 `PATH` 的情况。
  - 执行环境变量为 `SHELL=/bin/zsh`、`HOME={dir}` 和 `ZDOTDIR={dir}`。
  - 直接运行 `agw-shell-probe` 和通过 `/usr/bin/env agw-shell-probe` 运行的结果均包含 `agw-shell-probe-ok`。传入的字典中没有 `PATH`。
- `RunShell_DirectoriesMovedAfterLoginShellQuery_KeepsLoginShellPath`：
  - `.zshrc`：`echo q >> "$ZDOTDIR/queries"`、`export PATH="$ZDOTDIR/tools:$PATH"`。
  - 运行 `agw-shell-probe`，`Directory.Move` 移走工作目录，再运行一次。
  - 断言：两次结果都包含 `agw-shell-probe-ok`；`{dir}/queries` 只有一行。
- `RunShell_EnvironmentVariablesContainPath_UsesThatPath`：
  - `.zshrc`：`exit 7`。
  - 执行环境变量：`PATH={dir}/tools:/usr/bin:/bin`、`SHELL=/bin/zsh`、`ZDOTDIR={dir}`。
  - 断言：运行 `agw-shell-probe` 的结果包含 `agw-shell-probe-ok`。
- `Materialize_LoginShellQueryFails_ThrowsShellLoginPathUnavailable`：
  - `.zshrc`：`exit 7`；执行环境变量：`SHELL=/bin/zsh`、`ZDOTDIR={dir}`。
  - 断言：`MaterializeAsync` 抛出 `AgwException`，`Code == ErrorCodes.ShellLoginPathUnavailable.Code`，消息包含 `code 7`。
- `CreateLocalEnvironmentAsync_UnsupportedLoginShell_LeavesPathUnset`（不需要 zsh）：
  - `ToolMaterializationContext.EnvironmentVariables` 为 `SHELL=/usr/local/bin/fish`。
  - 断言：返回的字典没有 `PATH`，`SHELL` 为 `/usr/local/bin/fish`。

现有用例：
- `ToolRegistryServiceTests.CreateMaterializationContext` 增加参数 `IReadOnlyDictionary<string, string>? environmentVariables = null`，非空时写入 `EnvironmentVariables`；`MaterializeAsync_ContextualExecuteTool_RequiresApprovalAtRuntime` 传入测试进程的 `PATH`。
- `ExecutionWorkspaceAuthorizationTests.CreateShellContext` 的 `EnvironmentVariables` 设为测试进程的 `PATH`。

- [x] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Tools.Tests --filter-class "*ShellContextualToolTests"`
预期：编译失败，`ShellContextualTool.CreateLocalEnvironmentAsync` 不存在。

- [x] **步骤 3：实现**

- `CreateLocalEnvironmentAsync` 按设计 1.2 的五个步骤编写。Server 进程的四个变量用 `Environment.GetEnvironmentVariable` 读取，值为 `null` 的项不放进查询环境。
- `MaterializeAsync`：目录校验（`_workspaceAccess.ValidateAsync`）和 backend 读取之后，使用 switch 表达式选择执行器工厂。`local` 分支等待 `CreateLocalExecutorFactoryAsync`，由该方法调用一次 `CreateLocalEnvironmentAsync`，并返回委托 `() => CreateLocalExecutor(workspace, environment)`。backend 的值无效时抛出 `ErrorCodes.InvalidParam`。
- `CreateLocalExecutor`：`Environment = new Dictionary<string, string?>(environment, StringComparer.Ordinal)`，其余选项不变。

- [x] **步骤 4：运行测试，确认通过**

运行：
- `dotnet test --project tests/Agw.Tools.Tests`
- `dotnet test --project tests/Agw.Agents.Tests --filter-class "*ExecutionWorkspaceAuthorizationTests"`

预期：全部通过。

- [x] **步骤 5：格式化**

```bash
dotnet csharpier format src/server/Agw.Tools/Impl/ContextualTools/Shell/ShellContextualTool.cs tests/Agw.Tools.Tests/ShellContextualToolTests.cs tests/Agw.Tools.Tests/ToolRegistryServiceTests.cs tests/Agw.Agents.Tests/ExecutionWorkspaceAuthorizationTests.cs
```

- [x] **步骤 6：找出启动开发机 login shell 的现有用例**

这次运行回答的问题：除了步骤 1 列出的位置，还有哪些用例在没有 `PATH` 的执行环境变量下创建本机 `run_shell`。`SHELL=/nonexistent/zsh` 让这类用例以 `Cannot start login shell:` 结束。

对 `Agw.Tools.Tests`、`Agw.Agents.Tests`、`Agw.Files.Tests`、`Agw.Host.Tests`、`Agw.Extensions.Feishu.Tests`、`Agw.Extensions.TaskPilot.Tests` 分别运行：

```bash
SHELL=/nonexistent/zsh dotnet test --project tests/<项目名>
```

预期：没有用例以错误码 `5000035` 结束。出现这类用例时，在它的 Project 或 Agent 环境变量（直接创建 `ToolMaterializationContext` 的用例则是 `EnvironmentVariables`）中加入测试进程的 `PATH`，格式化修改过的文件，再运行该项目直到通过。

---

### 任务 3：站点文档

**文件：**
- 修改：`site/content/docs/operations/configuration.zh.md`（“Shell 工具与 Project 工作目录”，`Agents:Shell:Backend` 说明之后）
- 修改：`site/content/docs/operations/configuration.en.md`（“Shell tool and Project workspaces”，对应位置）

- [x] **步骤 1：加入中文说明**

在以“`Agents:Shell:Backend` 只选择 AGW Shell 工具的执行后端”开头的那一行之后，加入设计第 4 节引用块中的文字，作为单独的一行。

- [x] **步骤 2：加入英文说明**

在以“`Agents:Shell:Backend` selects only the AGW Shell tool backend.”开头的那一行之后加入：

```markdown
On macOS and Linux, the `local` backend uses the program search path (`PATH`) of the login shell of the account that runs the Server. The Server reads the startup configuration of the `sh`, `bash`, `zsh`, `dash`, or `ksh` named by the `SHELL` environment variable. When `PATH` is set in the environment variables of the Agent, the Project, or the execution request, that value is used. When `SHELL` is unset or names another shell, the Server process `PATH` is used. If reading the login shell `PATH` fails, the execution ends with error `5000035`.
```

- [x] **步骤 3：核对站点文档**

运行：`git diff --stat -- site/content/docs/operations`
预期：两个文件各增加一行文字和一个空行。

---

### 任务 4：回归与验收

**文件：**
- 创建（均被 `.gitignore` 忽略）：
  - `.scratch/run-shell-path/acceptance/com.agw.server.runshell.plist`
  - `src/clients/packages/chat-runtime/.tmp/run-shell-path-acceptance.ts`
- 提交：`docs/designs/2026-11-26-run_shell启动环境.md`、`docs/designs/2026-11-26-run_shell启动环境_todo_list.md`

- [x] **步骤 1：回归测试**

运行：
- `dotnet build Agw.slnx`
- `dotnet test --project tests/Agw.Tools.Tests`
- `dotnet test --project tests/Agw.Agents.Tests`
- `dotnet test --project tests/Agw.Shared.Tests`
- `dotnet test --project tests/Agw.Architecture.Tests`
- `dotnet csharpier check .`

构建应无错误，已运行的测试和格式检查应通过。

在伪终端中运行 `LoginShellPathReaderTests` 中的启动配置成功和超时用例。核对每个用例运行前后，终端的前台进程组都是调用方的进程组。查询结束后按 Ctrl+C，确认调用方收到 SIGINT。

- [x] **步骤 2：发布并用 LaunchAgent 启动 Server**

- 发布：`dotnet publish src/server/Agw.Standalone.Host -c Release -r osx-arm64 --self-contained true -o .scratch/run-shell-path/acceptance/server`。
- plist 的结构与 `buildLaunchAgentPlist`（`src/clients/desktop/src/main/daemon/registration.ts:10`）相同，没有 `EnvironmentVariables`：
  - `Label`：`com.agw.server.runshell`。
  - `ProgramArguments`：`<acceptance>/server/agw-server`、`serve`、`--urls=http://127.0.0.1:30961`、`--AgwDataDir=<acceptance>/data`、`--AgwLogDir=<acceptance>/logs`、`--Setup:AdminPassword=<验收密码>`。
  - `WorkingDirectory`：`<acceptance>/server`；`RunAtLoad`、`KeepAlive` 为 `true`。
- 启动：`launchctl bootstrap gui/$(id -u) .scratch/run-shell-path/acceptance/com.agw.server.runshell.plist`。
- 确认：`launchctl print gui/$(id -u)/com.agw.server.runshell` 显示 `state = running`。

- [x] **步骤 3：编写并运行验收脚本**

模型使用 Podman 容器 `agw-ollama-acceptance` 中的 `qwen3:1.7b`（`127.0.0.1:11435`）。确认 Podman VM 已运行，再执行 `podman start agw-ollama-acceptance`。

`run-shell-path-acceptance.ts` 的登录、Provider 创建、Agent 创建、Turn 运行和审批处理参照同目录的 `ollama-acceptance.ts`（`tool` 场景）。脚本内容：
- 创建带 `tools: [{ kind: "tool", definition: { name: "run_shell", options: {} } }]` 的 System Agent，指令要求按原样运行用户给出的一条命令。
- 以 `permissionMode: "alwaysAsk"` 运行，只批准与预期完全相同的命令。
- 场景 A（Agent 没有环境变量）：两个 Turn 分别运行 `which node` 和 `node --version`，输出每次工具调用的退出码和标准输出。
- 场景 B（Agent 环境变量 `PATH=/usr/bin:/bin:/usr/sbin:/sbin`）：一个 Turn 运行 `which node`。

运行：`pnpm --filter @agw/chat-runtime exec tsx .tmp/run-shell-path-acceptance.ts http://127.0.0.1:30961 <验收密码> http://127.0.0.1:11435 qwen3:1.7b`（在 `src` 目录）。

验收条件：
- 场景 A 的两条命令退出码均为 `0`，`which node` 的输出与终端中 `/bin/zsh -il +m -c 'which node'` 的输出相同。
- 场景 B：`which node` 的退出码为 `1`。

本次验收中，场景 A 的 `which node` 和 `node --version` 分别输出 `/opt/homebrew/bin/node` 和 `v26.13.1`，退出码均为 `0`。场景 B 的 `which node` 退出码为 `1`。

- [x] **步骤 4：恢复环境**

```bash
launchctl bootout gui/$(id -u)/com.agw.server.runshell
podman stop agw-ollama-acceptance
```

如果本次验收启动了 Podman VM，执行 `podman machine stop`。

- [x] **步骤 5：提交 run_shell 功能**

将代码、测试、站点文档、设计文档和本清单一起提交。
