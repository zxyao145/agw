# Diff 模式下查看文件：实施计划

> **给执行计划的 agent：** 必须使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 按任务逐个实施。步骤使用 checkbox（`- [ ]`）记录进度。

**目标：** Diff 模式下文件标题行最右侧增加查看切换按钮，在 diff 与文件查看（extension 查看器与 Source）之间切换。

**架构：** `ChatWorkspaceProvider` 保存当前目录的文件选择和 `diffFileView` 状态。选择或 `showDiff` 变化时，由一个 effect 读取内容。工作区和 `FileContent` 共用 `shouldShowFileDiff`。`FileContent` 用 `showViewToggle` 决定是否显示查看切换按钮。Desktop 继续使用 `SourceDocument` 与现有查看器。

**技术栈：** React 19、TypeScript、lucide-react、Monaco（`@agw/code-view`）、Node test runner（`tsx --test`，jsdom）、Playwright（`@agw/test-harness/browser`、Web e2e）、Electron 集成测试驱动。

**设计文档：** `docs/designs/2026-11-15-Diff模式下查看文件.md`。执行每个任务之前阅读设计文档的对应章节。

## 全局约束

- 在从 `main` 新建的 `feature/diff-file-view` worktree 中实施。所有路径都相对这个 worktree。
- 只修改 `@agw/projects`、`@agw/chat` 与测试（Web e2e、Desktop 集成测试）。Server、Desktop renderer 的产品代码、extension、Mobile 都不变，不执行 `pnpm gen:api`。
- 名称：工作区状态 `diffFileView`、`setDiffFileView`；`FileContent` props `diffFileView`、`onDiffFileViewChange`；局部变量 `showDiff`；`loadFileContent` 的第三个参数 `readDiff`。
- 文案：diff 显示时 "View file"，有未保存修改时 "View file, unsaved changes"，文件查看时 "View changes"。`aria-label` 与 `title` 使用同一段文字。
- 按钮：`Button`（`@agw/components`）`type="button"`、`variant="ghost"`、`size="icon"`、`className="relative size-6"`；图标 lucide-react 的 `Eye`（diff 显示时）与 `FileDiff`（文件查看时），`className="size-3.5"`；未保存标记为 `<span aria-hidden="true" className="absolute right-0.5 top-0.5 size-1.5 rounded-full bg-foreground" />`。
- 测试：
  - 新增用例命名为 `Method_Condition_ExpectedResult`，Diff 模式统一使用术语 `Diff mode`。
  - 组件用例使用现有组件与 Monaco。新增 Web e2e 使用 Standalone Host 和 Git 工作区，测试文件放在包内 `.scratch/` 下。
- 注释使用中文和英文，数量保持克制。
- 格式化只处理修改过的文件：`pnpm --filter <包名> exec oxfmt -c ../../.oxfmtrc.json <文件>`（`@agw/web`、`@agw/desktop` 使用 `-c ../.oxfmtrc.json`）。
- 本机 `core.autocrlf=true`，新 worktree 检出的文本文件为 CRLF。Markdown extension 的 `test-inputs` 因此在 `pnpm test` 中有 6 个 `MarkdownSourceMapError` 失败，与本改动无关；遇到其他只在本机出现、逐行核对文字的测试失败时，先用 `git ls-files --eol <文件>` 检查换行。
- worktree 中运行 `pnpm install` 安装自己的 `node_modules`，不用指向主目录的符号链接。
- 每个任务最后的提交步骤只在用户要求提交时执行，提交信息使用 Conventional Commits。不使用 Git 回退或丢弃任何修改。
- Desktop 集成测试每次运行之前都向用户确认，并写明这次运行要回答的问题。
- 创建子代理时，提示词要求它使用 Rider MCP 工具查找和阅读代码。

## 复核重点

