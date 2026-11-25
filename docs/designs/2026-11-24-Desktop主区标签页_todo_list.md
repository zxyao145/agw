# Desktop 主区标签页实施计划

> **供 agent 执行：** 必须使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐个任务执行本计划。步骤使用复选框（`- [ ]`）跟踪。

**目标：** Desktop 主区以标签页显示对话、文件、Jobs 与 extension 主区视图，单击打开预览标签，每个 Server Profile 的每个 Project 有独立的标签组，切换 Project 与重启后恢复，浮动对话有自己的对话记录。

**架构：** `ExtensionHost` 新增 `WorkspaceTabsStore`（当前标签组，经 bridge 读写主进程的 `workspace-tabs.json`），`WorkspaceLayoutStore` 只管理左侧栏与全屏视图。frame 按视图实例键记录，同一视图可以在多个标签中各有页面。`@agw/chat` 的 `ChatWorkspaceProvider` 分成 `ChatProjectProvider`、`ChatSessionProvider`、`FileDocumentProvider`，Desktop 为每个标签各放一份，Web 组合成原来的单对话、单文件布局。

**技术栈：** TypeScript、React 19、Electron（`Menu`、`ipcMain`）、zod、node:test + tsx + jsdom（`@agw/test-harness`）、Desktop extension 集成测试。

**设计文档：** `docs/designs/2026-11-24-Desktop主区标签页.md`。执行者同时阅读设计文档与本计划；本计划引用的“设计 x.y 节”都指这份文档。

## 全局约束

- 范围：Desktop renderer、Desktop main 与 preload、`@agw/chat`、`@agw/projects`、`@agw/extension-sdk`、`@agw/components` 的 `tokens.css`、Bare Repository extension。Server、数据库、OpenAPI 不变。Web 的界面与行为不变，`src/clients/web/e2e` 不修改且必须全部通过。
- 没有兼容性要求：旧的 `activeMainViewId` 状态、Desktop URL 的 `conversationId` 同步、输入草稿的 `…new` 键都不转换、不保留。
- 持久化文件：`<DesktopSettingsStore 目录>/workspace-tabs.json`，`schemaVersion: 1`；`extension.params` 序列化后不超过 64 KiB（65536 字节，按 UTF-8 计算）。
- `openMainView` 选项：`key` 1～256 个字符，`title` 1～100 个字符，`preview` 缺省 false；`openFile` 选项 `preview` 缺省 false。
- 界面文字（英文，逐字使用）：标签栏 `aria-label="Open tabs"`；关闭按钮 `aria-label="Close <标题>"`；预览 `aria-description="Preview"`；未保存 `aria-description="Unsaved changes"`；草稿标题 `New conversation`；没有标题的对话 `Conversation`；Jobs 标签 `Jobs`；右键菜单 `Close`、`Close Others`、`Close to the Right`、`Close All`、`Keep Open`；应用菜单 `Close Tab`、`Close Window`、`Next Tab`、`Previous Tab`；对话读取失败 `Failed to load the conversation: <消息>` 与按钮 `Retry`、`Close tab`；视图不可用 `This view isn't available. Enable or install its extension, or close this tab.`；目录不在 Project 中 `The directory is no longer part of this Project.`；保存失败 toast `Unable to save open tabs: <消息>`；无可见输入框 `No chat composer is visible.`；`agw.chat`/`agw.file` 的错误 `Conversations and files open as tabs from the sidebar or workspace.openFile.`；未加载标签组的错误 `The tab group is not loaded.`。
- 快捷键：`CmdOrCtrl+W` Close Tab；`Shift+CmdOrCtrl+W` Close Window；`Ctrl+Tab` Next Tab；`Ctrl+Shift+Tab` Previous Tab。
- 注释使用中英双语，技术术语保持英文，注释数量与周围代码相同；文件名为 kebab-case。
- 测试使用真实实现：主进程文件用 `createScratchDirectory`（`src/clients/desktop/src/main/scratch-directory.test-helper.ts`）创建的目录；渲染进程测试通过 `@desktop/main/...` 使用真实的 `WorkspaceTabsFile`；ExtensionHost 测试沿用 `extension-host.test-helper.ts` 的 `createMainProcess`；执行连接沿用 `@agw/test-harness` 的 `replaceExecutionHub` 与 `@agw/chat-runtime` 的 `configureExecutionRuntime`（与 `execution-session-manager.test.ts` 相同）；`@agw/chat` 的 Provider 测试沿用 `chat-workspace-hydration.test.ts` 的模块加载方式。出错直接抛出，不加备用处理。
- 修改文件只用编辑工具，不用 heredoc、`sed`、Python 脚本改代码；中间文件不放在 `/tmp`。
- 不执行 git commit；用户要求时再提交。每个任务结束时运行所改包的 typecheck 与 lint（任务 5 说明的例外除外）。
- 命令在 `src` 目录执行。单个测试文件：`pnpm --filter <包名> exec tsx --test <相对包目录的路径>`。包名：`@agw/desktop`、`@agw/chat`、`@agw/projects`、`@agw/extension-sdk`、`@agw/components`、`@agw/extension-bare-repository`。格式化：`pnpm --filter <包名> format`。
- Desktop 集成测试 `pnpm --filter @agw/desktop test:extensions` 每次运行前都请求用户确认，并写明这次运行要回答的问题；先用缩短版 `run()` 验证新增步骤。

## 审查重点

1. 读取标签组期间（切换 Project 后、启动时）收到 Mod+W、Ctrl+Tab、`workspace.openFile`、URL 中的 `conversationId`：命令被忽略，`openFile` 与 URL 等读取完成后执行，都不产生未处理的错误。测试：任务 4 的 `mutations before the group is read throw and whenLoaded waits for the read`，任务 7 的 `workspace.openFile waits for the group and opens a file tab without asking`。
2. 预览标签因为编辑变为固定后，再单击另一个文件：打开新的预览标签，原标签和它的未保存内容不受影响。测试：任务 4 的 `a preview tab pinned by an edit is not replaced by the next preview open`，任务 7 的 `unsaved changes are kept per view instance and pin the tab`。
3. 关闭多个标签（Close Others、Close All）时在未保存确认中取消：一个标签都不关闭。测试：任务 4 的 `canceling the unsaved confirmation closes nothing`。
4. 浮动对话中的对话从对话列表打开：对话只在新标签中显示，浮动对话变成新的草稿，两个 `Chat` 不会同时附着同一个对话。测试：任务 4 的 `opening the floating chat's conversation as a tab gives the floating chat a new draft`。
5. `workspace-tabs.json` 被手动改坏：读取抛出错误，主区显示错误边界，不会用新的标签组覆盖文件。测试：任务 1 的 `invalid content makes read throw and is not overwritten`，任务 4 的 `a failed read is kept in the snapshot and nothing is saved`。

## 文件结构

- `src/clients/desktop/src/shared/contracts/workspace-tabs.ts`【新增】：持久化类型与 `WorkspaceCommand`，只有类型。
- `src/clients/desktop/src/shared/workspace-tab-key.ts`【新增】：`tabKey`，主进程校验与渲染进程共用。
- `src/clients/desktop/src/main/settings/workspace-tabs-file.ts`【新增】：读写、校验、合并写入、清理。
- `src/clients/desktop/src/main/app-menu.ts`【新增】：应用菜单模板，运行时不导入 electron。
- `src/clients/desktop/renderer/src/runtime/extensions/workspace-tab.ts`【新增】：渲染进程的标签类型与纯函数。
- `src/clients/desktop/renderer/src/runtime/extensions/workspace-tabs-store.ts`【新增】：当前标签组的状态与规则。
- `src/clients/desktop/renderer/src/runtime/source/source-documents.ts`【新增】：按标签保存 Source 文档。
- `src/clients/desktop/renderer/src/runtime/components/workspace/` 下的 `workspace-tab-bar.tsx`、`chat-tab-panel.tsx`、`file-tab-panel.tsx`、`desktop-floating-chat.tsx`、`empty-workspace.tsx`【新增】。
- `src/clients/packages/chat/src/ui-web/pages/chat/` 下的 `chat-project-provider.tsx`、`chat-session-provider.tsx`、`file-document-provider.tsx`【新增】。
- 其余为设计变更清单中的【修改】文件。

---

### 任务 1：持久化类型与 WorkspaceTabsFile

**文件：**
- 新增：`src/clients/desktop/src/shared/contracts/workspace-tabs.ts`
- 修改：`src/clients/desktop/src/shared/contracts/index.ts`（导出新文件）
- 新增：`src/clients/desktop/src/shared/workspace-tab-key.ts`
- 新增：`src/clients/desktop/src/main/settings/workspace-tabs-file.ts`
- 测试：`src/clients/desktop/src/main/settings/workspace-tabs-file.test.ts`（新增）

**接口：**
- 产出（`contracts/workspace-tabs.ts`）：

```ts
export type JsonValue = null | boolean | number | string | JsonValue[] | { [key: string]: JsonValue };
export type PersistedChatTab = { id: string; kind: "chat"; preview: boolean; conversationId: string | null; draftId: string; draftSubmitted: boolean; title: string | null };
export type PersistedFileTab = { id: string; kind: "file"; preview: boolean; directoryId: string | null; path: string; diffScope: "staged" | "unstaged" | null; diffFileView: boolean };
export type PersistedJobsTab = { id: string; kind: "jobs"; preview: false };
export type PersistedExtensionTab = { id: string; kind: "extension"; preview: boolean; viewId: string; key: string | null; title: string | null; params: JsonValue };
export type PersistedTab = PersistedChatTab | PersistedFileTab | PersistedJobsTab | PersistedExtensionTab;
export type PersistedFloatingChat = { conversationId: string | null; draftId: string; draftSubmitted: boolean };
export type PersistedTabGroup = { tabs: PersistedTab[]; activeTabId: string | null; recentTabIds: string[]; floatingChat: PersistedFloatingChat };
export type WorkspaceTabsFileContent = { schemaVersion: 1; servers: Record<string, Record<string, PersistedTabGroup>> };
export type WorkspaceCommand = "closeTab" | "nextTab" | "previousTab";
```

- 产出（`shared/workspace-tab-key.ts`）：`export type TabIdentity = { kind: "chat"; conversationId: string | null; draftId: string } | { kind: "file"; directoryId: string | null; path: string } | { kind: "jobs" } | { kind: "extension"; viewId: string; key: string | null }`；`export function tabKey(tab: TabIdentity): string`（规则见设计 1.2 节）。
- 产出（`main/settings/workspace-tabs-file.ts`）：`export class WorkspaceTabsFile` 的 `constructor(directory: string)`、`read(serverId, projectId): Promise<PersistedTabGroup | null>`、`save(serverId, projectId, group: PersistedTabGroup): Promise<void>`、`pruneProjects(serverId, projectIds: readonly string[]): Promise<void>`、`pruneServers(profileIds: readonly string[]): Promise<void>`；`export const MAX_EXTENSION_PARAMS_BYTES = 65536`。

- [ ] **步骤 1：写失败的测试**

