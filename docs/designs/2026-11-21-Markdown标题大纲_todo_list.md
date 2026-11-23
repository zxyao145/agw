# Markdown 标题大纲实施计划

> **供 agent 执行：** 必须使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐个任务执行本计划。步骤使用复选框（`- [ ]`）跟踪。

**目标：** Markdown extension 的 Mixed 与 Preview 在内容右侧显示当前文件的 1～4 级标题大纲，点击大纲项跳转到标题，滚动时标出当前项。

**架构：** 标题列表来自文档模型：Preview 在 `renderMarkdown` 中从 Tiptap JSON 收集，Mixed 由 `EditOutline` 在每次文档变化时从 ProseMirror 文档收集。两个查看器共用 `OutlinePanel` 与 `useActiveHeading`，大纲作为 CSS 网格的第三列显示在内容列右侧，滚动位置与当前项从页面 DOM 读取。

**技术栈：** React 19.3（`useSyncExternalStore`、`useLayoutEffect`）、Tiptap 3.32.0 / ProseMirror、github-slugger、node:test + jsdom、Electron 集成测试。

**设计文档：** `docs/designs/2026-11-21-Markdown标题大纲.md`。执行者同时阅读设计文档与本计划；本计划引用的“设计 x.y 节”都指这份文档。

## 全局约束

- 范围：只修改 `src/extensions/markdown`，以及 Desktop 的 `src/clients/desktop/src/main/extensions.integration.ts` 与 `src/clients/desktop/scripts/test-extensions.mjs`。`manifest.json`、Extension API、宿主代码不变；Markdown 原文的解析结果与 Mixed 的写回结果不变。
- 只收集 `level` 为 1～4 的 `heading`；大纲项文字经 `outlineLabel` 处理（连续空白合并为一个空格，去掉首尾空白），处理后为空的标题不收集；Preview 的 `footnotes` 节点整体跳过。
- 界面文字为英文：`nav` 的 `aria-label="Outline"`、标题文字 `Outline`、空状态 `No headings`；当前项带 `aria-current="location"`。
- 数值：`--outline-width: 240px`；`@media (width < 960px)` 时隐藏大纲；当前项阈值 24px；标题 `scroll-margin-top: 16px`；`.outline` 内边距 `32px 16px 72px`；工作区背景下 `.outline` 内边距 `16px 12px 24px`、`max-height: calc(100vh - 80px)`；网格第三列最小宽度 `calc(var(--outline-width) + 16px)`；Mixed 内容列轨道 `calc(830px + 64px)`，工作区背景下 `calc(830px + 64px + 32px)`；Preview 内容列轨道 980px。
- 颜色与圆角只用宿主主题变量：`--muted-foreground`、`--foreground`、`--accent`、`--primary`、`--ring`、`--card`、`--radius-sm`、`--radius-xl`。
- 注释使用中英双语，技术术语保持英文，注释数量与周围代码相同。
- 测试使用真实实现（真实解析管线、Tiptap 编辑器、jsdom、`createEditHost`），禁止 mock 与 fake；出错时直接抛出，不加备用处理。
- 修改文件只用编辑工具，不用 heredoc、`sed`、Python 脚本改代码；中间文件不放在 `/tmp`。
- 不执行 git commit；用户要求时再提交。
- Markdown extension 的检查在仓库的 `src` 目录执行：`pnpm --filter @agw/extension-markdown typecheck`、`lint`、`test`、`format:check`。单个测试文件：`pnpm --filter @agw/extension-markdown exec tsx --test <相对 src/extensions/markdown 的路径>`。
- Desktop 集成测试 `pnpm --filter @agw/desktop test:extensions` 每次运行前都请求用户确认，并写明这次运行要回答的问题。

## 审查重点

1. Mixed 中输入文字：文档有标题时，在 paragraph 中输入文字不产生 React 提交（现有约定），大纲的订阅与 `useActiveHeading` 都不能引起提交。测试：任务 5 的 `typing in a paragraph of a document with headings commits nothing in React`。
2. 代码块中以 `#` 开头的行（shell 注释、缩进代码块）不是大纲项。测试：任务 2 的 `lines starting with # inside code blocks are not outline items`，任务 3 的 `the outline lists level 1-4 headings, nested ones included, each at its heading node`。
3. Mixed 中文字相同的标题：两个 `## Intro` 是两项，各自对应自己的位置，点击第二项跳到第二个标题。测试：任务 3 的 `headings with the same text are separate items at their own positions`。
4. Mixed 刷新：文件在磁盘上变化后再次显示（文档会话重新读取）时，大纲随新内容更新。测试：任务 3 的 `a refresh that reads changed text updates the headings`。
5. Mixed 通过 `unmountEditView` 释放文档并卸载 React。任务 3 的 `dispose stops notifications and a later unsubscribe still works` 检查取消订阅。任务 5 的 `unloading Mixed with an outline destroys the editor without a redraw or errors` 检查带大纲的视图卸载。

## 文件结构

- `src/extensions/markdown/src/shared/outline.ts`【新增】：大纲项类型与纯函数（文字处理、比较、当前项计算），两个查看器共用。
- `src/extensions/markdown/src/shared/outline-panel.tsx`【新增】：`OutlinePanel` 组件与 `useActiveHeading` hook，只依赖 DOM 与 `outline.ts`。
- `src/extensions/markdown/src/shared/outline.css`【新增】：大纲、共用网格与卡片样式；正文列宽和大纲的上沿位置由各查看器设置。
- `src/extensions/markdown/src/editor/edit-outline.ts`【新增】：`EditOutline`，Mixed 的大纲项与标题位置。
- `src/extensions/markdown/src/test-layout.ts`【新增】：测试共用的 `LayoutFreeResizeObserver` 与 `outlineItems`。
- 其余为设计变更清单中的【修改】文件。

---

### 任务 1：共用的大纲规则