1. 文件选择发起一次读取，迟到的文件读取响应不改变当前 diff。测试在任务 3。
2. 工作区中已经没有这个文件时，Web 显示 `FileError`，标题行保留 "More" 和 "View changes"。测试在任务 1 和任务 3。
3. 文件查看中再次点击文件树中同一个条目，保持文件查看和当前内容。测试在任务 3。
4. 通过 `openFile` 打开其他目录的文件时，目录切换后选中待打开的文件。Chat 的现有 hydration 用例覆盖确认与目录切换。任务 3 覆盖切换目录后的请求路径。
5. Source 有未保存修改时，diff 标题行上的 "View file" 按钮带标记。测试在任务 1 和任务 4。

## 文件结构

- `src/clients/packages/projects/src/ui-web/components/file-explorer/file-content.tsx`：`shouldShowFileDiff`、查看切换按钮与未保存标记。`index.tsx` 导出 `shouldShowFileDiff`。
- `src/clients/packages/projects/src/ui-web/components/file-explorer/file-content.test.ts`：jsdom 用例。
- `src/clients/packages/projects/browser-tests/fixture/main.tsx`、`src/clients/packages/projects/browser-tests/file-content.test.ts`：Chromium 用例。
- `src/clients/packages/projects/src/ui-web/components/file-explorer/README.md`：`FileContent` 的说明。
- `src/clients/packages/chat/src/ui-web/pages/chat/chat-workspace.tsx`：预览选择与 `diffFileView`、读取 effect、`ChatFilePane` 的传递。
- `src/clients/web/e2e/file-code-view.spec.ts`：Web e2e 用例。`src/clients/web/e2e/standalone-file-project.ts` 提供测试 Project、Standalone Host 与请求转发。
- `AGENTS.md` 与 `docs/1.Development.md` 说明本地 Web e2e 的 Standalone Host 构建命令。
- `src/clients/desktop/src/main/extensions.integration.ts`：`diffModeFileView` 与 `diffModeSourceEdits` 步骤。

## 准备

- [x] 从 `main` 创建 `feature/diff-file-view` worktree，本机路径为 `/Users/ben/.codex/worktrees/diff-file-view/agw`。
- [x] 把两份设计文档移入 worktree 的 `docs/designs/`。
- [x] 在 worktree 的 `src/clients` 中运行 `pnpm install --frozen-lockfile`。
- [x] 在 worktree 的 `src/clients` 中运行 `pnpm --filter @agw/projects test`、`pnpm --filter @agw/chat test`，记录基线。

---

### 任务 1：`FileContent` 的查看切换按钮

**文件：**

- 修改：`src/clients/packages/projects/src/ui-web/components/file-explorer/file-content.tsx`
- 修改：`src/clients/packages/projects/src/ui-web/components/file-explorer/README.md`
- 修改：`src/clients/packages/projects/browser-tests/fixture/main.tsx`
- 测试：`src/clients/packages/projects/src/ui-web/components/file-explorer/file-content.test.ts`、`src/clients/packages/projects/browser-tests/file-content.test.ts`

**接口：**

- 产出（任务 2 使用）：`FileContentProps` 增加

  ```ts
  diffFileView?: boolean; // 缺省为 false
  onDiffFileViewChange?: (diffFileView: boolean) => void;
  ```

  按钮在 diff 显示时调用 `onDiffFileViewChange(true)`，在文件查看时调用 `onDiffFileViewChange(false)`。