```ts
import assert from "node:assert/strict";
import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";

import type { PersistedTabGroup } from "../../shared/contracts";
import { tabKey } from "../../shared/workspace-tab-key";
import { createScratchDirectory } from "../scratch-directory.test-helper";
import { WorkspaceTabsFile } from "./workspace-tabs-file";

const DRAFT = "0196f3a4-1c2d-7e8f-9a0b-1c2d3e4f5a6b";

function group(overrides: Partial<PersistedTabGroup> = {}): PersistedTabGroup {
  return {
    tabs: [
      { id: "t1", kind: "chat", preview: false, conversationId: null, draftId: DRAFT, draftSubmitted: false, title: null },
      { id: "t2", kind: "file", preview: true, directoryId: null, path: "docs/a.md", diffScope: null, diffFileView: false },
    ],
    activeTabId: "t2",
    recentTabIds: ["t2", "t1"],
    floatingChat: { conversationId: null, draftId: "0196f3a4-1c2d-7e8f-9a0b-000000000001", draftSubmitted: false },
    ...overrides,
  };
}

async function scratchFile(t: import("node:test").TestContext) {
  const scratch = await createScratchDirectory("workspace-tabs");
  t.after(() => scratch.dispose());
  return { path: scratch.path, file: new WorkspaceTabsFile(scratch.path) };
}

test("tabKey identifies each kind of tab", () => {
  assert.equal(tabKey({ kind: "chat", conversationId: "c1", draftId: DRAFT }), "chat:c1");
  assert.equal(tabKey({ kind: "chat", conversationId: null, draftId: DRAFT }), `draft:${DRAFT}`);
  assert.equal(tabKey({ kind: "file", directoryId: null, path: "a/b.md" }), "file::a/b.md");
  assert.equal(tabKey({ kind: "file", directoryId: "d1", path: "a/b.md" }), "file:d1:a/b.md");
  assert.equal(tabKey({ kind: "jobs" }), "jobs");
  assert.equal(tabKey({ kind: "extension", viewId: "x:file", key: null }), "view:x:file");
  assert.equal(tabKey({ kind: "extension", viewId: "x:file", key: "r/main/a.ts" }), "view:x:file:r/main/a.ts");
});

test("read returns null for a Project without saved tabs", async (t) => {
  const { file } = await scratchFile(t);
  assert.equal(await file.read("local", "p1"), null);
});

test("save then read returns the group per Server and Project", async (t) => {
  const { file } = await scratchFile(t);
  await file.save("local", "p1", group());
  await file.save("remote", "p1", group({ activeTabId: "t1", recentTabIds: ["t1", "t2"] }));
  assert.deepEqual(await file.read("local", "p1"), group());
  assert.equal((await file.read("remote", "p1"))?.activeTabId, "t1");
  assert.equal(await file.read("local", "p2"), null);
});

test("invalid content makes read throw and is not overwritten", async (t) => {
  const { path, file } = await scratchFile(t);
  const content = '{"schemaVersion":1,"servers":{"local":{"p1":{"tabs":[]}}}}';
  await writeFile(join(path, "workspace-tabs.json"), content, "utf8");
  await assert.rejects(file.read("local", "p1"));
  await assert.rejects(file.save("local", "p2", group()));
  assert.equal(await readFile(join(path, "workspace-tabs.json"), "utf8"), content);
});

test("save rejects groups that break the validation rules", async (t) => {
  const { file } = await scratchFile(t);
  const [chat, doc] = group().tabs;
  const invalid: PersistedTabGroup[] = [
    group({ tabs: [chat, { ...doc, id: "t1" }] }),
    group({ tabs: [chat, doc, { ...doc, id: "t3" }], recentTabIds: ["t2", "t1", "t3"] }),
    group({ tabs: [{ ...chat, preview: true }, doc] }),
    group({ recentTabIds: ["t2"] }),
    group({ activeTabId: "missing" }),
    group({ tabs: [{ ...chat, draftId: "not-a-uuid" }, doc] }),
    group({ tabs: [{ ...chat, conversationId: "c1", draftSubmitted: true }, doc] }),
  ];
  for (const value of invalid) await assert.rejects(file.save("local", "p", value));
});

test("extension params over 64 KiB are rejected and exactly 64 KiB are accepted", async (t) => {
  const { file } = await scratchFile(t);
  const tab = (params: string) => ({ id: "t3", kind: "extension" as const, preview: false, viewId: "x:file", key: null, title: null, params });
  const withTab = (params: string) => group({ tabs: [...group().tabs, tab(params)], recentTabIds: ["t2", "t1", "t3"] });
  // JSON 字符串的两个引号也计入。The two quotes of the JSON string count too.
  await assert.rejects(file.save("local", "p", withTab("a".repeat(65535))));
  await file.save("local", "p", withTab("a".repeat(65534)));
});

test("concurrent saves of one Project write the last group", async (t) => {
  const { file } = await scratchFile(t);
  await Promise.all([
    file.save("local", "p1", group({ activeTabId: "t1", recentTabIds: ["t1", "t2"] })),
    file.save("local", "p1", group()),
    file.save("local", "p2", group()),
  ]);
  assert.equal((await file.read("local", "p1"))?.activeTabId, "t2");
  assert.notEqual(await file.read("local", "p2"), null);
});

test("a read issued after a save that is still writing returns the saved group", async (t) => {
  const { file } = await scratchFile(t);
  const saving = file.save("local", "p1", group());
  assert.deepEqual(await file.read("local", "p1"), group());
  await saving;
});

test("pruneProjects and pruneServers keep only the given ids", async (t) => {
  const { file } = await scratchFile(t);
  await file.save("local", "p1", group());
  await file.save("local", "p2", group());
  await file.save("remote", "p1", group());
  await file.pruneProjects("local", ["p2"]);
  assert.equal(await file.read("local", "p1"), null);
  assert.notEqual(await file.read("local", "p2"), null);
  await file.pruneServers(["local"]);
  assert.equal(await file.read("remote", "p1"), null);
});
```

- [ ] **步骤 2：运行测试，确认失败**

运行：`pnpm --filter @agw/desktop exec tsx --test src/main/settings/workspace-tabs-file.test.ts`
预期：FAIL，找不到模块 `./workspace-tabs-file`。

- [ ] **步骤 3：实现类型、`tabKey` 与 `WorkspaceTabsFile`**

zod 模式只放在 `workspace-tabs-file.ts`（渲染进程不导入 zod）：`draftId` 用正则 `/^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/u`；`params` 用 `z.json()`，再检查 `Buffer.byteLength(JSON.stringify(params), "utf8") <= MAX_EXTENSION_PARAMS_BYTES`；组级规则（设计“数据存储设计”的校验规则）用 `superRefine`，标识重复用 `tabKey` 判断。写入：所有写入经过一个 Promise 链按顺序执行；`pending: Map<string, PersistedTabGroup>`（键为 `` `${serverId}\n${projectId}` ``）保存等待写入的最新内容，链上的任务读取并校验当前文件、合并 `pending` 的全部内容、清空 `pending` 后用现有 `writeJsonFile` 写出。`save` 先校验参数，校验失败直接拒绝，不进入 `pending`。`read`、`pruneProjects`、`pruneServers` 也排在同一个链上，读到此前全部 `save` 的结果。

- [ ] **步骤 4：运行测试，确认通过**

运行：同步骤 2。预期：9 个测试 PASS。

---

### 任务 2：Bridge、IPC 与应用菜单

**文件：**
- 修改：`src/clients/desktop/src/shared/contracts/bridge.ts`
- 修改：`src/clients/desktop/src/preload/index.ts`
- 新增：`src/clients/desktop/src/main/app-menu.ts`
- 修改：`src/clients/desktop/src/main/index.ts`
- 修改：`src/clients/desktop/renderer/src/runtime/extensions/extension-host.test-helper.ts`、`src/clients/desktop/renderer/src/runtime/server-connection.integration.ts`（bridge 对象加入新方法，委托给真实 `WorkspaceTabsFile`）
- 测试：`src/clients/desktop/src/main/app-menu.test.ts`（新增）

**接口：**
- 消费：任务 1。
- 产出（`AgwDesktopBridge` 新增）：`getWorkspaceTabs(serverId: string, projectId: string): Promise<PersistedTabGroup | null>`、`saveWorkspaceTabs(serverId: string, projectId: string, group: PersistedTabGroup): Promise<void>`、`pruneWorkspaceTabs(serverId: string, projectIds: string[]): Promise<void>`、`onWorkspaceCommand(listener: (command: WorkspaceCommand) => void): () => void`。IPC 通道 `agw:get-workspace-tabs`、`agw:save-workspace-tabs`、`agw:prune-workspace-tabs`、`agw:workspace-command`。
- 产出（`app-menu.ts`）：`export function buildApplicationMenuTemplate(platform: NodeJS.Platform, send: (command: WorkspaceCommand) => void): MenuItemConstructorOptions[]`（只 `import type` electron）。

- [ ] **步骤 1：写失败的测试**

```ts
import assert from "node:assert/strict";
import test from "node:test";
import type { MenuItemConstructorOptions } from "electron";

import type { WorkspaceCommand } from "../shared/contracts";
import { buildApplicationMenuTemplate } from "./app-menu";

function item(template: MenuItemConstructorOptions[], label: string): MenuItemConstructorOptions {
  for (const entry of template) {
    const found = Array.isArray(entry.submenu) ? entry.submenu.find((child) => child.label === label) : undefined;
    if (found) return found;
  }
  throw new Error(`No menu item ${label}.`);
}

for (const platform of ["darwin", "win32", "linux"] as const) {
  test(`the ${platform} menu sends tab commands with their accelerators`, () => {
    const sent: WorkspaceCommand[] = [];
    const template = buildApplicationMenuTemplate(platform, (command) => sent.push(command));
    assert.equal(item(template, "Close Tab").accelerator, "CmdOrCtrl+W");
    assert.equal(item(template, "Close Window").accelerator, "Shift+CmdOrCtrl+W");
    assert.equal(item(template, "Close Window").role, "close");
    assert.equal(item(template, "Next Tab").accelerator, "Ctrl+Tab");
    assert.equal(item(template, "Previous Tab").accelerator, "Ctrl+Shift+Tab");
    for (const label of ["Close Tab", "Next Tab", "Previous Tab"]) (item(template, label).click as () => void)();
    assert.deepEqual(sent, ["closeTab", "nextTab", "previousTab"]);
    assert.ok(template.some((entry) => entry.role === "editMenu"));
    assert.ok(template.some((entry) => entry.role === "viewMenu"));
    assert.equal(template.some((entry) => entry.role === "appMenu"), platform === "darwin");
  });
}
```

- [ ] **步骤 2：运行测试，确认失败**

运行：`pnpm --filter @agw/desktop exec tsx --test src/main/app-menu.test.ts`
预期：FAIL，找不到模块 `./app-menu`。

- [ ] **步骤 3：实现**