**文件：**
- 新增：`src/extensions/markdown/src/shared/outline.ts`
- 测试：`src/extensions/markdown/src/shared/outline.test.ts`（新增）

**接口：**
- 产出：
  - `export type OutlineHeading = { level: 1 | 2 | 3 | 4; label: string }`
  - `export function isOutlineLevel(level: unknown): level is OutlineHeading["level"]`
  - `export function outlineLabel(text: string): string`
  - `export function sameOutline(a: readonly OutlineHeading[], b: readonly OutlineHeading[]): boolean`
  - `export function activeHeadingIndex(tops: readonly number[], viewport: { height: number; atBottom: boolean }): number | null`

- [ ] **步骤 1：写失败的测试**

```ts
import assert from "node:assert/strict";
import test from "node:test";

import { activeHeadingIndex, outlineLabel, sameOutline } from "./outline";

test("outlineLabel collapses runs of whitespace, newlines and tabs and trims", () => {
  assert.equal(outlineLabel("  Install\n\tthe   tool  "), "Install the tool");
  assert.equal(outlineLabel(" \n\t "), "");
});

test("sameOutline compares the level and label of every item", () => {
  const outline = [
    { level: 2, label: "A" },
    { level: 3, label: "B" },
  ] as const;
  assert.equal(sameOutline(outline, [{ level: 2, label: "A" }, { level: 3, label: "B" }]), true);
  assert.equal(sameOutline(outline, [{ level: 2, label: "A" }, { level: 4, label: "B" }]), false);
  assert.equal(sameOutline(outline, [{ level: 2, label: "A" }, { level: 3, label: "C" }]), false);
  assert.equal(sameOutline(outline, [{ level: 2, label: "A" }]), false);
});

test("activeHeadingIndex is null without headings and before the first heading", () => {
  assert.equal(activeHeadingIndex([], { height: 600, atBottom: false }), null);
  assert.equal(activeHeadingIndex([25, 300], { height: 600, atBottom: false }), null);
});

test("activeHeadingIndex returns the last heading at or above 24px", () => {
  assert.equal(activeHeadingIndex([-400, -10, 24, 25], { height: 600, atBottom: false }), 2);
  assert.equal(activeHeadingIndex([-400, 100], { height: 600, atBottom: false }), 0);
});

test("at the bottom activeHeadingIndex returns the last heading whose top is inside the visible part", () => {
  assert.equal(activeHeadingIndex([-400, 300, 599, 600], { height: 600, atBottom: true }), 2);
  assert.equal(activeHeadingIndex([700], { height: 600, atBottom: true }), null);
});
```

- [ ] **步骤 2：运行测试，确认失败**

运行：`pnpm --filter @agw/extension-markdown exec tsx --test src/shared/outline.test.ts`
预期：FAIL，找不到模块 `./outline`。

- [ ] **步骤 3：实现 `src/shared/outline.ts`**

`outlineLabel` 用 `/\s+/gu` 替换为一个空格后 `trim()`。`activeHeadingIndex` 按设计 4.1 节：没有到底部时返回最后一个 `tops[i] <= 24` 的 `i`，到底部时返回最后一个 `tops[i] < viewport.height` 的 `i`，都没有时返回 `null`；24 写成模块常量，双语注释说明它是 16px 的 `scroll-margin-top` 加 8px。四个导出都带设计 1.1、4.1 节中的双语文档注释。

- [ ] **步骤 4：运行测试，确认通过**

运行：同步骤 2。预期：5 个测试 PASS。

---

### 任务 2：Preview 收集大纲项

**文件：**
- 修改：`src/extensions/markdown/src/preview/markdown-pipeline.ts:359-381`
- 修改：`src/extensions/markdown/src/preview/markdown-document.tsx:50-54,73`（只改为取用返回值的 `element`，布局在任务 4）
- 测试：`src/extensions/markdown/src/preview/markdown-pipeline.test.ts`

**接口：**
- 使用：任务 1 的 `OutlineHeading`、`outlineLabel`。
- 产出：
  - `export type PreviewOutlineHeading = OutlineHeading & { id: string }`
  - 模块内部函数 `collectOutline(document: JSONContent): PreviewOutlineHeading[]`
  - `export function renderMarkdown(text: string, components?: MarkdownComponents): { element: ReactElement; outline: PreviewOutlineHeading[] }`

- [ ] **步骤 1：修改现有测试，写新测试**

现有的 `render` 改为 `renderToStaticMarkup(renderMarkdown(markdown).element)`；Mermaid 用例中的 `renderToStaticMarkup(renderMarkdown(markdown, { mermaid: figure }))` 改为取 `.element`。在文件末尾加入：