- [x] **步骤 1：编写 jsdom 用例**

  在 `file-content.test.ts` 中通过 `element(props)` 构造 `FileContent`。按用例传入所需 props：

  ```ts
  const BINARY_DIFF = {
    diff: "Binary files a/logo.png and b/logo.png differ\n",
    unchanged: false,
    isBinary: true,
    originalContent: null,
    modifiedContent: null,
  };

  /** 标题行中的按钮名称。The names of the buttons in the title row. */
  const buttonNames = () => screen.getAllByRole("button").map((button) => button.getAttribute("aria-label"));
  ```

  - `FileContent_FileViewInDiffMode_ShowsViewerTabsAndActions`：`onlyDiff` 和 `diffFileView` 为 true。检查查看器标签和默认查看器、"More" 菜单、`["More", "View changes"]` 按钮顺序及切换回调 `[false]`。
  - `FileContent_DiffModeOff_HasNoViewToggle`：提供 `onDiffFileViewChange` 并关闭 Diff 开关，检查查看切换按钮不显示。
  - `FileContent_DiffModeWithUnsavedViewer_MarksViewFile`：使用 `BINARY_DIFF` 与有未保存修改的 `VIEWERS`。检查二进制 diff、按钮 `aria-label` 与 `title`，以及切换回调 `[true]`。
  - `FileContent_FileViewReadErrorInDiffMode_KeepsViewChangesToggle`：`diffFileView: true`、`fileViewers: []`、`contentError: "File not found"`。检查错误文字和 `["More", "View changes"]`。

- [x] **步骤 2：运行 jsdom 用例，确认失败**

  运行：`pnpm --filter @agw/projects exec tsx --test src/ui-web/components/file-explorer/file-content.test.ts`
  实现前，查看器标签与切换按钮的用例应失败。Diff 开关关闭的用例及原有用例应通过。

- [x] **步骤 3：编写 Chromium 用例**

  `fixture/main.tsx`：`Scenario` 增加 `diffFileView?: boolean`；`FileContent` 增加 `diffFileView={scenario.diffFileView}` 与 `onDiffFileViewChange={(value) => record("onDiffFileViewChange", value)}`（所有场景都提供，`onlyDiff` 为 false 时不显示按钮）。

  `browser-tests/file-content.test.ts` 增加三个用例，diff 使用 `{ diff: "@@ -1 +1 @@\n-a\n+b\n", unchanged: false, isBinary: false, originalContent: "a\n", modifiedContent: "b\n" }`：

  - `FileContent_DiffMode_ShowsOnlyViewFileInHeader`：`onlyDiff: true`、`diffScope: "unstaged"`。等 `.line-insert` 出现后检查标题行右侧只有 "View file"，点击后 `calls(page)` 为 `[["onDiffFileViewChange", true]]`。
  - `FileContent_DiffModeWithUnsavedSource_MarksViewFile`：`onlyDiff: true`、`editable: { ...CLEAN, status: "dirty", saveStatus: "unsaved", unsaved: true }`。检查 "View file, unsaved changes" 与圆点，`saveStatusText(page)` 为 null，`calls(page)` 中没有 `show`。
  - `FileContent_FileViewInDiffMode_EnablesEditableSource`：`onlyDiff` 与 `diffFileView` 为 true。检查 Source 状态、"More" 菜单与标题行按钮顺序。选择 "Save" 和 "View changes" 后检查回调顺序。

  现有回归用例 `FileContent_DiffMode_DisplaysReadOnlyChanges` 继续检查 diff 的只读行为。

- [x] **步骤 4：运行 Chromium 用例，确认失败**

  运行：`pnpm --filter @agw/projects test:browser`
  预期：三个新用例失败（找不到按钮；第三个用例没有状态栏），原有用例通过。

- [x] **步骤 5：在 `file-content.tsx` 中实现**

  - props 接口加入两个 prop，注释使用设计文档第 2 节的中英文；解构时 `diffFileView = false`。
  - 导出 `shouldShowFileDiff(onlyDiff, diffFileView)` 供工作区使用。`FileContent` 在计算 `viewers` 前调用它得到 `showDiff`，用于 `viewers`、`sourceEditing`、`sourceEntries`、`reload`、`ErrorBoundary` 的 `resetKeys` 与 `GitDiffView` 的显示条件。
  - 未保存标记读取 props：`(editableSource?.document?.unsaved ?? false) || fileViewers.some((viewer) => viewer.unsaved)`，只在 `showDiff` 为 true 时显示。
  - `showViewToggle` 检查 `onlyDiff && onDiffFileViewChange !== undefined`。按钮放在 `actions` 最后，点击调用 `onDiffFileViewChange(showDiff)`。
  - `hasHeaderActions` 根据标签、"More" 菜单与 `showViewToggle` 决定是否显示标题行操作。