菜单结构按设计 5.4 节。`index.ts`：应用 ready 后 `Menu.setApplicationMenu(Menu.buildFromTemplate(buildApplicationMenuTemplate(process.platform, (command) => mainWindow?.webContents.send("agw:workspace-command", command))))`；用 `DesktopSettingsStore` 的目录创建 `WorkspaceTabsFile`；三个 `ipcMain.handle` 直接调用对应方法；保存设置的处理函数（调用 `settingsStore.save(settings)` 处）在 `settings.profiles` 存在时随后调用 `workspaceTabsFile.pruneServers(settings.profiles.map((profile) => profile.id))`。preload 的 `onWorkspaceCommand` 与现有事件订阅写法相同（`ipcRenderer.on`，返回取消订阅的函数）。

- [ ] **步骤 4：运行测试与检查**

运行：同步骤 2，预期 3 个测试 PASS；`pnpm --filter @agw/desktop typecheck` 通过。

---

### 任务 3：渲染进程的标签模型

**文件：**
- 新增：`src/clients/desktop/renderer/src/runtime/extensions/workspace-tab.ts`
- 测试：`src/clients/desktop/renderer/src/runtime/extensions/workspace-tab.test.ts`（新增）

**接口：**
- 消费：任务 1。
- 产出：设计 1.1 节的 `ChatTab`、`FileTab`、`JobsTab`、`ExtensionTab`（含 `sequence: number`）、`WorkspaceTab`、`TabGroup`；`export type FileLocation = { directoryId: string | null; path: string; diffScope: GitDiffScope | null }`；`createInitialTabGroup(): TabGroup`；`toPersistedTabGroup(group: TabGroup): PersistedTabGroup`；`fromPersistedTabGroup(group: PersistedTabGroup): TabGroup`；`activeViewIdOf(tab: WorkspaceTab): string`；`newDraftId(): string`（`createUuidV7()`，来自 `@agw/api`）；`newTabId(): string`（`crypto.randomUUID()`）；再导出 `tabKey`。

- [ ] **步骤 1：写失败的测试**

```ts
import assert from "node:assert/strict";
import test from "node:test";

import { BUILT_IN_VIEWS } from "./workspace-ids";
import { activeViewIdOf, createInitialTabGroup, fromPersistedTabGroup, toPersistedTabGroup, type ExtensionTab } from "./workspace-tab";

const UUID_V7 = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/u;

test("a new group has one active pinned draft conversation and a separate floating draft", () => {
  const group = createInitialTabGroup();
  const [tab] = group.tabs;
  assert.equal(group.tabs.length, 1);
  assert.ok(tab.kind === "chat");
  assert.equal(tab.conversationId, null);
  assert.equal(tab.preview, false);
  assert.equal(tab.draftSubmitted, false);
  assert.match(tab.draftId, UUID_V7);
  assert.equal(group.activeTabId, tab.id);
  assert.deepEqual(group.recentTabIds, [tab.id]);
  assert.match(group.floatingChat.draftId, UUID_V7);
  assert.notEqual(group.floatingChat.draftId, tab.draftId);
  assert.equal(group.floatingChat.draftSubmitted, false);
});

test("persisting drops the extension sequence and reading starts it at 0", () => {
  const extension: ExtensionTab = { id: "e", kind: "extension", preview: false, viewId: "x:main", key: null, title: null, params: { a: 1 }, sequence: 4 };
  const group = { ...createInitialTabGroup(), tabs: [extension], activeTabId: "e", recentTabIds: ["e"] };
  const persisted = toPersistedTabGroup(group);
  assert.equal("sequence" in persisted.tabs[0], false);
  assert.deepEqual(fromPersistedTabGroup(persisted).tabs[0], { ...extension, sequence: 0 });
});

test("activeViewIdOf maps tabs to their view ids", () => {
  assert.equal(activeViewIdOf(createInitialTabGroup().tabs[0]), BUILT_IN_VIEWS.chat);
  assert.equal(activeViewIdOf({ id: "f", kind: "file", preview: false, directoryId: null, path: "a", diffScope: null, diffFileView: false }), BUILT_IN_VIEWS.file);
  assert.equal(activeViewIdOf({ id: "j", kind: "jobs", preview: false }), BUILT_IN_VIEWS.jobs);
  assert.equal(activeViewIdOf({ id: "e", kind: "extension", preview: false, viewId: "x:main", key: null, title: null, params: null, sequence: 0 }), "x:main");
});
```

- [ ] **步骤 2：运行测试，确认失败**

运行：`pnpm --filter @agw/desktop exec tsx --test renderer/src/runtime/extensions/workspace-tab.test.ts`
预期：FAIL，找不到模块 `./workspace-tab`。

- [ ] **步骤 3：实现 `workspace-tab.ts`**，类型带设计 1.1 节的双语文档注释。

- [ ] **步骤 4：运行测试，确认通过**

运行：同步骤 2。预期：3 个测试 PASS。

---

### 任务 4：WorkspaceTabsStore

**文件：**
- 新增：`src/clients/desktop/renderer/src/runtime/extensions/workspace-tabs-store.ts`
- 测试：`src/clients/desktop/renderer/src/runtime/extensions/workspace-tabs-store.test.ts`（新增）

**接口：**
- 消费：任务 1、2、3。
- 产出：设计第 2 节的 `TabGroupScope`、`WorkspaceTabsSnapshot`（含 `error`）、`TabResources`（含 `discardDrafts`）与 `WorkspaceTabsStore` 的全部方法；`export type WorkspaceTabsBridge = Pick<AgwDesktopBridge, "getWorkspaceTabs" | "saveWorkspaceTabs">`；`export type ExtensionTabOptions = { key: string | null; title: string | null; preview: boolean }`。`setScope` 不会以错误结束（读取错误写入快照的 `error`），`whenLoaded` 以读取错误结束；`close` 返回 `Promise<boolean>`。

- [ ] **步骤 1：写失败的测试**