```ts
function outline(markdown: string) {
  return renderMarkdown(markdown).outline;
}

test("the outline lists the level 1-4 headings in document order", () => {
  assert.deepEqual(outline("# A\n\n## B\n\n### C\n\n#### D\n\n##### E\n\n###### F\n\n## G"), [
    { level: 1, label: "A", id: "a" },
    { level: 2, label: "B", id: "b" },
    { level: 3, label: "C", id: "c" },
    { level: 4, label: "D", id: "d" },
    { level: 2, label: "G", id: "g" },
  ]);
});

test("setext headings and headings in blockquotes and list items are outline items", () => {
  assert.deepEqual(outline("Title\n=====\n\nSub\n---\n\n> ## Quoted\n\n- item\n\n  ### Listed\n"), [
    { level: 1, label: "Title", id: "title" },
    { level: 2, label: "Sub", id: "sub" },
    { level: 2, label: "Quoted", id: "quoted" },
    { level: 3, label: "Listed", id: "listed" },
  ]);
});

test("outline labels keep the text of marks, drop images and footnote references and collapse spaces", () => {
  const labels = outline(
    "## **Bold** *it* ~~old~~ `code` [link](https://example.com) ![img](i.png) note[^n]\n\n" +
      "### Spaced   out\ttext\n\n[^n]: Note.\n",
  ).map(({ label }) => label);
  assert.deepEqual(labels, ["Bold it old code link note", "Spaced out text"]);
});

test("empty headings are not outline items", () => {
  assert.deepEqual(outline("#\n\n## Kept\n\n###   \n"), [{ level: 2, label: "Kept", id: "kept" }]);
});

test("outline ids match the rendered heading ids, with suffixes for repeated headings", () => {
  const { element, outline: items } = renderMarkdown("# Intro\n\n## Intro\n\n### 中文 标题");
  assert.deepEqual(
    items.map(({ id }) => id),
    ["intro", "intro-1", "中文-标题"],
  );
  const html = renderToStaticMarkup(element);
  for (const { id } of items) assert.match(html, new RegExp(`<h\\d id="${id}">`));
});

test("the footnotes section and headings in footnote definitions are not outline items", () => {
  const { element, outline: items } = renderMarkdown(
    "# Doc\n\nText[^a].\n\n[^a]: Note.\n\n    ## Inside note\n",
  );
  // 先确认脚注定义中确实渲染出了标题。First make sure the footnote definition renders a heading.
  assert.match(renderToStaticMarkup(element), /<li id="user-content-fn-a">.*<h2 id="inside-note">/su);
  assert.deepEqual(items, [{ level: 1, label: "Doc", id: "doc" }]);
});

test("lines starting with # inside code blocks are not outline items", () => {
  assert.deepEqual(outline("```sh\n# comment\n```\n\n    # indented code\n\n## Real\n"), [
    { level: 2, label: "Real", id: "real" },
  ]);
});

test("inline HTML in a heading stays in the outline label as written", () => {
  assert.deepEqual(
    outline("## Install <kbd>npm</kbd>").map(({ label }) => label),
    ["Install <kbd>npm</kbd>"],
  );
});
```

- [ ] **步骤 2：运行测试，确认失败**

运行：`pnpm --filter @agw/extension-markdown exec tsx --test src/preview/markdown-pipeline.test.ts`
预期：FAIL；`renderMarkdown` 的返回值还没有 `element` 与 `outline`。

- [ ] **步骤 3：实现**

- `collectOutline`：在 `assignHeadingIds` 之后的文档上递归。`footnotes` 节点整体跳过；`heading` 且 `level <= 4` 时计算文字并且不再进入它的子节点；其他节点进入 `content`。文字规则：`text` 节点取 `text`；没有 `content` 的其他节点（`image`、`footnoteReference`、`hardBreak`）取一个空格；有 `content` 的节点连接子节点的文字；结果经 `outlineLabel`，为空时不收集。`id` 取 `attrs.id`（`assignHeadingIds` 之后一定是字符串）。
- `renderMarkdown`：文档只解析一次，同一个文档交给 `collectOutline` 与 `renderMarkdownJson`，返回 `{ element, outline }`。双语文档注释说明返回值。
- `markdown-document.tsx`：`useMemo` 保存 `renderMarkdown` 的结果，`<article>` 中渲染它的 `element`。

- [ ] **步骤 4：运行测试，确认通过**

运行：`pnpm --filter @agw/extension-markdown exec tsx --test src/preview/markdown-pipeline.test.ts src/preview/markdown-document.test.ts`，再运行 `pnpm --filter @agw/extension-markdown typecheck`。
预期：全部 PASS，typecheck 没有错误。

---

### 任务 3：Mixed 收集大纲项

**文件：**
- 新增：`src/extensions/markdown/src/editor/edit-outline.ts`
- 修改：`src/extensions/markdown/src/editor/edit-document.ts:49-102`
- 测试：`src/extensions/markdown/src/editor/edit-outline.test.ts`（新增）

**接口：**
- 使用：任务 1 的 `OutlineHeading`、`outlineLabel`、`sameOutline`。
- 产出：

```ts
export class EditOutline {
  public constructor(editor: Editor);
  /** 大纲项不变时为同一个数组。The same array while the items are unchanged. */
  public get headings(): readonly OutlineHeading[];
  /** 第 index 项的标题节点在当前文档中的位置；越界时抛出。Throws when out of range. */
  public position(index: number): number;
  /** 箭头函数属性，可以直接交给 useSyncExternalStore（与 SlashMenuStore.subscribe 相同）。 */
  public readonly subscribe: (listener: () => void) => () => void;
  public dispose(): void;
}
// EditDocument 新增：public readonly outline: EditOutline;
```

- [ ] **步骤 1：写失败的测试**

文件开头的 `host`、`opened`、`after`、`open(name, text)` 与 `endOf(document, text)` 和 `src/editor/edit-document.test.ts` 中的相同。另加：

```ts
function positions(document: EditDocument): number[] {
  return document.outline.headings.map((_, index) => document.outline.position(index));
}

function count(document: EditDocument) {
  const calls = { count: 0 };
  const unsubscribe = document.outline.subscribe(() => (calls.count += 1));
  return { calls, unsubscribe };
}

test("the outline lists level 1-4 headings, nested ones included, each at its heading node", async () => {
  const document = await open(
    "levels.md",
    "# A\n\n## B\n\n### C\n\n#### D\n\n##### E\n\n###### F\n\n> ## Quoted\n\n- item\n\n  ### Listed\n\n" +
      "Setext\n---\n\n```sh\n# comment\n```\n",
  );
  assert.deepEqual(document.outline.headings, [
    { level: 1, label: "A" },
    { level: 2, label: "B" },
    { level: 3, label: "C" },
    { level: 4, label: "D" },
    { level: 2, label: "Quoted" },
    { level: 3, label: "Listed" },
    { level: 2, label: "Setext" },
  ]);
  document.outline.headings.forEach(({ level }, index) => {
    const node = document.editor.state.doc.nodeAt(document.outline.position(index));
    assert.equal(node?.type.name, "heading");
    assert.equal(node?.attrs.level, level);
  });
});