- [x] **步骤 6：运行全部用例，确认通过**

  运行：`pnpm --filter @agw/projects test`、`pnpm --filter @agw/projects test:browser`、`pnpm --filter @agw/projects typecheck`、`pnpm --filter @agw/projects lint`
  预期：全部通过（基线中记录的失败除外）。

- [x] **步骤 7：更新 README**

  `README.md` 的 `FileContent` 一节说明 Diff 模式、标题行切换按钮和 `diffFileView` 的行为。

- [x] **步骤 8：格式化修改过的文件**

  运行：`pnpm --filter @agw/projects exec oxfmt -c ../../.oxfmtrc.json src/ui-web/components/file-explorer/file-content.tsx src/ui-web/components/file-explorer/file-content.test.ts browser-tests/fixture/main.tsx browser-tests/file-content.test.ts src/ui-web/components/file-explorer/README.md`

- [ ] **步骤 9：提交（用户要求时）**

  ```bash
  git add src/clients/packages/projects
  git commit -m "feat: add file view toggle to the diff header"
  ```

---

### 任务 2：工作区的 `diffFileView`

**文件：**

- 修改：`src/clients/packages/chat/src/ui-web/pages/chat/chat-workspace.tsx`
- 回归：运行已有的 `src/clients/packages/chat/src/ui-web/pages/chat/chat-workspace-hydration.test.ts`。新增的文件读取用例见任务 3。

**接口：**

- 使用（任务 1）：`FileContent` 的 `diffFileView`、`onDiffFileViewChange`。
- 产出：`ChatWorkspaceContextValue` 增加 `diffFileView: boolean` 与 `setDiffFileView: (diffFileView: boolean) => void`（注释使用设计文档第 1 节的中英文）。

- [x] **步骤 1：在 `chat-workspace.tsx` 中实现**

  - `fileSelection` 记录路径、scope、Project、目录和工作区路径。`fileInDirectory` 用同一规则匹配 `currentFileSelection` 与待打开文件的 Project 和目录。
  - `handleOnFileSelected`、同目录的 `openFile` 和目录切换后待打开的文件都通过 `selectFileForPreview` 更新选择。这个函数开始加载状态，并把 `diffFileView` 置为 false。
  - 读取 effect 依赖 `[currentFileSelection, loadFileContent, showDiff]`。它调用 `loadFileContent(filePath, diffScope, readDiff)`，传入 `showDiff` 作为 `readDiff`。
  - `loadFileContent` 更新文件内容、diff 内容、错误与加载状态。`fileLoadGenerationRef` 确保只有当前请求更新预览。
  - `handleOnFileReseted` 使用当前 `showDiff` 重新读取。Desktop 的 `handleFileWritten` 刷新文件树，在文件查看中清空当前文件的工作区预览内容与读取错误。Desktop 通过 `SourceDocument` 读取 Source 内容。
  - `setOnlyDiff` 和 `clearFilePreview` 把 `diffFileView` 置为 false。`handleOnFileGitScopeChanged` 更新选中文件的 scope。
  - `ChatFilePane` 传入 `diffFileView={workspace.diffFileView}` 与 `onDiffFileViewChange={workspace.setDiffFileView}`。

- [x] **步骤 2：运行用例，确认通过**

  运行 `pnpm --filter @agw/chat test`、`pnpm --filter @agw/chat typecheck`、`pnpm --filter @agw/chat lint`、`pnpm --filter @agw/desktop typecheck` 与 `pnpm exec tsc -p web/tsconfig.json`。