```ts
import assert from "node:assert/strict";
import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import test, { type TestContext } from "node:test";

import type { PersistedTabGroup } from "@desktop/shared/contracts";
import { createScratchDirectory } from "@desktop/main/scratch-directory.test-helper";
import { WorkspaceTabsFile } from "@desktop/main/settings/workspace-tabs-file";
import { type TabResources, WorkspaceTabsStore } from "./workspace-tabs-store";

const A = { serverId: "local", projectId: "project-a" };
const B = { serverId: "local", projectId: "project-b" };

/** 真实的持久化文件；资源回调记录 store 的输出。A real tabs file; the resource callbacks record the store's output. */
async function setup(t: TestContext, options: { answer?: boolean; fileContent?: string } = {}) {
  const scratch = await createScratchDirectory("workspace-tabs-store");
  t.after(() => scratch.dispose());
  if (options.fileContent !== undefined) await writeFile(join(scratch.path, "workspace-tabs.json"), options.fileContent, "utf8");
  const file = new WorkspaceTabsFile(scratch.path);
  const released: { tabIds: string[]; keepUnsaved: boolean }[] = [];
  const confirmed: string[][] = [];
  const discarded: string[] = [];
  const resources: TabResources = {
    confirmDiscard: async (tabIds) => {
      confirmed.push([...tabIds]);
      return options.answer ?? true;
    },
    release: (tabIds, keepUnsaved) => released.push({ tabIds: [...tabIds], keepUnsaved }),
    discardDrafts: (_scope, draftIds) => discarded.push(...draftIds),
  };
  const store = new WorkspaceTabsStore({
    bridge: {
      getWorkspaceTabs: (serverId, projectId) => file.read(serverId, projectId),
      saveWorkspaceTabs: (serverId, projectId, group) => file.save(serverId, projectId, group),
    },
    resources,
    reportError: (_message, error) => {
      throw error;
    },
  });
  await store.setScope(A);
  const group = () => store.getSnapshot().group!;
  const keys = () =>
    group().tabs.map((tab) => (tab.kind === "file" ? tab.path : tab.kind === "chat" ? (tab.conversationId ?? "draft") : tab.kind));
  const active = () => group().tabs.find((tab) => tab.id === group().activeTabId);
  /** 等到文件中保存的标签组满足条件。Waits until the saved group matches. */
  const saved = async (matches: (group: PersistedTabGroup) => boolean) => {
    for (let attempt = 0; attempt < 100; attempt += 1) {
      const value = await file.read(A.serverId, A.projectId);
      if (value && matches(value)) return value;
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
    throw new Error("The expected group was not saved.");
  };
  return { store, directory: scratch.path, released, confirmed, discarded, group, keys, active, saved };
}

const file = (path: string, directoryId: string | null = null) => ({ directoryId, path, diffScope: null });

test("a Project without saved tabs starts with one pinned draft conversation and saves it", async (t) => {
  const { keys, saved } = await setup(t);
  assert.deepEqual(keys(), ["draft"]);
  await saved((value) => value.tabs.length === 1);
});

test("opening the same conversation twice activates one tab", async (t) => {
  const { store, keys, active } = await setup(t);
  store.openConversation("c1", { preview: false });
  store.openFile(file("a.md"), { preview: false });
  store.openConversation("c1", { preview: true });
  assert.deepEqual(keys(), ["draft", "c1", "a.md"]);
  assert.ok(active()?.kind === "chat" && active()?.conversationId === "c1");
});

test("new tabs open to the right of the active tab", async (t) => {
  const { store, group, keys } = await setup(t);
  store.openFile(file("a.md"), { preview: false });
  store.openFile(file("b.md"), { preview: false });
  store.activate(group().tabs[1].id);
  store.openFile(file("c.md"), { preview: false });
  assert.deepEqual(keys(), ["draft", "a.md", "c.md", "b.md"]);
});

test("a preview open replaces the preview tab in place and releases it", async (t) => {
  const { store, group, keys, released } = await setup(t);
  store.openFile(file("a.md"), { preview: true });
  const previewId = group().tabs[1].id;
  store.openFile(file("b.md"), { preview: true });
  assert.deepEqual(keys(), ["draft", "b.md"]);
  assert.equal(group().tabs[1].preview, true);
  assert.notEqual(group().tabs[1].id, previewId);
  assert.deepEqual(released.at(-1), { tabIds: [previewId], keepUnsaved: false });
});

test("a pinned open of an existing preview tab pins it; a pinned tab never becomes a preview", async (t) => {
  const { store, group } = await setup(t);
  store.openFile(file("a.md"), { preview: true });
  store.openFile(file("a.md"), { preview: false });
  assert.equal(group().tabs[1].preview, false);
  store.openFile(file("a.md"), { preview: true });
  assert.equal(group().tabs[1].preview, false);
});

test("a preview tab pinned by an edit is not replaced by the next preview open", async (t) => {
  const { store, group, keys } = await setup(t);
  store.openFile(file("a.md"), { preview: true });
  store.pin(group().tabs[1].id);
  store.openFile(file("b.md"), { preview: true });
  assert.deepEqual(keys(), ["draft", "a.md", "b.md"]);
  assert.deepEqual(group().tabs.map((tab) => tab.preview), [false, false, true]);
});

test("closing the active tab activates the most recently used remaining tab", async (t) => {
  const { store, group, active } = await setup(t);
  store.openFile(file("a.md"), { preview: false });
  store.openFile(file("b.md"), { preview: false });
  const [draft, a, b] = group().tabs;
  store.activate(draft.id);
  store.activate(b.id);
  assert.equal(await store.close([b.id]), true);
  assert.equal(active()?.id, draft.id);
  assert.deepEqual(group().recentTabIds, [draft.id, a.id]);
});

test("closing the last tab leaves an empty group", async (t) => {
  const { store, group, saved } = await setup(t);
  await store.close([group().tabs[0].id]);
  assert.deepEqual(group().tabs, []);
  assert.equal(group().activeTabId, null);
  await saved((value) => value.activeTabId === null && value.tabs.length === 0);
});

test("canceling the unsaved confirmation closes nothing", async (t) => {
  const { store, group, confirmed, released } = await setup(t, { answer: false });
  store.openFile(file("a.md"), { preview: false });
  const ids = group().tabs.map((tab) => tab.id);
  assert.equal(await store.close(ids), false);
  assert.deepEqual(confirmed, [ids]);
  assert.equal(group().tabs.length, 2);
  assert.deepEqual(released, []);
});

test("closing a draft tab discards its drafts", async (t) => {
  const { store, group, discarded } = await setup(t);
  const draft = group().tabs[0];
  assert.ok(draft.kind === "chat");
  await store.close([draft.id]);
  assert.deepEqual(discarded, [draft.draftId]);
});

test("accepting a draft requires its draftId and turns it into the conversation's tab", async (t) => {
  const { store, group, keys } = await setup(t);
  const draft = group().tabs[0];
  assert.ok(draft.kind === "chat");
  assert.throws(() => store.acceptConversation(draft.id, "other"));
  store.setDraftSubmitted(draft.id, true);
  store.acceptConversation(draft.id, draft.draftId);
  assert.deepEqual(keys(), [draft.draftId]);
  const accepted = group().tabs[0];
  assert.ok(accepted.kind === "chat" && accepted.draftSubmitted === false);
});

test("opening the floating chat's conversation as a tab gives the floating chat a new draft", async (t) => {
  const { store, group, keys, discarded } = await setup(t);
  const floatingDraft = group().floatingChat.draftId;
  store.setFloatingDraftSubmitted(true);
  store.acceptFloatingConversation(floatingDraft);
  store.openConversation(floatingDraft, { preview: true });
  assert.ok(keys().includes(floatingDraft));
  assert.equal(group().floatingChat.conversationId, null);
  assert.notEqual(group().floatingChat.draftId, floatingDraft);
  assert.deepEqual(discarded, []);
});

test("a new floating conversation discards the old draft", async (t) => {
  const { store, group, discarded } = await setup(t);
  const old = group().floatingChat.draftId;
  store.newFloatingConversation();
  assert.notEqual(group().floatingChat.draftId, old);
  assert.deepEqual(discarded, [old]);
});

test("removing conversations closes their tabs without asking and resets the floating chat", async (t) => {
  const { store, group, keys, confirmed } = await setup(t);
  store.openConversation("c1", { preview: false });
  store.openConversation("c2", { preview: false });
  store.removeConversations(["c1"]);
  assert.deepEqual(keys(), ["draft", "c2"]);
  store.acceptFloatingConversation(group().floatingChat.draftId);
  store.removeConversations("all");
  assert.deepEqual(keys(), []);
  assert.equal(group().floatingChat.conversationId, null);
  assert.deepEqual(confirmed, []);
});

test("removing a directory closes the file tabs under it", async (t) => {
  const { store, keys } = await setup(t);
  store.openFile(file("docs/a.md"), { preview: false });
  store.openFile(file("docs/sub/b.md"), { preview: false });
  store.openFile(file("docs-other/c.md"), { preview: false });
  store.openFile(file("docs/a.md", "d2"), { preview: false });
  store.removeFiles(null, "docs");
  assert.deepEqual(keys(), ["draft", "docs-other/c.md", "docs/a.md"]);
});

test("opening a file from Diff mode switches the existing tab to that scope", async (t) => {
  const { store, group } = await setup(t);
  store.openFile(file("a.md"), { preview: false });
  store.setFileView(group().tabs[1].id, { diffScope: null, diffFileView: true });
  store.openFile({ directoryId: null, path: "a.md", diffScope: "staged" }, { preview: true });
  const tab = group().tabs[1];
  assert.ok(tab.kind === "file" && tab.diffScope === "staged" && tab.diffFileView === false && tab.preview === false);
});

test("move reorders tabs and pins a moved preview tab", async (t) => {
  const { store, group, keys } = await setup(t);
  store.openFile(file("a.md"), { preview: true });
  store.move(group().tabs[1].id, 0);
  assert.deepEqual(keys(), ["a.md", "draft"]);
  assert.equal(group().tabs[0].preview, false);
});

test("activateAdjacent follows the bar order and wraps around", async (t) => {
  const { store, group, active } = await setup(t);
  store.openFile(file("a.md"), { preview: false });
  store.activateAdjacent(1);
  assert.equal(active()?.id, group().tabs[0].id);
  store.activateAdjacent(-1);
  assert.equal(active()?.id, group().tabs[1].id);
});

test("extension tabs share a tab per view without a key and open one tab per key", async (t) => {
  const { store, group } = await setup(t);
  store.openExtensionView("x:repo", { action: "clone" }, { key: null, title: null, preview: false });
  store.openExtensionView("x:repo", { feature: "f" }, { key: null, title: null, preview: false });
  store.openExtensionView("x:file", { path: "a" }, { key: "r/a", title: "a", preview: true });
  store.openExtensionView("x:file", { path: "b" }, { key: "r/b", title: "b", preview: false });
  const tabs = group().tabs.filter((tab) => tab.kind === "extension");
  assert.equal(tabs.length, 3);
  assert.ok(tabs[0].kind === "extension");
  assert.equal(tabs[0].sequence, 1);
  assert.deepEqual(tabs[0].params, { feature: "f" });
});

test("switching scopes releases the old tabs keeping unsaved ones and restores each group", async (t) => {
  const { store, group, keys, released } = await setup(t);
  store.openFile(file("a.md"), { preview: false });
  const idsA = group().tabs.map((tab) => tab.id);
  const switching = store.setScope(B);
  assert.equal(store.getSnapshot().group, null);
  await switching;
  assert.deepEqual(released.at(-1), { tabIds: idsA, keepUnsaved: true });
  assert.deepEqual(keys(), ["draft"]);
  await store.setScope(A);
  assert.deepEqual(keys(), ["draft", "a.md"]);
});

test("a read that finishes after another scope switch is dropped", async (t) => {
  const { store, keys } = await setup(t);
  store.openFile(file("a.md"), { preview: false });
  await Promise.all([store.setScope(B), store.setScope(A)]);
  assert.deepEqual(store.getSnapshot().scope, A);
  assert.deepEqual(keys(), ["draft", "a.md"]);
});

test("mutations before the group is read throw and whenLoaded waits for the read", async (t) => {
  const { store } = await setup(t);
  const switching = store.setScope(B);
  assert.throws(() => store.openJobs(), /The tab group is not loaded\./u);
  await store.whenLoaded();
  await switching;
  store.openJobs();
});

test("a failed read is kept in the snapshot and nothing is saved", async (t) => {
  const { store, directory } = await setup(t, { fileContent: "{" });
  assert.equal(store.getSnapshot().group, null);
  assert.ok(store.getSnapshot().error instanceof Error);
  await assert.rejects(store.whenLoaded());
  assert.throws(() => store.openJobs(), /The tab group is not loaded\./u);
  assert.equal(await readFile(join(directory, "workspace-tabs.json"), "utf8"), "{");
});
```

- [ ] **步骤 2：运行测试，确认失败**

运行：`pnpm --filter @agw/desktop exec tsx --test renderer/src/runtime/extensions/workspace-tabs-store.test.ts`
预期：FAIL，找不到模块 `./workspace-tabs-store`。

- [ ] **步骤 3：实现 `WorkspaceTabsStore`**

规则按设计第 2 节、1.3 节、第 6 节、7.3～7.5 节、8.4 节、8.6 节、10.3 节与第 11 节。`setScope` 用递增的 `generation` 丢弃过期读取；`whenLoaded` 返回当前 generation 的读取 Promise。每次修改经过私有 `commit(next: TabGroup)`：更新快照、通知订阅者、调用 `bridge.saveWorkspaceTabs(...).catch((error) => reportError("Unable to save open tabs", error))`。`removeFiles(directoryId, path)` 匹配目录相同且 `tab.path === path || tab.path.startsWith(`${path}/`)`。`openConversation` 在浮动对话的 `conversationId` 等于该对话时，同一次 `commit` 中把浮动对话换成 `{ conversationId: null, draftId: newDraftId(), draftSubmitted: false }`，不删除草稿（被移出的是已受理的对话）。

- [ ] **步骤 4：运行测试，确认通过**

运行：同步骤 2。预期：23 个测试 PASS。

---

### 任务 5：布局状态、全屏视图与 Ribbon

**文件：**
- 修改：`src/clients/desktop/renderer/src/runtime/extensions/workspace-ids.ts`
- 修改：`src/clients/desktop/renderer/src/runtime/extensions/workspace-layout-store.ts`
- 修改：`src/clients/desktop/renderer/src/runtime/components/workspace/ribbon.tsx`
- 测试：`src/clients/desktop/renderer/src/runtime/extensions/workspace-layout-store.test.ts`、`src/clients/desktop/renderer/src/runtime/components/workspace/ribbon.test.ts`（修改）

**接口：**
- 消费：任务 3、4。
- 产出（`workspace-ids.ts`）：`FULL_WORKSPACE_VIEWS: ReadonlySet<string>`（`agw.events`、`agw.notifications`）、`sidebarInstanceKey(viewId)`、`tabInstanceKey(tabId)`、`fileViewerInstanceKey(tabId, viewId)`、`settingsInstanceKey(extensionId)`，格式见设计 10.2 节。
- 产出（`workspace-layout-store.ts`）：设计第 4 节的 `WorkspaceLayoutState`；`constructor(registry: WorkspaceRegistry, tabs: WorkspaceTabsStore)`；`openMainView(id: string, params?: JsonValue, options?: Partial<ExtensionTabOptions>): void`；`leaveFullWorkspaceView(): void`；`DEFAULT_WORKSPACE_LAYOUT.fullWorkspaceViewId === null`。
- 产出（`ribbon.tsx`）：`Ribbon` 的属性 `activeMainViewId: string | null`（调用方按设计第 4 节计算）。

- [ ] **步骤 1：修改测试**

`createStore` 改为 `async createStore(t)`：在任务 4 的方式下创建真实 `WorkspaceTabsFile` 与 `WorkspaceTabsStore` 并 `await tabs.setScope(...)`，返回 `{ registry, layout, tabs }`。删除依赖 `activeMainViewId` 的旧用例，新增：