test("typing before the first heading keeps the headings array, notifies nobody and moves the positions", async () => {
  const document = await open("typing.md", "Intro.\n\n## First\n\n## Second\n");
  const headings = document.outline.headings;
  const before = positions(document);
  const { calls, unsubscribe } = count(document);
  document.editor.commands.setTextSelection(endOf(document, "Intro."));
  typeText(document.editor, "abc");
  assert.equal(document.outline.headings, headings);
  assert.equal(calls.count, 0);
  assert.deepEqual(positions(document), before.map((position) => position + 3));
  unsubscribe();
});

test("editing heading text, making a heading, changing a level to 5 and deleting a heading each notify once", async () => {
  const document = await open("changes.md", "## First\n\nBody.\n");
  const { editor } = document;
  const { calls, unsubscribe } = count(document);

  editor.view.dispatch(editor.state.tr.insertText(" part", endOf(document, "First")));
  assert.deepEqual(document.outline.headings, [{ level: 2, label: "First part" }]);
  assert.equal(calls.count, 1);

  editor.chain().setTextSelection(endOf(document, "Body.")).setHeading({ level: 3 }).run();
  assert.deepEqual(document.outline.headings, [
    { level: 2, label: "First part" },
    { level: 3, label: "Body." },
  ]);
  assert.equal(calls.count, 2);

  editor.chain().setTextSelection(endOf(document, "First part")).setHeading({ level: 5 }).run();
  assert.deepEqual(document.outline.headings, [{ level: 3, label: "Body." }]);
  assert.equal(calls.count, 3);

  const at = document.outline.position(0);
  editor.view.dispatch(editor.state.tr.delete(at, at + editor.state.doc.nodeAt(at)!.nodeSize));
  assert.deepEqual(document.outline.headings, []);
  assert.equal(calls.count, 4);
  unsubscribe();
});

test("undo and redo bring the headings back", async () => {
  const document = await open("undo.md", "## First\n\nBody.\n");
  const { editor } = document;
  const { calls, unsubscribe } = count(document);
  const both = [
    { level: 2, label: "First" },
    { level: 3, label: "Body." },
  ];
  editor.chain().setTextSelection(endOf(document, "Body.")).setHeading({ level: 3 }).run();
  assert.deepEqual(document.outline.headings, both);
  editor.commands.undo();
  assert.deepEqual(document.outline.headings, [{ level: 2, label: "First" }]);
  editor.commands.redo();
  assert.deepEqual(document.outline.headings, both);
  assert.equal(calls.count, 3);
  unsubscribe();
});

test("headings with the same text are separate items at their own positions", async () => {
  const document = await open("same.md", "## Intro\n\nOne.\n\n## Intro\n\nTwo.\n");
  assert.deepEqual(document.outline.headings, [
    { level: 2, label: "Intro" },
    { level: 2, label: "Intro" },
  ]);
  const [first, second] = positions(document);
  assert.ok(first! < second!);
  assert.equal(document.editor.state.doc.nodeAt(second!)?.type.name, "heading");
});

test("an empty heading is not an item until text is typed in it", async () => {
  const document = await open("empty.md", "Text.\n");
  const { editor } = document;
  const { calls, unsubscribe } = count(document);
  editor.chain().setTextSelection(endOf(document, "Text.")).splitBlock().setHeading({ level: 2 }).run();
  assert.deepEqual(document.outline.headings, []);
  assert.equal(calls.count, 0);
  typeText(editor, "New");
  assert.deepEqual(document.outline.headings, [{ level: 2, label: "New" }]);
  assert.equal(calls.count, 3);
  unsubscribe();
});

test("inline HTML keeps its source in the label and footnote definitions add no items", async () => {
  const document = await open(
    "raw.md",
    "## Install <kbd>npm</kbd>\n\nText[^a].\n\n[^a]: Note.\n\n    ## Inside note\n",
  );
  assert.deepEqual(document.outline.headings, [{ level: 2, label: "Install <kbd>npm</kbd>" }]);
});

test("a refresh that reads changed text updates the headings", async () => {
  const document = await open("refresh.md", "## Old\n");
  host.files.set("refresh.md", "## New\n\n### Added\n");
  await document.show({ uri: fileUri("refresh.md"), name: "refresh.md" });
  await until(() => document.outline.headings.length === 2, "the refreshed headings");
  assert.deepEqual(document.outline.headings, [
    { level: 2, label: "New" },
    { level: 3, label: "Added" },
  ]);
});