- [x] **步骤 3：格式化修改过的文件**

  运行 `pnpm --filter @agw/chat exec oxfmt -c ../../.oxfmtrc.json src/ui-web/pages/chat/chat-workspace.tsx`。

- [ ] **步骤 4：提交（用户要求时）**

  ```bash
  git add src/clients/packages/chat
  git commit -m "feat: switch between the diff and the file view in Diff mode"
  ```

---

### 任务 3：Web e2e

**文件：**

- 测试：`src/clients/web/e2e/file-code-view.spec.ts`
- 测试准备：`src/clients/web/e2e/standalone-file-project.ts`

**接口：**

- 使用（任务 1、2）："View file"、"View changes"、Diff 开关与目录选择。测试使用 Standalone Host 的文件接口、SQLite 和隔离的 Git 工作区。

- [x] **步骤 1：编写用例**

  - `ChatFilePane_DiffMode_ShowsWorkingFileAndReadsEachScopeOnce` 检查 `calc.ts` 的两个 scope、重复选择同一条目和 stage 后的显示。在 Source 中输入文字，检查工作区内容保持原样。记录 `/api/files/read` 与 `/api/files/diff`，检查请求的次数和 scope。
  - `ChatFilePane_SelectionChangesInDiffMode_LoadsTheSelectedFile`：检查切换文件、Diff 开关与目录后的预览。请求 `extra` 目录时只读取新选中的 `notes.ts`。
  - `ChatFilePane_DelayedSourceError_KeepsTheLatestDiff`：读取 `deleted.ts` 时延迟转发 Server 的缺失文件响应。在响应送达前切回 diff，检查 diff 保留。再次进入文件查看时显示 `FileError`，并可切回 diff。

  现有回归用例 `ChatFilePane_DiffMode_RendersBothSidesWithMonacoWorker` 检查 Monaco diff 两栏、worker 与行评论。

- [x] **步骤 2：运行静态导出用例，确认通过**

  从仓库根目录运行 `dotnet build src/server/Agw.Standalone.Host`。在 `src/clients` 中运行 `pnpm --filter @agw/web test:e2e:export`。桌面宽度的 7 个用例通过，手机宽度的 7 个用例按 `test.skip` 跳过。

- [x] **步骤 3：格式化并检查**

  运行 `pnpm --filter @agw/web exec oxfmt -c ../.oxfmtrc.json e2e/file-code-view.spec.ts e2e/standalone-file-project.ts` 与 `pnpm --filter @agw/web lint`。

- [ ] **步骤 4：提交（用户要求时）**

  ```bash
  git add src/clients/web/e2e/file-code-view.spec.ts src/clients/web/e2e/standalone-file-project.ts
  git commit -m "test: cover the Diff mode file view in Web e2e"
  ```

---

### 任务 4：Desktop 集成测试

**文件：**

- 修改：`src/clients/desktop/src/main/extensions.integration.ts`（`monacoDiffAndComments`）

**接口：**

- 使用（任务 1、2）：按钮的 `aria-label`。使用现有的 `clickHost`、`clickFileInTree`、`untilHost`、`evalHost`、`selectedViewerIs`、`FILE_VIEWER_TABS`、`selectViewer`、`sourceIncludes`、`SOURCE_UNSAVED`、`saveStatusIs` 与 Source 输入和保存操作。
- `diffModeFileView(readmePath, readme)` 检查 Markdown 与 Image 查看器。`diffModeSourceEdits(readmePath, readme)` 检查修改保留和保存。`visibleMonacoText` 供 Source 与 diff 的修改侧共用。