```ts
test("Events hides the sidebar and the tabs; leaving restores the sidebar", async (t) => {
  const { layout } = await createStore(t);
  layout.openMainView(BUILT_IN_VIEWS.events);
  assert.equal(layout.getSnapshot().fullWorkspaceViewId, BUILT_IN_VIEWS.events);
  assert.equal(layout.getSnapshot().sidebarVisible, false);
  layout.showSidebarView(BUILT_IN_VIEWS.files);
  assert.equal(layout.getSnapshot().fullWorkspaceViewId, null);
  assert.equal(layout.getSnapshot().sidebarVisible, true);
  assert.equal(layout.getSnapshot().activeSidebarViewId, BUILT_IN_VIEWS.files);
});

test("leaving a full view restores a hidden sidebar as hidden", async (t) => {
  const { layout } = await createStore(t);
  layout.setSidebarVisible(false);
  layout.openMainView(BUILT_IN_VIEWS.notifications, { notificationId: "n1" });
  assert.deepEqual(layout.getSnapshot().openRequests[BUILT_IN_VIEWS.notifications]?.params, { notificationId: "n1" });
  layout.leaveFullWorkspaceView();
  assert.equal(layout.getSnapshot().sidebarVisible, false);
});

test("openMainView opens Jobs and extension views as tabs and leaves a full view", async (t) => {
  const { layout, tabs } = await createStore(t);
  layout.openMainView(BUILT_IN_VIEWS.events);
  layout.openMainView(BUILT_IN_VIEWS.jobs);
  layout.openMainView("sample:app", { a: 1 }, { key: "k", title: "K", preview: true });
  assert.equal(layout.getSnapshot().fullWorkspaceViewId, null);
  assert.deepEqual(tabs.getSnapshot().group!.tabs.map((tab) => tab.kind), ["chat", "jobs", "extension"]);
});

test("conversations and files cannot be opened as main views and built-ins take no options", async (t) => {
  const { layout } = await createStore(t);
  assert.throws(() => layout.openMainView(BUILT_IN_VIEWS.chat), { code: "ERR_UNSUPPORTED_OPERATION" });
  assert.throws(() => layout.openMainView(BUILT_IN_VIEWS.file), { code: "ERR_UNSUPPORTED_OPERATION" });
  assert.throws(() => layout.openMainView(BUILT_IN_VIEWS.jobs, {}, { preview: true }), { code: "ERR_INVALID_ARGUMENT" });
  assert.throws(() => layout.openMainView("sample:list"), { code: "ERR_UNKNOWN_VIEW" });
});
```

`ribbon.test.ts`：把 `activeMainViewId` 改为 `string | null`，新增 `null` 时没有 `openMainView` 条目高亮的断言。

- [ ] **步骤 2：运行测试，确认失败**

运行：`pnpm --filter @agw/desktop exec tsx --test renderer/src/runtime/extensions/workspace-layout-store.test.ts renderer/src/runtime/components/workspace/ribbon.test.ts`
预期：FAIL（构造函数参数与 `fullWorkspaceViewId` 不存在）。

- [ ] **步骤 3：实现**

按设计第 4 节。进入全屏视图时把进入前的 `sidebarVisible` 保存在私有字段；`showSidebarView`、`setSidebarVisible(true)` 与非全屏视图的 `openMainView` 先调用 `leaveFullWorkspaceView()`。`fallBackToDefaults` 只处理侧栏。

- [ ] **步骤 4：运行测试，确认通过**

运行：同步骤 2。预期：全部 PASS。`ExtensionHost` 与组件的类型错误在任务 7、15 修复，本任务不运行 desktop 的 typecheck。

---

### 任务 6：输入草稿的键

**文件：**
- 修改：`src/clients/packages/chat/src/lib/chat/conversation-composer-storage.ts`、`conversation-image-drafts.ts`
- 修改：`src/clients/packages/chat/src/ui-web/components/message/use-conversation-image-draft.ts`、`chat.tsx`、`src/clients/packages/chat/src/ui-web/pages/chat/chat-workspace.tsx`（调用点）
- 修改：`src/clients/packages/chat/src/index.ts`（导出 `ComposerDraftKey`、`conversationComposerStorage`、`conversationImageDrafts`）
- 测试：`src/clients/packages/chat/src/lib/chat/conversation-composer-storage.test.ts`（修改）

**接口：**
- 产出：`export type ComposerDraftKey = { conversationId: string } | { draftId: string }`；`conversationComposerStorage.get(scope, key: ComposerDraftKey)`、`set(scope, key, values)`、`remove(scope, key)`、`move(scope, from: ComposerDraftKey, to: ComposerDraftKey): void`、`removeConversations(scope)`（同时删除该范围的草稿键）；`conversationImageDrafts.get(scope, key)`、`accept(scope, from: ComposerDraftKey, to: ComposerDraftKey)`、`remove(scope, key)`、`removeConversations(scope)`；`getStorageKey(scope, key)` 的后缀为 `conversation:<encodeURIComponent(id)>` 或 `draft:<encodeURIComponent(draftId)>`。

- [ ] **步骤 1：修改测试**

新增用例 `draft and conversation keys are separate and move transfers the values`：`getStorageKey(scope, { draftId: "d1" })` 以 `draft:d1` 结尾，`{ conversationId: "c1" }` 以 `conversation:c1` 结尾；向 `{ draftId: "d1" }` 写入 `{ input: "hello" }` 后 `{ draftId: "d2" }` 为空；`move(scope, { draftId: "d1" }, { conversationId: "c1" })` 后 `get(scope, { draftId: "d1" })` 为 `{}`、`get(scope, { conversationId: "c1" })` 为 `{ input: "hello" }`。删除使用 `null` 键的旧用例。

- [ ] **步骤 2：运行测试，确认失败**

运行：`pnpm --filter @agw/chat exec tsx --test src/lib/chat/conversation-composer-storage.test.ts`
预期：FAIL。

- [ ] **步骤 3：实现**，现有调用点改为 `{ conversationId }`；`conversationId` 为 null 的调用点改为 `{ draftId: "web" }`（任务 9、13 改为使用 `draftId` 属性）。

- [ ] **步骤 4：运行测试与检查**

运行：同步骤 2，预期 PASS；`pnpm --filter @agw/chat test`、`typecheck`、`lint` 通过。

---

### 任务 7：ExtensionHost 的视图实例与标签资源

**文件：**
- 新增：`src/clients/desktop/renderer/src/runtime/source/source-documents.ts`
- 修改：`src/clients/desktop/renderer/src/runtime/extensions/extension-host.ts`
- 修改：`src/clients/desktop/renderer/src/runtime/extensions/context-service.ts`
- 测试：`src/clients/desktop/renderer/src/runtime/extensions/extension-host.test.ts`（修改）；`context-service.test.ts`、`server-extension-sync.test.ts`、`a2a-service.test.ts`、`cloud-file-system.test.ts`（`WorkspaceState` 字段调整）

**接口：**
- 消费：任务 4、5、6。
- 产出（`ExtensionHost`）：`readonly tabs: WorkspaceTabsStore`；`readonly sourceDocuments: SourceDocuments`；`acquireView(instanceKey: string, viewId: string): () => void`；`reopenView(instanceKey: string, params: unknown): void`；`reloadView(instanceKey: string): Promise<void>`；`frameIdOf(instanceKey: string): string | null`；`setFileViewerFile(instanceKey: string, viewId: string, file: FileViewerFile): void`；`reloadFileViewerDocument(instanceKey: string): void`；`selectFileHeaderItem(instanceKey: string, itemId: string, entryId: string | null): void`；`setSourceUnsaved(tabId: string, fileName: string | null): void`；`confirmDiscardUnsaved(scope?: { tabIds: readonly string[] } | { instanceKeys: readonly string[] }): Promise<boolean>`；`confirmDiscardScope(scope: TabGroupScope): Promise<boolean>`；`releaseScope(scope: TabGroupScope): void`。快照字段：`instanceErrors`、`unsavedInstanceKeys`、`unsavedSourceTabIds`，`saveStatuses` 与 `fileHeaderItems` 以实例键为键。私有 `openFile(extensionId, uri, options: { preview?: boolean })`。【删除】`retainFileViewers`、`setFileOpener`、`frameIdOfView`、`unsavedViewIds`、`viewErrors`。
- 产出（`source-documents.ts`）：`export class SourceDocuments` 的 `get(tabId): SourceDocument | null`、`open(tabId: string, file: FileViewerFile, services: SourceDocumentServices): Promise<SourceDocument>`、`release(tabId: string, keepUnsaved: boolean): void`、`hasUnsaved(tabId): boolean`、`dispose(): void`。
- 产出（`WorkspaceState`）：`activeMainViewId: string | null`。

- [ ] **步骤 1：修改与新增测试**

现有用例改用实例键（辅助函数 `showLoaded(viewId, file)` 改为 `showLoaded(instanceKey, viewId, file)`，删除 `host.retainFileViewers` 调用，`pageOf` 改用 `host.frameIdOf`）。测试开始前 `await host.tabs.setScope({ serverId: "local", projectId: PROJECT })`。新增：

- `one file viewer has a frame per file tab`：标签 `t1`、`t2` 用同一个 `VIEWER` 各自 `acquireView(fileViewerInstanceKey(tN, VIEWER), VIEWER)`；两个 `frameIdOf` 不同，两个页面 `view.onLoad` 的参数分别是 `{ file: 各自的文件 }`。
- `unsaved changes are kept per view instance and pin the tab`：`host.tabs.openFile(..., { preview: true })` 后为该标签显示查看器，页面报告未保存；`getSnapshot().unsavedInstanceKeys` 只含该实例键，该标签的 `preview` 为 false。
- `release keeps unsaved frames and Source documents and closes the rest`：两个文件标签，一个有未保存修改；`await host.tabs.setScope(B)` 后 `main.destroyed` 只含没有修改的标签的 frame；`host.setSourceUnsaved(tabId, "a.md")` 的标签在 `sourceDocuments` 中仍可取得。
- `confirmDiscardUnsaved with tab ids lists only those tabs`：确认窗口只列出给定标签的文件。
- `confirmDiscardScope lists a left Project's kept changes and releaseScope closes them`。
- `an extension tab's frame loads with the tab's params and a reopen sends onShow`：`host.layout.openMainView(MAIN_VIEW, { a: 1 })` 后对该标签 `acquireView(tabInstanceKey(tabId), MAIN_VIEW)`，页面 `view.onLoad` 参数为 `{ a: 1 }`；再次 `openMainView(MAIN_VIEW, { a: 2 })`，页面收到参数为 `{ a: 2 }` 的 `view.onShow`。
- `workspace.openFile waits for the group and opens a file tab without asking`：另一个文件标签有未保存修改时，context 的当前 Project 已切换为 B、`host.tabs.setScope(B)` 还在读取时，页面调用 `workspace.openFile(<B 中文件的 uri>)`；调用完成后 B 的标签组有该文件的固定标签并处于激活状态，确认窗口没有出现。
- `context.workspace reports the active tab's view id and null for an empty group`。

- [ ] **步骤 2：运行测试，确认失败**