test("dispose stops notifications and a later unsubscribe still works", async () => {
  const document = await open("dispose.md", "## Title\n");
  const { calls, unsubscribe } = count(document);
  document.outline.dispose();
  document.editor.commands.setTextSelection(endOf(document, "Title"));
  typeText(document.editor, "s");
  assert.equal(calls.count, 0);
  unsubscribe();
});
```

- [ ] **步骤 2：运行测试，确认失败**

运行：`pnpm --filter @agw/extension-markdown exec tsx --test src/editor/edit-outline.test.ts`
预期：FAIL，`document.outline` 为 `undefined`。

- [ ] **步骤 3：实现 `EditOutline` 并接入 `EditDocument`**

- 构造时收集一次，并监听 `editor.on("transaction", …)`；`editor.state.doc` 与上次收集的文档不是同一个对象时重新收集（设计 1.3 节）。
- 收集：`doc.descendants((node, pos) => …)`。`heading` 时，`level <= 4` 且文字非空就记录 `{ level, label }` 与 `pos`，返回 `false`；`node.isTextblock || node.isAtom` 时返回 `false`；其他返回 `true`。
- 文字：`node.textBetween(0, node.content.size, "", (leaf) => leaf.type.name === "rawInline" ? String(leaf.attrs.raw) : " ")`，再经 `outlineLabel`。ProseMirror 的 `leafText` 参数接收函数，`rawInline` 取原文，其他叶子节点（`footnoteReference`、`image`、`hardBreak`）各算一个空格。
- 位置列表每次收集都替换；`sameOutline(新, 旧)` 为 `false` 时替换 `headings` 数组并调用每个监听者，为 `true` 时保留原数组、不调用监听者。
- `dispose()`：`editor.off("transaction", …)` 并清空监听者；重复调用没有影响。
- `EditDocument`：声明 `public readonly outline: EditOutline;`，在构造函数中 `this.editor = new Editor(...)` 之后赋值 `this.outline = new EditOutline(this.editor)`；`dispose()` 中 `this.outline.dispose()` 放在 `this.editor.destroy()` 之前。

- [ ] **步骤 4：运行测试，确认通过**

运行：`pnpm --filter @agw/extension-markdown exec tsx --test src/editor/edit-outline.test.ts src/editor/edit-document.test.ts`，再运行 `typecheck`。
预期：全部 PASS，typecheck 没有错误。

---

### 任务 4：大纲面板与 Preview 布局

**文件：**
- 新增：`src/extensions/markdown/src/shared/outline-panel.tsx`、`src/extensions/markdown/src/shared/outline.css`、`src/extensions/markdown/src/test-layout.ts`
- 修改：`src/extensions/markdown/src/preview/markdown-document.tsx`、`src/extensions/markdown/src/preview/markdown.css:34-64`、`src/extensions/markdown/src/preview/main.tsx:9-11`、`src/extensions/markdown/src/editor/test-edit-host.ts:33-44`
- 测试：`src/extensions/markdown/src/preview/markdown-document.test.ts`

**接口：**
- 使用：任务 1 的 `OutlineHeading`、`activeHeadingIndex`；任务 2 的 `renderMarkdown(...).outline`、`PreviewOutlineHeading`。
- 产出：

```ts
// src/shared/outline-panel.tsx
export function OutlinePanel(props: {
  headings: readonly OutlineHeading[];
  active: number | null;
  onSelect(index: number): void;
}): ReactElement;
export function useActiveHeading(options: {
  scroller: HTMLElement | Window | null;
  documentColumn: HTMLElement | null;
  headingElements(): readonly (HTMLElement | null)[];
  headings: readonly OutlineHeading[];
}): { active: number | null; pin(index: number): void };
// src/test-layout.ts
export class LayoutFreeResizeObserver { observe(): void; unobserve(): void; disconnect(): void }
```

- [ ] **步骤 1：修改测试环境，写失败的测试**

`markdown-document.test.ts` 的 jsdom 启用 `pretendToBeVisual`，并提供 `ResizeObserver`、`requestAnimationFrame` 与 `cancelAnimationFrame`。从 `../test-layout` 导入 `LayoutFreeResizeObserver` 和 `outlineItems`。

```ts
test("a document with level 1-4 headings shows the outline beside the article", async () => {
  files.set(`${ROOT}outline.md`, {
    text: "## Install\n\n### Requirements\n\n#### Versions\n\n##### Five\n\n## Usage\n",
  });
  const viewer = mountViewer();
  try {
    await viewer.show("outline.md");
    await until(viewer, () => outlineItems(viewer.container) !== null, "the outline");
    assert.deepEqual(outlineItems(viewer.container), [
      ["Install", "0"],
      ["Requirements", "1"],
      ["Versions", "2"],
      ["Usage", "0"],
    ]);
    const layout = viewer.container.querySelector(".preview-layout")!;
    assert.equal(layout.hasAttribute("data-outline"), true);
    assert.deepEqual(
      [...layout.children].map((child) => child.localName),
      ["article", "nav"],
    );
    const nav = layout.querySelector("nav.outline")!;
    assert.equal(nav.getAttribute("aria-label"), "Outline");
    assert.equal(nav.querySelector(".outline-title")?.textContent, "Outline");
    for (const item of nav.querySelectorAll(".outline-item")) {
      assert.equal(item.getAttribute("type"), "button");
      assert.equal(item.getAttribute("title"), item.textContent);
    }
  } finally {
    await viewer.unmount();
  }
});

test("a document without level 1-4 headings shows no outline", async () => {
  files.set(`${ROOT}flat.md`, { text: "##### Five\n\nText.\n" });
  const viewer = mountViewer();
  try {
    await viewer.show("flat.md");
    await until(viewer, () => paragraph(viewer) === "Text.", "the content");
    assert.equal(outlineItems(viewer.container), null);
    assert.equal(
      viewer.container.querySelector(".preview-layout")?.hasAttribute("data-outline"),
      false,
    );
  } finally {
    await viewer.unmount();
  }
});

test("loading and failed reads show no outline", async () => {
  files.set(`${ROOT}slow.md`, { text: "# Slow\n" });
  files.set(`${ROOT}failed.md`, { code: "ERR_NOT_TEXT" });
  const viewer = mountViewer();
  try {
    let release!: () => void;
    gate = new Promise((resolve) => (release = resolve));
    await viewer.show("slow.md");
    await until(viewer, () => reads.length === 1, "the read");
    assert.match(viewer.container.textContent ?? "", /Loading/u);
    assert.equal(outlineItems(viewer.container), null);
    release();
    await until(viewer, () => outlineItems(viewer.container) !== null, "the outline after the read");
    await viewer.show("failed.md");
    await until(
      viewer,
      () => viewer.container.querySelector(".document-error") !== null,
      "the error panel",
    );
    assert.equal(outlineItems(viewer.container), null);
  } finally {
    await viewer.unmount();
  }
});