- [x] **步骤 1：准备改动的图片**

  `GitCommandService` 用不带 `-uall` 的 `git status --porcelain` 列出改动，未跟踪目录只显示为一项，所以图片要先提交再修改。在 `monacoDiffAndComments` 中：

  - `const logoPath = join(project, "images", "logo.png");` 与 `const logo = await readFile(logoPath);`；
  - 初始提交改为 `git("add", "readme.md", "images/logo.png")`；
  - 修改 `readme.md` 的同时，用 `node:fs/promises` 的 `copyFile` 把 `resolve("assets", "tray-icon.png")` 复制到 `logoPath`（驱动的工作目录为 `src/clients/desktop`；文件顶部的导入中没有 `copyFile`、`resolve` 时加入）；
  - `finally` 中先 `await writeFile(logoPath, logo)`，再删除 `.git`。

- [x] **步骤 2：编写 `diffModeFileView` 与 `diffModeSourceEdits`**

  在 `monacoDiffAndComments` 清除待发送评论并选中 `readme.md` 后调用 `diffModeFileView`。按以下顺序检查。

  1. 点击 "View file"，检查选中 Mixed，标签为 `[MIXED_VIEWER, MARKDOWN_VIEWER, "source"]`，标题行有 "View changes"。
  2. 在 `diffModeSourceEdits` 中选择 Source 并输入 `file view edit`，检查保存状态。点击 "View changes" 后，检查修改侧仍显示工作区内容，标题行有 "View file, unsaved changes"。
  3. 再次进入文件查看，检查选中 Source 且输入仍在。保存后等文件内容更新和 Source 状态变为 "Saved"，点击 "View changes"，检查修改侧显示 `file view edit`。
  4. 选择 `images/logo.png`，检查二进制 diff 提示。点击 "View file"，检查选中 Image，标签为 `[IMAGE_VIEWER, "source"]`。
  5. `pass("Diff mode switches to the file view: readme.md opens in Mixed, Source edits survive returning to the diff and a save shows in it, and a changed image opens in the Image viewer")`。

  `visibleMonacoText` 按位置排列编辑器中可见的行，并将 Monaco 的不换行空格还原为空格。三个查看切换按钮的选择器使用共享常量。

- [x] **步骤 3：类型检查与 lint**

  运行：`pnpm --filter @agw/desktop typecheck`、`pnpm --filter @agw/desktop lint`
  预期：通过。

- [ ] **步骤 4：运行 Desktop 集成测试（运行前向用户确认）**

  - 确认范围包括 Diff 模式的 Markdown、图片、Source 修改与保存，以及现有 Files 和查看器步骤。运行会重新生成 worktree 内的 Desktop 构建产物。
  - 运行 `pnpm --filter @agw/desktop test:extensions`。first 阶段检查 `monacoDiffAndComments` 与 `diffModeFileView` 的 `PASS` 文案，restart 和 performance 阶段检查原有测试结果。
  - 若构建更新了 `desktop/renderer/next-env.d.ts`，把 `./.next/types/...` 引用改回 `./.next/dev/types/...`。

- [x] **步骤 5：格式化**

  运行：`pnpm --filter @agw/desktop exec oxfmt -c ../.oxfmtrc.json src/main/extensions.integration.ts`

- [ ] **步骤 6：提交（用户要求时）**

  ```bash
  git add src/clients/desktop/src/main/extensions.integration.ts
  git commit -m "test: cover the Diff mode file view in Desktop integration"
  ```

---

## 最终验证

- [x] 在 worktree 的 `src/clients` 中运行 `pnpm test`、`pnpm lint`、`pnpm fmt:check`。测试保留 6 个 Markdown CRLF 基线失败，lint 通过。全量格式检查报告 CRLF 文件，改动文件的 Oxfmt 检查通过。
- [x] 运行 `pnpm --filter @agw/projects test:browser`，27 个用例通过。
- [x] 运行 `pnpm --filter @agw/web test:e2e`，17 个用例通过，7 个手机宽度的文件查看用例跳过。
- [ ] 按任务 4 步骤 4 运行完整 Desktop 集成测试。first 与 restart 阶段检查 `PASS`，performance 阶段记录 `perf/tables-631.md` 的 keystroke 耗时，与本机基线对照。