运行：`pnpm --filter @agw/desktop exec tsx --test renderer/src/runtime/extensions/extension-host.test.ts`
预期：FAIL。

- [ ] **步骤 3：实现**

按设计 8.2、8.3、8.7、10.2 节与第 12 节。构造函数先创建 `tabs`（`resources` 为本实例的私有实现；`discardDrafts` 调用任务 6 导出的 `conversationComposerStorage.remove` 与 `conversationImageDrafts.remove`；`reportError` 调用 `console.error` 与 sonner 的 `toast.error(`${message}: ${error instanceof Error ? error.message : String(error)}`)`），再创建 `layout`；`tabs.subscribe` 与 `layout.subscribe` 都调用 `context.setWorkspace(this.workspaceState())`。`HostFrame` 增加 `instanceKey`、`tabId`；创建 frame 或 Source 文档时记录 `tabScopes.set(tabId, 当前 tabs 范围)`。`setUnsaved(frameId, true)` 与 `setSourceUnsaved(tabId, 非 null)` 在标签属于当前标签组时调用 `tabs.pin(tabId)`。`attachWorkspace` 释放时另外调用 `sourceDocuments.dispose()`。

- [ ] **步骤 4：运行测试，确认通过**

运行：同步骤 2；再运行 `pnpm --filter @agw/desktop exec tsx --test renderer/src/runtime/extensions/context-service.test.ts renderer/src/runtime/extensions/server-extension-sync.test.ts renderer/src/runtime/extensions/a2a-service.test.ts renderer/src/runtime/extensions/cloud-file-system.test.ts`。预期：全部 PASS。

---

### 任务 8：Extension API 选项、参数校验与对话输入框

**文件：**
- 修改：`src/clients/packages/extension-sdk/src/api.ts`、`src/clients/packages/extension-sdk/src/sdk/api.ts`
- 修改：`src/clients/desktop/renderer/src/runtime/extensions/extension-bridge-broker.ts`、`extension-host.ts`
- 测试：`src/clients/packages/extension-sdk/src/sdk/api.test.ts`、`src/clients/desktop/renderer/src/runtime/extensions/extension-bridge.test.ts`（修改）

**接口：**
- 消费：任务 5 的 `openMainView`，任务 7 的 `openFile`。
- 产出（SDK）：设计 10.1 节的 `OpenMainViewOptions`、`OpenFileOptions`、`WorkspaceApi.openMainView(id, params?, options?)`、`WorkspaceApi.openFile(uri, options?)`、`WorkspaceState.activeMainViewId: string | null`。RPC 参数：`workspace.openMainView` 为 `{ id, params, options }`，`workspace.openFile` 为 `{ uri, options }`，`options` 缺省为 `{}`。
- 产出（`ExtensionHost`）：`registerComposer(composer: { insertText(text: string): void }): () => void`；【删除】`setComposer`。
- 产出（broker 模块内）：`isJsonValue(value: unknown): value is JsonValue`，拒绝 `undefined`、函数、symbol、bigint、非有限数字、原型既不是 `Object.prototype` 也不是 null 的对象（`Date`、`Map` 等）、循环引用。

- [ ] **步骤 1：写测试**

`sdk/api.test.ts`：`openMainView("file", { a: 1 }, { key: "k", preview: true })` 发出的请求参数为 `{ id: "file", params: { a: 1 }, options: { key: "k", preview: true } }`；`openFile(uri)` 的请求参数为 `{ uri, options: {} }`。

`extension-bridge.test.ts` 新增：
- `openMainView accepts key, title and preview for extension views`：标签组中出现带该 `key` 与 `title` 的预览标签。
- `openMainView rejects invalid options and params with ERR_INVALID_ARGUMENT`：`options` 含未知字段、`key` 为 `""` 或 257 个字符、`title` 为 `""` 或 101 个字符、`preview` 为 `"yes"`、`params` 为 `new Date()`、`new Map()`、`{ n: Number.NaN }`、循环对象、65535 个 `a` 组成的字符串（序列化为 65537 字节）时都以 `{ code: "ERR_INVALID_ARGUMENT" }` 拒绝；65534 个 `a` 的字符串被接受。
- `openMainView of agw.chat fails with ERR_UNSUPPORTED_OPERATION and built-ins reject options`。
- `chat.composer.insertText writes to the registered composer and fails with ERR_UNAVAILABLE without one`：注册后插入的文字被收到；取消注册后以 `{ code: "ERR_UNAVAILABLE", message: "No chat composer is visible." }` 拒绝；先后注册两个时后注册的收到文字，取消后注册的那个后由先注册的收到。

- [ ] **步骤 2：运行测试，确认失败**

运行：`pnpm --filter @agw/extension-sdk exec tsx --test src/sdk/api.test.ts` 与 `pnpm --filter @agw/desktop exec tsx --test renderer/src/runtime/extensions/extension-bridge.test.ts`
预期：新增用例 FAIL。

- [ ] **步骤 3：实现**

broker 的 `workspace.openMainView` 先校验 `options`（只允许 `key`、`title`、`preview`）与 `params`（`isJsonValue` 且 `new TextEncoder().encode(JSON.stringify(params)).byteLength <= 65536`），再调用 `layout.openMainView(resolveViewId(...), params, options)`；`workspace.openFile` 校验 `options` 后传给 `openFile`。`registerComposer` 用数组保存登记顺序，`insertText` 使用最后一个。

- [ ] **步骤 4：运行测试与检查**

运行：同步骤 2，预期全部 PASS；`pnpm --filter @agw/extension-sdk typecheck` 与 `lint` 通过。

---

### 任务 9：Chat 的草稿 id 与提交回调

**文件：**
- 修改：`src/clients/packages/chat/src/ui-web/components/message/chat.tsx`
- 测试：`src/clients/packages/chat/src/ui-web/components/message/chat-draft-conversation.test.ts`（新增）

**接口：**
- 消费：任务 6。
- 产出（`ChatProps` 新增）：`draftId?: string`（`persistInputDraft` 为 true 时必须提供，缺少时渲染抛出 `Error("persistInputDraft requires draftId.")`）；`newConversationId?: string | null`；`onSubmitted?: () => void`。

- [ ] **步骤 1：写失败的测试**

`setupDomEnvironment` 渲染 `Chat`（写法同 `chat-composer-lifecycle.test.ts`），执行连接使用 `replaceExecutionHub(t, { provider: "InProcess" })` 与 `configureExecutionRuntime({ baseUrl: "https://agw.test", token: null, attachmentStore: null })`，测试结束时恢复原配置并 `executionSessionManager.discard(key)`。`projectId` 为固定值，`target` 为一个 Agent。用例：
- `a draft's first send uses newConversationId`：`conversationId: null`、`newConversationId: "0196f3a4-1c2d-7e8f-9a0b-0000000000a1"`；输入并点击 `Send message`；测试 hub 记录的命令中的会话 `conversationId` 等于 `newConversationId`；`onSubmitted` 被调用一次。
- `the input draft of a draft is stored under its draftId`：`persistInputDraft`、`draftId: "d1"`，输入文字后 `conversationComposerStorage.get(scope, { draftId: "d1" }).input` 为输入内容，`{ draftId: "d2" }` 为 `{}`。
- `a remounted draft attaches to its running session and reports acceptance`：发送后卸载，以相同属性重新挂载；hub 连接数量仍为 1；通过该连接推送 turn-start 的 `ReceiveMessage` 后，重新挂载的 `Chat` 调用 `onConversationAccepted(newConversationId)`。
- `persistInputDraft without draftId throws`。

- [ ] **步骤 2：运行测试，确认失败**

运行：`pnpm --filter @agw/chat exec tsx --test src/ui-web/components/message/chat-draft-conversation.test.ts`
预期：FAIL。

- [ ] **步骤 3：实现**

`ensureConversationId` 在 `newConversationId` 不为 null 时使用它；`draftConversationId` 的初始值：`conversationId` 为 null、`newConversationId` 不为 null 且 `executionSessionManager.has({ serverId, projectId, conversationId: newConversationId })` 时为 `newConversationId`，否则为 null。`handleExecute` 中提交返回 `started`、`queued` 或 `steering` 后调用 `onSubmitted`。输入草稿与图片草稿的键：`conversationId` 不为 null 时 `{ conversationId }`，否则 `{ draftId }`。

- [ ] **步骤 4：运行测试，确认通过**

运行：同步骤 2，预期 4 个测试 PASS；`pnpm --filter @agw/chat test` 全部 PASS。

---

### 任务 10：ChatProjectProvider 与 FileDocumentProvider

**文件：**
- 新增：`src/clients/packages/chat/src/ui-web/pages/chat/chat-project-provider.tsx`、`file-document-provider.tsx`
- 测试：`src/clients/packages/chat/src/ui-web/pages/chat/chat-project-provider.test.ts`、`file-document-provider.test.ts`（新增）

**接口：**
- 产出：`ChatProjectProvider({ routeBasePath, children })` 与 `useChatProject(): ChatProjectContextValue`。字段：设计 7.1 节列出的项目级状态，另含 `notifyFileWritten(directoryId: string | null, path: string): void`、`notifyFileReset(path: string): void`、`subscribeFileChanges(listener: (change: { kind: "written" | "reset"; directoryId: string | null; path: string }) => void): () => void`、`reportReconnectState(sessionId: string, state: ExecutionReconnectState | null, retry: () => Promise<void>): void`、`isReconnecting: boolean`。
- 产出：`FileDocumentProvider`（设计 7.1 节的属性）与 `useFileDocument(): FileDocumentContextValue`（`directory: ProjectDirectoryOption | undefined`、`path`、`diffScope`、`fileContent`、`isLoadingContent`、`contentError`、`diffContentData`、`diffFileView`、`setDiffFileView`、`reload(): void`）。

- [ ] **步骤 1：写失败的测试**

加载方式同 `chat-workspace-hydration.test.ts`（`apiGet`、`readFile`、`getFileDiff` 由测试内实现返回数据）。
- `chat-project-provider.test.ts`：`switching Projects keeps each Project's directory and pending comments`（A 选目录 d2 并加一条评论，切到 B 再切回 A，`selectedDirectory.id` 为 d2、评论仍在）；`notifyFileWritten bumps the tree signal and reaches subscribers`；`the reconnect dialog shows the latest non-null report`。
- `file-document-provider.test.ts`：`the file document reads diff or content by onlyDiff and diffFileView`；`a write notification for the same path reads again and other paths do not`；`a directory outside the Project shows the directory error`（`contentError` 等于 `The directory is no longer part of this Project.`）。

- [ ] **步骤 2：运行测试，确认失败**

运行：`pnpm --filter @agw/chat exec tsx --test src/ui-web/pages/chat/chat-project-provider.test.ts src/ui-web/pages/chat/file-document-provider.test.ts`
预期：FAIL，找不到模块。

- [ ] **步骤 3：实现**

从 `chat-workspace.tsx` 复制设计 7.1 节列出的逻辑到两个 Provider（`chat-workspace.tsx` 在任务 13 改为使用它们并删除原代码）；`selectedDirectoryId` 与代码评论用 `Map<projectId, …>` 保存；没有 `confirmFileChange`。