test("showing a changed file again updates the outline", async () => {
  files.set(`${ROOT}changing.md`, { text: "# First\n" });
  const viewer = mountViewer();
  try {
    await viewer.show("changing.md");
    await until(viewer, () => outlineItems(viewer.container)?.length === 1, "the first outline");
    files.set(`${ROOT}changing.md`, { text: "# First\n\n## Second\n" });
    await viewer.show("changing.md");
    await until(viewer, () => outlineItems(viewer.container)?.length === 2, "the updated outline");
    assert.deepEqual(outlineItems(viewer.container), [
      ["First", "0"],
      ["Second", "1"],
    ]);
  } finally {
    await viewer.unmount();
  }
});
```

- [ ] **步骤 2：运行测试，确认失败**

运行：`pnpm --filter @agw/extension-markdown exec tsx --test src/preview/markdown-document.test.ts`
预期：FAIL；先是找不到模块 `../test-layout`，建好该文件后是没有 `nav.outline`。

- [ ] **步骤 3：实现**

- `src/test-layout.ts`：从 `test-edit-host.ts` 移入 `LayoutFreeResizeObserver`，给 Radix tooltip 与大纲的 jsdom 测试使用。Preview 与 Mixed 共用 `outlineItems(container)` 读取大纲文字和层级。
- `OutlinePanel`（设计 2.1 节）：DOM 与设计中的结构相同；`headings` 为空时 `ol` 的位置显示 `<p class="outline-empty">No headings</p>`；`data-depth` 为 `level` 减去全部项中最小的 `level`；只有当前项带 `aria-current="location"`；`title` 为完整文字；点击调用 `onSelect(index)`。当前项变化后在 `useLayoutEffect` 中让它保持可见：可见范围为 `nav` 的矩形去掉计算样式中的 `padding-top` 与 `padding-bottom`，当前项超出时只修改 `nav.scrollTop`。不调用 `scrollIntoView`，它会同时滚动内容的滚动区域。
- `useActiveHeading`（设计 4.2 节）：
  - 测量：`scroller === window` 时滚动元素为 `document.documentElement`、可见部分上沿为 0，否则为 `scroller` 本身与它的 `getBoundingClientRect().top`；`tops` 为每个标题元素的 `getBoundingClientRect().top` 减去可见部分上沿，`height` 为滚动元素的 `clientHeight`，`atBottom` 为 `scrollTop + clientHeight >= scrollHeight - 1`。标题元素为 `null` 时抛出错误（大纲项对应的标题一定已经渲染）。
  - pin：`pin(index)` 记录 `scrollTop` 并立即把当前项设为 `index`。测量时 `scrollTop` 与记录相同就保持 `index`，不同就清除记录后按 `activeHeadingIndex` 计算。
  - `scroller` 与 `documentColumn` 都存在时，监听 `scroller` 的 `scroll`、窗口的 `resize`，并用 `ResizeObserver` 观察正文列。三个来源共用 `requestAnimationFrame` 测量。清理时移除监听、断开观察者并取消动画帧。每次提交时更新 `headingElements` 的 ref。
  - `headings` 变化时在 `useLayoutEffect` 中清除 pin 并立即测量（`scroller` 为 `null` 时跳过）。
- `outline.css`：按设计 2.2 节写全部规则。
- `markdown.css` 按设计 2.4 节设置 `--document-column-width`、正文间距以及大纲左侧间距和工作区背景下的上沿。网格、960px 断点与卡片样式使用 `outline.css` 的共用规则。
- `preview/main.tsx`：在 `./markdown.css` 之前加入 `import "../shared/outline.css";`。
- `markdown-document.tsx` 读取成功后渲染 `MarkdownDocumentLayout`，在其中用 `useMemo` 取得渲染元素和大纲。layout effect 调用 `indexRenderedHeadings(article)`，建立支持空字符串 ID 的标题映射。遇到重复 ID 时使用先出现的标题。有大纲项时添加 `data-outline` 并渲染 `OutlinePanel`。跳转与当前项计算共用映射。用 `## !!!` 验证空 ID，再验证正文标题和脚注区标签同名时的映射结果。

- [ ] **步骤 4：运行测试，确认通过**

运行：`pnpm --filter @agw/extension-markdown exec tsx --test src/preview/markdown-document.test.ts src/editor/edit-view.test.ts`，再运行 `typecheck` 与 `lint`。
预期：全部 PASS；`edit-view.test.ts` 用来确认 `LayoutFreeResizeObserver` 移动后 Mixed 的测试环境不变。

---

### 任务 5：Mixed 布局、跳转与当前项

**文件：**
- 修改：`src/extensions/markdown/src/editor/edit-view.tsx:16-59`
- 修改：`src/extensions/markdown/src/editor/edit.css:49-81`（并在标题样式附近加入 `scroll-margin-top`）
- 修改：`src/extensions/markdown/src/editor/main.tsx:13-16`
- 测试：`src/extensions/markdown/src/editor/edit-view.test.ts`

**接口：**
- 使用：任务 3 的 `EditDocument.outline`（`headings`、`position`、`subscribe`）；任务 4 的 `OutlinePanel`、`useActiveHeading`。

- [ ] **步骤 1：写失败的测试**

现有用例 `a load failure shows the error panel and hides the editor` 末尾加入：

```ts
  assert.equal(
    container.querySelector(".edit-scroll")?.contains(container.querySelector("nav.outline")),
    true,
  );
```

从 `../test-layout` 导入 `outlineItems`。给现有卸载用例的文档增加标题，验证大纲渲染。卸载时记录 React 错误，并等待一个动画帧确认没有错误。

```ts
test("the outline beside the editor lists the headings with their depth", async () => {
  const { container } = await mount(
    "outline.md",
    "## Install\n\n### Requirements\n\n#### Versions\n\n## Usage\n",
  );
  assert.deepEqual(outlineItems(container), [
    ["Install", "0"],
    ["Requirements", "1"],
    ["Versions", "2"],
    ["Usage", "0"],
  ]);
  const layout = container.querySelector(".edit-scroll > .edit-layout")!;
  assert.deepEqual(
    [...layout.children].map((child) => child.className),
    ["edit-column document-column", "outline"],
  );
});

test("the outline shows No headings before creating a heading and after deleting it", async () => {
  const { document, container } = await mount("no-headings.md", "Text.\n");
  const { editor } = document;
  const empty = () => container.querySelector("nav.outline .outline-empty")?.textContent ?? null;
  assert.equal(empty(), "No headings");
  await act(async () => {
    editor.commands.setTextSelection(1 + "Text.".length);
    pressKey(editor, "Enter");
    typeText(editor, "## Title");
    await settle();
  });
  assert.deepEqual(outlineItems(container), [["Title", "0"]]);
  assert.equal(empty(), null);
  await act(async () => {
    editor.commands.deleteNode("heading");
    await settle();
  });
  assert.equal(empty(), "No headings");
});

test("typing in a paragraph of a document with headings commits nothing in React", async () => {
  const { document, commits } = await mount("typing-outline.md", "## Title\n\nText.\n");
  await act(async () => {
    document.editor.commands.setTextSelection(document.editor.state.doc.content.size - 1);
    await settle();
  });
  const before = commits.count;
  await act(async () => {
    typeText(document.editor, "x");
    await settle();
  });
  assert.equal(commits.count, before);
});

```

- [ ] **步骤 2：运行测试，确认失败**

运行：`pnpm --filter @agw/extension-markdown exec tsx --test src/editor/edit-view.test.ts`
预期：新用例与加了断言的读取失败用例 FAIL（没有 `nav.outline`）；`typing in a paragraph…` 与 `unloading…` 此时可能已经通过，它们防止后面的实现引入问题。

- [ ] **步骤 3：实现**

- `edit-view.tsx`：`EditView` 处理文档状态及菜单，`EditDocumentLayout` 管理正文与大纲。布局组件用 `useSyncExternalStore` 订阅大纲，通过 `editor.view.nodeDOM(outline.position(index))` 找标题。读取成功时启用当前项监听。跳转按设计第 3 节执行。
- `edit.css` 设置 `--document-column-width`、工作区背景下两张卡片的间距，以及标题的 `scroll-margin-top`。网格、960px 断点与卡片样式使用 `outline.css` 的共用规则。
- `editor/main.tsx`：在 `./edit.css` 之前加入 `import "../shared/outline.css";`。

- [ ] **步骤 4：运行 Markdown extension 的全部检查**

运行：在仓库的 `src` 目录执行 `pnpm --filter @agw/extension-markdown typecheck`、`lint`、`test`、`format:check`，将各命令的输出和退出码保存在 worktree 的 `.scratch`。格式化范围限于本计划改动的文件。
预期：全部通过。

---

### 任务 6：Desktop 集成测试

**文件：**
- 修改：`src/clients/desktop/scripts/test-extensions.mjs`（`FIRST_LINE_MARKDOWN` 之后加入常量；约 575 行 `notes/edit.md` 之后写入文件）
- 修改：`src/clients/desktop/src/main/extensions.integration.ts`（`clickInFrame` 约 4380 行；新增两个步骤；`run()` 约 10347、10351 行）

**接口：**
- 使用：任务 4、5 的 DOM：`nav.outline`、`.outline-item[data-depth]`、`aria-current="location"`、`.outline-empty`、`.preview-layout`、`.edit-layout`、`.edit-column`、`.markdown-body`。

- [ ] **步骤 1：写入测试文件**

`test-extensions.mjs` 中加入带双语注释的常量，并在 `notes/edit.md` 之后写入 `notes/outline.md` 与 `notes/plain.md`：

```js
const OUTLINE_BODY = Array.from(
  { length: 20 },
  (_, index) => `Body paragraph ${index + 1} of this section.`,
).join("\n\n");
const OUTLINE_MARKDOWN = [
  "# Outline sample", OUTLINE_BODY,
  "## Install", OUTLINE_BODY,
  "### Requirements", OUTLINE_BODY,
  "#### Versions", OUTLINE_BODY,
  "##### Level five", OUTLINE_BODY,
  "###### Level six", OUTLINE_BODY,
  "## 中文 标题", OUTLINE_BODY,
  "## Usage", OUTLINE_BODY,
  "### Options", OUTLINE_BODY,
  "## Short end", "Short end text.",
  "## Last", "Last text.",
].join("\n\n") + "\n";
const PLAIN_MARKDOWN = "First plain paragraph.\n\nSecond plain paragraph.\n";
```

- [ ] **步骤 2：`clickInFrame` 增加不滚动的点击**

签名改为 `clickInFrame(frame, element, modifiers = [], { scroll = true }: { scroll?: boolean } = {})`。`scroll` 为 `false` 时跳过调用 `scrollIntoView({ block: "center" })` 的那一段，元素出现与可以点到都由后面的 `centerOf` 等待。双语注释说明原因：大纲项在 `position: sticky` 的 `nav` 中，对它调用 `scrollIntoView` 会滚动页面本身。

- [ ] **步骤 3：新增辅助表达式与 `markdownOutline()`**

```ts
const OUTLINE_ITEMS = `[...document.querySelectorAll("nav.outline .outline-item")].map((item) => [item.textContent, item.getAttribute("data-depth")])`;
const CURRENT_OUTLINE_ITEM = `(document.querySelector('nav.outline [aria-current="location"]')?.textContent ?? null)`;
const OUTLINE_EXPECTED = [
  ["Outline sample", "0"], ["Install", "1"], ["Requirements", "2"], ["Versions", "3"],
  ["中文 标题", "1"], ["Usage", "1"], ["Options", "2"], ["Short end", "1"], ["Last", "1"],
];
function outlineItem(label: string): string; // nav.outline 中文字为 label 的 .outline-item
```