- [ ] **步骤 4：运行测试，确认通过**

运行：同步骤 2。预期：6 个测试 PASS。

---

### 任务 11：ChatSessionProvider

**文件：**
- 新增：`src/clients/packages/chat/src/ui-web/pages/chat/chat-session-provider.tsx`
- 测试：`src/clients/packages/chat/src/ui-web/pages/chat/chat-session-provider.test.ts`（新增）

**接口：**
- 消费：任务 6、9、10。
- 产出：设计 7.1 节的 `ChatSessionProviderProps`；`useChatSession(): ChatSessionContextValue`，字段：`conversationId`、`draftId`、`newConversationId`、`selectedTarget`、`selectAgent`、`chatSessionSeed`、`isLoadingConversation`、`restoreExecution`、`composerRef`、`currentConversationTurnId`、`loadError: string | null`、`retry(): void`、`handleConversationAccepted(id: string): void`、`handleConversationChange(options?: ConversationChangeOptions): void`、`handleReconnectStateChange(state, retry): void`、`onSubmitted: (() => void) | undefined`。

- [ ] **步骤 1：写失败的测试**

加载方式同 `chat-workspace-hydration.test.ts`，`chatHttpClient.getConversation`、`getConversationMessages` 由测试内实现返回数据或抛出 `status` 为 404 的 `ApiTransportError`；执行连接同任务 9。用例：
- `mounting a conversation reads its history and reports the title`。
- `an accepted draft keeps its session when conversationId becomes the same id`：先 `conversationId: null`，触发受理后以同一 id 重新渲染；`getConversation` 没有被调用，`chatSessionSeed.revision` 不变；输入草稿从 `{ draftId }` 移到 `{ conversationId }`。
- `a failed read shows loadError and retry reads again`。
- `a submitted draft with a known status is accepted at once`：先向 `executionSessionManager.conversationStatuses` 写入 `(scope, draftId)` 的 `running`，挂载后 `onConversationAccepted(draftId)` 被调用一次。
- `a submitted draft with a running session is accepted when its status appears`。
- `a submitted draft found on the Server after a restart is accepted`：没有执行会话，`getConversation` 返回详情；回调被调用并读取历史。
- `a submitted draft missing on the Server is reported lost`：`getConversation` 抛出 404；`onDraftLost` 被调用，`onConversationAccepted` 没有被调用。
- `only a visible session reports its reconnect state`。

- [ ] **步骤 2：运行测试，确认失败**

运行：`pnpm --filter @agw/chat exec tsx --test src/ui-web/pages/chat/chat-session-provider.test.ts`
预期：FAIL，找不到模块。

- [ ] **步骤 3：实现**

从 `chat-workspace.tsx` 复制设计 7.1 节列出的会话逻辑；找回受理按设计 7.3 节的三步顺序，订阅 `conversationStatuses` 的方式与现有 `useConversationStatuses` 相同；`acceptedConversationId` 用 ref 保存，同一 id 只受理一次；`visible` 为 false 时向 `reportReconnectState` 报告 null。

- [ ] **步骤 4：运行测试，确认通过**

运行：同步骤 2。预期：8 个测试 PASS。

---

### 任务 12：对话列表与 Explorer 的单击、双击和选中

**文件：**
- 修改：`src/clients/packages/projects/src/ui-web/components/task/conversation-list.tsx`
- 修改：`src/clients/packages/projects/src/ui-web/components/file-explorer/explorer.tsx`、`explorer-file-tree.tsx`
- 修改：`src/clients/packages/chat/src/ui-web/pages/chat/chat-workspace.tsx`（现有侧栏对新回调签名的调用）
- 测试：`src/clients/packages/projects/src/ui-web/components/task/conversation-list.test.ts`、`file-explorer/explorer-file-tree.test.ts`（修改）

**接口：**
- 产出：`ConversationList` 的 `onConversationSelect(conversation: ConversationSummary, options: { pinned: boolean }): void` 与新属性 `onConversationsLoaded?: (items: readonly ConversationSummary[]) => void`；`Explorer` 的 `onFileSelected(path: string, scope: GitDiffScope | undefined, options: { pinned: boolean }): void` 与新属性 `selectedFilePath?: string | null`；`ExplorerFileTree` 的 `onFileSelect(path, scope, options)` 与 `selectedPath?: string | null`。

- [ ] **步骤 1：写测试**

- `conversation-list.test.ts`：`a click selects with pinned false and a double click with pinned true`（双击的事件序列产生的调用中最后一次的 `pinned` 为 true）；`loaded items are reported`。
- `explorer-file-tree.test.ts`：`a double click on a file selects it pinned`；`selectedPath highlights the file and expands its folders`（折叠状态的 `docs/sub/a.md`，设置 `selectedPath` 后上级目录展开，条目带 `aria-selected="true"`，在 `Element.prototype` 上定义的 `scrollIntoView` 记录到一次以该条目为对象的调用）；`a click on a file only highlights it and keeps the expansion`。

- [ ] **步骤 2：运行测试，确认失败**

运行：`pnpm --filter @agw/projects exec tsx --test src/ui-web/components/task/conversation-list.test.ts src/ui-web/components/file-explorer/explorer-file-tree.test.ts`
预期：新增用例 FAIL。

- [ ] **步骤 3：实现**

条目 `onClick` 传 `{ pinned: false }`，`onDoubleClick` 传 `{ pinned: true }`；选中项使用 `bg-accent`；`selectedPath` 变化时按设计第 13 节读取并展开上级目录，然后 `scrollIntoView({ block: "nearest" })`；`ConversationList` 每次读取列表成功后调用 `onConversationsLoaded`。`chat-workspace.tsx` 的现有回调接收新参数后忽略 `options`。

- [ ] **步骤 4：运行测试与检查**

运行：同步骤 2，预期 PASS；`pnpm --filter @agw/projects test`、`typecheck`、`lint` 通过；`pnpm --filter @agw/chat typecheck` 通过。

---

### 任务 13：Web 组合与受控侧栏

**文件：**
- 修改：`src/clients/packages/chat/src/ui-web/pages/chat/chat-workspace.tsx`
- 修改：`src/clients/packages/chat/src/index.ts`
- 测试：`src/clients/packages/chat/src/ui-web/pages/chat/chat-workspace-hydration.test.ts`（修改）

**接口：**
- 消费：任务 10、11、12。
- 产出：受控的 `ChatConversationSidebarProps` 与 `ChatFilesSidebarProps`（设计 7.1 节，含 `onConversationsLoaded`）；`ChatConversationPane` 读取 `useChatProject()` 与 `useChatSession()`；`ChatFilePane` 读取 `useChatProject()` 与 `useFileDocument()`；`index.ts` 导出 `ChatProjectProvider`、`useChatProject`、`ChatSessionProvider`、`useChatSession`、`FileDocumentProvider`、`useFileDocument` 及其属性类型。【删除】`ChatWorkspaceProvider`、`useChatWorkspace`、`ChatWorkspaceProviderProps`、`ChatWorkspaceContextValue` 及其导出，删除 `chat-workspace.tsx` 中已移到三个 Provider 的代码。

- [ ] **步骤 1：修改测试**

`chat-workspace-hydration.test.ts` 的现有用例改为渲染 `ChatWorkspace`（Web 组合），通过界面与 `useChatProject`、`useChatSession`、`useFileDocument` 读取状态；用例数量与断言内容不变（URL 同步、打开对话、新对话、文件选择、重连对话框）。

- [ ] **步骤 2：运行测试，确认失败**

运行：`pnpm --filter @agw/chat exec tsx --test src/ui-web/pages/chat/chat-workspace-hydration.test.ts`
预期：FAIL。

- [ ] **步骤 3：实现**

Web 的 `ChatWorkspace` 内部保存单个对话选择与单个文件选择，渲染 `ChatSessionProvider`（`draftId: "web"`、`newConversationId: null`、`draftSubmitted: false`、`visible: true`，受理时不重新挂载）与选中文件时的 `FileDocumentProvider`（`readSourceContent: true`）；保留现有 URL 同步；侧栏回调忽略 `pinned`。

- [ ] **步骤 4：运行测试与检查**

运行：同步骤 2，预期 PASS；`pnpm --filter @agw/chat test`、`typecheck`、`lint` 通过；`pnpm --filter @agw/web typecheck` 通过。

---

### 任务 14：WorkspaceTabBar

**文件：**
- 新增：`src/clients/desktop/renderer/src/runtime/components/workspace/workspace-tab-bar.tsx`
- 修改：`src/clients/packages/components/src/ui-tokens/tokens.css`（`.agw-workspace-tabs`、`.agw-workspace-tab`、`.is-active`、`.is-preview`、拖动插入线）
- 测试：`src/clients/desktop/renderer/src/runtime/components/workspace/workspace-tab-bar.test.ts`（新增）

**接口：**
- 消费：任务 3。
- 产出：

```ts
export type WorkspaceTabView = {
  tab: WorkspaceTab;
  title: string;
  /** 同名文件的区分路径或目录名。The distinguishing path or directory name of same-named files. */
  description: string | null;
  tooltip: string;
  icon: WorkspaceIcon;
  status: ConversationStatus | null;
  unsaved: boolean;
};
export type TabDescribeContext = {
  directories: readonly ProjectDirectoryOption[];
  conversationStatuses: ReadonlyMap<string, ConversationStatus>;
  unsavedTabIds: ReadonlySet<string>;
  view(viewId: string): WorkspaceView | undefined;
};
export function describeTabs(group: TabGroup, context: TabDescribeContext): WorkspaceTabView[];
export function WorkspaceTabBar(props: {
  tabs: readonly WorkspaceTabView[];
  activeTabId: string | null;
  onActivate(tabId: string): void;
  onPin(tabId: string): void;
  onClose(tabIds: readonly string[]): void;
  onMove(tabId: string, index: number): void;
}): React.ReactElement;
```

- [ ] **步骤 1：写失败的测试**

`describeTabs`：
- `titles follow the tab kind`：草稿为 `New conversation`，`title` 为 null 的对话为 `Conversation`，文件为文件名，Jobs 为 `Jobs`，extension 为 `title` 或视图标题；视图不存在时为保存的标题与 `{ kind: "lucide", name: "Puzzle" }`。
- `same-named files show the shortest distinguishing parent path`：`src/a/index.ts` 与 `src/b/index.ts` 的 `description` 为 `src/a`、`src/b`；`x/src/a/index.ts` 与 `y/src/a/index.ts` 为 `x/src/a`、`y/src/a`；路径相同、目录不同时为目录名；没有同名文件时为 null。
- `tooltips name the directory only in a multi-directory Project`。
- `running and waiting conversations carry their status and unsaved files are marked`。