`markdownOutline()` 依次检查下面各项，每项结束时调用 `pass(...)`，最后 `goToChat()`。大纲项一律用 `clickInFrame(frame, outlineItem(label), [], { scroll: false })` 点击。

1. Preview 大纲：`openFileInTree("notes/outline.md")`、`selectPreview()`，`OUTLINE_ITEMS` 等于 `OUTLINE_EXPECTED`；`.markdown-body` 宽度等于 `Math.min(980, layout.width - 256)`，`nav.outline` 左边等于 `.markdown-body` 右边加 16（误差都为 1px，`layout` 为 `.preview-layout` 的矩形）。
2. Preview 跳转：点击 `Usage`，等到 `document.getElementById("usage")` 的上沿在 16±1px 且 `CURRENT_OUTLINE_ITEM === "Usage"`；点击 `中文 标题`，`document.getElementById("中文-标题")` 的上沿在 16±1px 且它成为当前项。
3. Preview 当前项：`window.scrollTo` 到 `usage` 标题的文档位置加 100px，等到当前项为 `Usage`。滚动到底部并等到当前项为 `Last`。仅缩小窗口高度，确认滚动位置和正文宽高相同，当前项随可见高度变化。恢复高度后，点击 `Short end`，确认大纲高亮该节且页面仍在底部。
4. Mixed 跳转与光标：`selectViewer(MIXED_VIEWER)`，`mixedShowing(\`Boolean(${mixedBlock("Usage", "h2")})\`, …)`；`OUTLINE_ITEMS` 同样等于 `OUTLINE_EXPECTED`；`.edit-column` 宽度等于 `Math.min(894, layout.width - 256)`，`nav.outline` 左边等于 `.edit-column` 右边（`layout` 为 `.edit-layout` 的矩形）。点击 `Usage`，等到该 `h2` 上沿减去 `.edit-scroll` 上沿在 16±1px、`document.activeElement` 为 `.ProseMirror`、`getSelection()` 的 `anchorNode` 在该 `h2` 内且 `anchorOffset === 0`。`typeIn(mixed, "Go ")` 后等到 `mixedBlock("Go Usage", "h2")` 存在且大纲中有 `Go Usage` 一项；`saveIn(mixed)`，等到未保存标记消失。
5. Mixed 空大纲：`openFileInTree("notes/plain.md")`、`selectViewer(MIXED_VIEWER)`、`mixedShowing`；`.outline-empty` 文字为 `No headings`；记录 `.edit-column` 的 `left` 与 `width`。`placeCaret` 到 `Second plain paragraph.` 末尾，`pressKey(mixed, "Enter")`，`typeIn(mixed, "/")`，用 `clickInFrame` 点击 `/` 菜单的 `Heading 2`（与 `textEditorMarkdown` 第 4 项相同），`typeIn(mixed, "Added")`；等到 `OUTLINE_ITEMS` 为 `[["Added", "0"]]` 且当前项为 `Added`（文档短于一屏，滚动区域在底部）；`.edit-column` 的 `left` 与 `width` 与之前相同（误差 0.5px）。保存后等到未保存标记消失。
6. 将 Desktop 调整到 `getMinimumSize()` 提供的最小宽度，保持原有高度。打开 `notes/outline.md`，在 Preview 与 Mixed 中分别检查页面宽度小于 960px 时隐藏大纲，正文按查看器的最大列宽居中。恢复窗口尺寸后，确认页面宽度至少为 960px，两个查看器重新显示大纲。

- [ ] **步骤 4：新增 `markdownOutlineBackdrop()`**

在 Warm Minimalism 主题下（`warmMinimalismTheme()` 结束时的状态）：打开 `notes/outline.md`。Mixed 中 `.edit-scroll` 滚动到顶部后，`nav.outline` 与 `.edit-column` 的计算背景色、`border-top-left-radius` 相同，两者上沿相差不超过 1px；Preview 中 `window.scrollTo(0, 0)` 后，`nav.outline` 与 `.markdown-body` 做同样的检查。结束时调用 `pass(...)`。

- [ ] **步骤 5：加入 `run()`**

`await markdownOutline();` 放在 `await dragHandleFirstLine();` 与 `await hostScaling();` 之间（`dragHandleFirstLine` 依赖 `textEditorTypography` 留下的编辑器状态，`hostScaling` 自己打开 `readme.md`）；`await markdownOutlineBackdrop();` 放在 `await warmMinimalismTheme();` 之后（`sidebarThemes` 自己选择主题与模式）。

- [ ] **步骤 6：缩短版运行（请求用户确认）**

将 `scripts/test-extensions.mjs` 的完整场景阶段列表临时设为 `["first"]`。在 `run()` 的 `await boot();` 之后临时加入：

```ts
if (config.phase === "first") {
  await bundledDefaults();
  await enableSample();
  await markdownOutline();
  await warmMinimalismTheme();
  await markdownOutlineBackdrop();
  return;
}
```

请求确认时说明：这次运行只回答“新增的两个大纲步骤在真实 Electron 页面中是否通过”。用户确认后在 `src` 目录运行 `pnpm --filter @agw/desktop test:extensions`。预期：日志中有新增步骤的 `PASS` 行，退出码为 0。每次运行后用编辑工具恢复阶段列表并删除临时分支。失败时按日志修正，修正后再次请求确认。

- [ ] **步骤 7：完整运行（请求用户确认）**

请求确认时说明：这次运行回答“加入大纲后，其他步骤是否仍然通过”，与上次运行的区别是包含全部步骤。用户确认后运行同一命令。预期：first 与 restart 阶段全部通过；performance 阶段的已知失败（`perf/tables-631.md keystroke` 超过 50 ms）与本计划无关，在报告中照实说明。运行结束后把 `src/clients/desktop/renderer/next-env.d.ts` 中被构建改成 `./.next/types/...` 的路径手工改回 `./.next/dev/types/...`；如果用户开着 `pnpm dev:desktop`，提醒用户重启。