`WorkspaceTabBar`（`setupDomEnvironment` 渲染）：
- `tabs carry tab roles, selection, labels and preview and unsaved descriptions`：`role="tablist"` 的 `aria-label` 为 `Open tabs`；激活标签 `aria-selected="true"`；预览标签带 `is-preview` 类与 `aria-description="Preview"`；未保存标签带 `aria-description="Unsaved changes"`；关闭按钮 `aria-label="Close a.md"`。
- `click activates, double click pins, middle click and the close button close`。
- `the context menu closes others, to the right and all, and keeps a preview open`：Close Others 调用 `onClose` 的参数为其余全部 id，Close to the Right 为右侧全部 id，Close All 为全部 id；Keep Open 只在预览标签的菜单中出现并调用 `onPin`。
- `arrow keys, Home and End move the focus and Delete closes`。
- `dropping a tab before another calls onMove with the target index`（派发 `dragstart`、`dragover`、`drop`）。

- [ ] **步骤 2：运行测试，确认失败**

运行：`pnpm --filter @agw/desktop exec tsx --test renderer/src/runtime/components/workspace/workspace-tab-bar.test.ts`
预期：FAIL。

- [ ] **步骤 3：实现**

按设计第 5 节。右键菜单用 `@agw/components` 的 `ContextMenu`；状态标记使用标题栏 `StatusDot` 的类名 `agw-status-dot is-<status>`；滚轮转换与 `app-shell.tsx` 中 Project 标签的处理相同；激活标签变化后 `scrollIntoView({ block: "nearest", inline: "nearest" })`。

- [ ] **步骤 4：运行测试，确认通过**

运行：同步骤 2，预期全部 PASS；`pnpm --filter @agw/components lint` 通过。

---

### 任务 15：Desktop 主区组装

**文件（相对 `src/clients/desktop/renderer/src/runtime`）：**
- 新增：`components/workspace/chat-tab-panel.tsx`、`file-tab-panel.tsx`、`desktop-floating-chat.tsx`、`empty-workspace.tsx`
- 修改：`components/workspace/main-workspace.tsx`、`desktop-workspace.tsx`、`left-sidebar.tsx`、`jobs-workspace.tsx`、`extension-view.tsx`
- 修改：`source/use-editable-source.ts`
- 测试：`components/workspace/extension-view.test.ts`、`jobs-workspace.test.ts`（修改）

**接口：**
- 消费：任务 4～14。
- 产出：`ChatTabPanel({ host, tab, active, fullWorkspaceViewId, composerActions, messageActions, onSelectionChange })`；`FileTabPanel({ host, layout, snapshot, tab, active, fileViewerDefaults, onSetDefaultFileViewer })`；`DesktopFloatingChat({ host, snapshot, floatingChat, composerActions, messageActions, onSelectionChange })`；`EmptyWorkspace({ onNewConversation })`；`ExtensionView` 新增必填属性 `instanceKey: string`；`useEditableSource(host: ExtensionHost, tabId: string, file: FileViewerFile | null): EditableSource`。

- [ ] **步骤 1：修改组件测试**

`extension-view.test.ts`：现有用例传入 `instanceKey`；新增 `an unregistered view shows the unavailable panel with Close tab` 与 `the panel turns into the view once it registers`。`jobs-workspace.test.ts`：删除 `onOpenChat` 相关断言；日志中的对话链接 `href` 为 `/desktop/chat?projectId=…&conversationId=…`。

- [ ] **步骤 2：运行测试，确认失败**

运行：`pnpm --filter @agw/desktop exec tsx --test renderer/src/runtime/components/workspace/extension-view.test.ts renderer/src/runtime/components/workspace/jobs-workspace.test.ts`
预期：新增用例 FAIL。

- [ ] **步骤 3：实现**

- `DesktopWorkspace`：`ChatProjectProvider routeBasePath="/desktop/chat"` 替换 `ChatWorkspaceProvider`；`selectedProjectId` 确定后 `host.tabs.setScope({ serverId, projectId })`，卸载时 `setScope(null)`；Project 列表加载后 `desktopBridge.pruneWorkspaceTabs(serverId, ids)`；订阅 `desktopBridge.onWorkspaceCommand`（设计 5.4 节）；URL 的 `conversationId` 按设计第 14 节处理（`await host.tabs.whenLoaded()` 后打开）；对话列表与 Files 的回调按设计 7.3、8.4、第 13 节；`onConversationsLoaded` 按设计 7.6 节更新标题；可见会话的 `onSelectionChange` 写入组件状态，现有写 `host.context.setSelection` 的 effect 改用它；`host.setFileWriteListener` 改为调用 `notifyFileWritten`；删除 `fileOpener`、`retainFileViewers` 与按当前文件计算查看器的代码。
- `MainWorkspace`：外层一列，`WorkspaceTabBar`（全屏视图时不渲染）在 `.agw-main-workspace` 之前；`.agw-main-workspace` 设置 `data-view-id`（全屏视图 id、激活标签的视图 id 或 `agw.empty`）与 `data-tab-id`；已挂载集合（设计第 12 节）中的标签按类型渲染 `ChatTabPanel`、`FileTabPanel`、`JobsWorkspace`、`ExtensionView instanceKey={tabInstanceKey(tab.id)}`，未激活的加 `hidden`，带 `role="tabpanel"` 与 `aria-labelledby`；空标签组渲染 `EmptyWorkspace`；快照 `error` 不为 null 时抛出它；`DesktopFloatingChat` 在标签组读取完成后渲染。
- `ChatTabPanel`、`FileTabPanel`、`DesktopFloatingChat` 按设计 7.2、8.1 节与第 11 节；`ChatSessionProvider` 的 `visible` 为 true 时调用 `host.registerComposer`。
- `useEditableSource` 按设计 8.2 节，`setUnsaved` 调用 `host.setSourceUnsaved(tabId, fileName)`。

- [ ] **步骤 4：运行测试与检查**

运行：同步骤 2，预期 PASS；`pnpm --filter @agw/desktop typecheck`、`pnpm --filter @agw/desktop lint`、`pnpm --filter @agw/desktop test` 全部通过。

- [ ] **步骤 5：启动 Desktop 检查主要流程**

运行：仓库根目录 `dotnet build src/server/Agw.Standalone.Host`，然后在 `src` 运行 `pnpm dev:desktop`。检查：在 Files 树中单击两个文件只有一个预览标签，双击后固定；New conversation 新开标签，发送第一条消息后标签标题变为对话标题；Events 收起标签栏与左侧栏，点击 Files 回到标签组；切换 Project 后切回恢复；焦点在 Markdown 页面中时 Mod+W 关闭标签；关闭有未保存修改的标签时出现确认。发现的问题在本任务内修复，并为它补充对应任务的测试。

---

### 任务 16：标题栏的 Project 切换与关闭

**文件：**
- 修改：`src/clients/desktop/renderer/src/runtime/components/app-shell.tsx`
- 测试：`src/clients/desktop/renderer/src/runtime/components/app-shell-routing.test.ts`（修改）

**接口：**
- 消费：任务 7 的 `confirmDiscardScope`、`releaseScope`。

- [ ] **步骤 1：修改测试**

`app-shell-routing.test.ts` 中经过 `buildSettingsHref` 得到的 `returnTo` 断言改为只含 `projectId`。

- [ ] **步骤 2：实现**

按设计第 12 节：Project 选择器、Project 标签链接与 Background conversations 菜单不再经过 `afterUnsavedConfirmed`；`confirmTitlebarLink` 对 Project 标签链接不确认；关闭 Project 标签先 `await host.confirmDiscardScope({ serverId, projectId })`，确认后 `host.releaseScope(…)`，再执行现有的 `closeTab`；`chatReturnHref` 不含 `conversationId`。

- [ ] **步骤 3：运行测试与检查**

运行：`pnpm --filter @agw/desktop exec tsx --test renderer/src/runtime/components/app-shell-routing.test.ts`，预期 PASS；`pnpm --filter @agw/desktop typecheck` 与 `lint` 通过。

---

### 任务 17：Bare Repository 以标签打开文件

**文件：**
- 修改：`src/extensions/bare-repository/src/sidebar.tsx`

**接口：**
- 消费：任务 8 的 `OpenMainViewOptions`、`OpenFileOptions`。

- [ ] **步骤 1：实现**

按设计 10.5 节：`open(path: string, pinned: boolean)`；树组件的 `onOpen` 回调签名改为 `(path: string, pinned: boolean) => void`，条目 `onClick` 传 false、`onDoubleClick` 传 true；`key` 为 `` `${repository}/${branch}/${path}` ``，`title` 为 `path` 最后一个 `/` 之后的部分。

- [ ] **步骤 2：检查**

运行：`pnpm --filter @agw/extension-bare-repository typecheck` 与 `lint`。预期：通过。行为由任务 18 的集成测试验证。

---

### 任务 18：Desktop extension 集成测试

**文件：**
- 修改：`src/clients/desktop/src/main/extensions.integration.ts`
- 修改：`src/clients/desktop/scripts/test-extensions.mjs`（需要时加入 fixture 文件）

- [ ] **步骤 1：更新依赖单一主区视图的现有步骤**

用 `.agw-main-workspace` 的 `data-tab-id` 与 `data-view-id` 定位激活内容；打开对话、文件后等待对应标签 `aria-selected="true"`；文件标题栏的 `[role="tablist"]` 选择器保持在 `.agw-main-workspace` 内，主区标签栏用 `[aria-label="Open tabs"]`。

- [ ] **步骤 2：新增 `workspaceTabs` 与 `workspaceTabsRestore` 步骤**

按设计第 17 节集成测试的说明逐项断言。

- [ ] **步骤 3：缩短版运行**

把 `run()` 临时缩短为只执行启动、`workspaceTabs`、`workspaceTabsRestore`；运行前请求用户确认，说明这次要回答的问题：“新增的两个步骤在真实 Electron 中是否通过”。运行：`pnpm --filter @agw/desktop test:extensions`。预期：PASS。之后恢复 `run()`。

- [ ] **步骤 4：完整运行**

请求用户确认，说明这次要回答的问题：“修改后的现有步骤与新增步骤是否全部通过”。运行同步骤 3。预期：全部 PASS。

---

### 任务 19：文档与全量检查

**文件：**
- 修改：`src/clients/desktop/README.md`、`docs/2.Architecture.md`、`src/clients/packages/extension-sdk/README.md`

- [ ] **步骤 1：更新文档**

按设计第 18 节。`docs/human/` 与 `docs/designs/` 中已提交的文件不修改。

- [ ] **步骤 2：格式化**

运行：对 `@agw/desktop`、`@agw/chat`、`@agw/projects`、`@agw/extension-sdk`、`@agw/components`、`@agw/extension-bare-repository` 分别执行 `pnpm --filter <包名> format`。

- [ ] **步骤 3：全量检查**

运行（`src` 目录）：`pnpm test`（含 `test:boundaries`、各包 typecheck 与 test）、`pnpm lint`、`pnpm fmt:check`。预期：全部通过。

- [ ] **步骤 4：Web e2e**

运行：仓库根目录 `dotnet build src/server/Agw.Standalone.Host`；在 `src/clients` 运行 `pnpm --filter @agw/web exec playwright install chromium` 与 `pnpm --filter @agw/web test:e2e`。预期：全部 PASS，e2e 测试文件没有修改。
