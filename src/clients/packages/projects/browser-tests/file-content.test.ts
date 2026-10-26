// FileContent 在 Chromium 中的行为：渲染 Source（Monaco）的切换与菜单用例，以及 Source 与 diff 的行评论（设计文档测试计划）。
// FileContent in Chromium: the switch and menu cases that render Source (Monaco), and line comments in Source and the
// diff (design test plan).
import assert from "node:assert/strict";
import { resolve } from "node:path";
import { after, before, test, type TestContext } from "node:test";
import { setTimeout as delay } from "node:timers/promises";

import { type BrowserFixture, startBrowserFixture } from "@agw/test-harness/browser";
import type { Locator, Page } from "playwright";

import type { FileViewerOption } from "../src/ui-web/components/file-explorer/file-content";
import type { CommentData, EditableScenario, Scenario } from "./fixture/main";

let fixture: BrowserFixture;

before(async () => {
  fixture = await startBrowserFixture({
    root: resolve("browser-tests/fixture"),
    outDir: resolve(".scratch/file-content-browser"),
  });
});

after(async () => {
  await fixture?.close();
});

const VIEWERS: FileViewerOption[] = [
  {
    id: "markdown-preview:preview",
    title: "Preview",
    unsaved: false,
    saveStatus: "saved",
    topItems: [],
    bottomItems: [],
  },
  {
    id: "other:outline",
    title: "Outline",
    unsaved: true,
    saveStatus: "changed-on-disk",
    topItems: [],
    bottomItems: [],
  },
];

const VIEWERS_WITH_ITEMS: FileViewerOption[] = [
  {
    ...VIEWERS[0],
    bottomItems: [
      { id: "export", label: "Export" },
      {
        id: "indent",
        label: "Indentation",
        menu: [
          { id: "tabs", label: "Tabs" },
          { id: "spaces", label: "Spaces" },
        ],
      },
    ],
  },
  VIEWERS[1],
];

const SOURCE = '[data-code-view="source"]';

async function until<T>(check: () => Promise<T>, label: string): Promise<T> {
  const deadline = Date.now() + 10_000;
  let last: T;
  do {
    last = await check();
    if (last) return last;
    await delay(50);
  } while (Date.now() < deadline);
  throw new Error(`Timed out waiting for ${label}: ${JSON.stringify(last)}`);
}

async function open(t: TestContext, scenario: Scenario) {
  const { page, errors } = await fixture.newPage();
  t.after(async () => {
    await page.close();
  });
  await page.goto(fixture.origin);
  await render(page, scenario);
  return { page, done: () => assert.deepEqual(errors, []) };
}

async function render(page: Page, scenario: Scenario) {
  await page.evaluate((value) => window.fixture.render(value), scenario);
  await page.getByText(scenario.selectedFile, { exact: true }).first().waitFor();
}

function calls(page: Page) {
  return page.evaluate(() => window.fixture.calls);
}

function comments(page: Page): Promise<CommentData[]> {
  return page.evaluate(() => window.fixture.comments);
}

function tabs(page: Page) {
  return page.evaluate(() =>
    [...document.querySelectorAll('[role="tab"]')].map((tab) => ({
      id: tab.getAttribute("data-file-viewer-tab"),
      selected: tab.getAttribute("aria-selected") === "true",
      text: (tab.textContent ?? "").trim(),
    })),
  );
}

async function selectedTab(page: Page) {
  return (await tabs(page)).find((tab) => tab.selected)?.id;
}

async function selectTab(page: Page, id: string) {
  await page.locator(`[data-file-viewer-tab="${id}"]`).click();
  await until(async () => (await selectedTab(page)) === id, `the ${id} tab`);
}

async function openMoreMenu(page: Page): Promise<Locator> {
  await page.getByRole("button", { name: "More" }).click();
  const menu = page.getByRole("menu");
  await menu.waitFor();
  return menu;
}

async function closeMenu(page: Page) {
  await page.keyboard.press("Escape");
  await page.getByRole("menu").waitFor({ state: "detached" });
}

async function chooseMoreItem(page: Page, text: string) {
  const menu = await openMoreMenu(page);
  await menu.getByText(text, { exact: true }).click();
  await page.getByRole("menu").waitFor({ state: "detached" });
}

/** 菜单中各项的文字，含勾选项。The texts of the menu's entries, checkbox items included. */
async function menuTexts(menu: Locator) {
  return (await menu.locator('[role="menuitem"], [role="menuitemcheckbox"]').allTextContents()).map(
    (text) => text.trim(),
  );
}

async function moreMenuInfo(page: Page) {
  const menu = await openMoreMenu(page);
  const info = await menu.getByTestId("info").textContent();
  await closeMenu(page);
  return info;
}

/** Source 显示的文字，按行的位置排列。The text Source shows, in line order. */
function sourceText(page: Page) {
  return page.evaluate((selector) => {
    const lines = [...document.querySelectorAll<HTMLElement>(`${selector} .view-line`)];
    return lines
      .map(
        (line) =>
          [Number.parseFloat(line.style.top), line.textContent!.replace(/ /gu, " ")] as const,
      )
      .sort((left, right) => left[0] - right[0])
      .map((entry) => entry[1])
      .join("\n");
  }, SOURCE);
}

/** 鼠标移到行号上，点击行号旁出现的 "Add comment"。Hovers a line number and clicks the "Add comment" beside it. */
async function addCommentOn(page: Page, editor: string, line: number, text: string) {
  await page.locator(`${editor} .line-numbers`).getByText(String(line), { exact: true }).hover();
  await page.locator(`${editor} [aria-label="Add comment, line ${line}"]:not(.is-hidden)`).click();
  const input = page.locator(`${editor} textarea:focus`);
  await input.fill(text);
  await page.keyboard.press("Control+Enter");
}

test("FileContent_WithViewers_ShowsSwitchAndMoreMenuWithInfoAndReload", async (t) => {
  const { page, done } = await open(t, {
    selectedFile: "README.md",
    viewers: VIEWERS,
    handlers: ["onReloadFileViewer", "renderFileViewerInfo", "onReloadSource"],
  });
  // 文件标题行右侧依次是切换按钮与 "More" 菜单，切换到 Source 后不变。The title row's right side holds the switch and
  // the "More" menu, also in Source.
  const controls = () =>
    page.evaluate(() => {
      const actions = document.querySelector('[role="tablist"]')!.parentElement!;
      return {
        children: [...actions.children].map(
          (child) => child.getAttribute("role") ?? child.getAttribute("aria-label"),
        ),
        row: actions.parentElement!.textContent ?? "",
      };
    });
  const initial = await controls();
  assert.deepEqual(initial.children, ["tablist", "More"]);
  assert.ok(initial.row.includes("README.md"));

  // 菜单顶部是查看器说明，分隔线之后是刷新。The menu starts with the viewer info, then reloading after a separator.
  const menu = await openMoreMenu(page);
  assert.equal(await menu.getByTestId("info").textContent(), "info markdown-preview:preview");
  assert.equal(
    await menu.evaluate(
      (element) =>
        element.firstElementChild === element.querySelector('[data-testid="info"]')!.parentElement,
    ),
    true,
  );
  assert.deepEqual(await menuTexts(menu), ["Reload view"]);
  assert.equal(await menu.locator('[role="separator"]').count(), 1);
  await closeMenu(page);

  await selectTab(page, "source");
  assert.deepEqual((await controls()).children, ["tablist", "More"]);
  assert.deepEqual(await menuTexts(await openMoreMenu(page)), ["Reload file", "Word wrap"]);
  done();
});

test("FileContent_SourceSelected_ReloadRereadsFileInsteadOfViewer", async (t) => {
  const { page, done } = await open(t, {
    selectedFile: "README.md",
    viewers: VIEWERS,
    handlers: ["onReloadFileViewer", "onReloadSource"],
  });

  await selectTab(page, "source");
  await chooseMoreItem(page, "Reload file");

  assert.deepEqual(await calls(page), [["onReloadSource"]]);
  assert.deepEqual(await menuTexts(await openMoreMenu(page)), ["Reload file", "Word wrap"]);
  done();
});

test("FileContent_SourceSelected_InfoShowsViewerSelectedBeforeSource", async (t) => {
  const { page, done } = await open(t, {
    selectedFile: "README.md",
    viewers: VIEWERS,
    handlers: ["renderFileViewerInfo"],
  });

  await selectTab(page, "source");
  assert.equal(await moreMenuInfo(page), "info markdown-preview:preview");

  await selectTab(page, "other:outline");
  await selectTab(page, "source");
  await selectTab(page, "source");
  assert.equal(await moreMenuInfo(page), "info other:outline");
  done();
});

test("FileContent_MoreMenu_TopItemsFollowReloadOnlyForSelectedViewer", async (t) => {
  const { page, done } = await open(t, {
    selectedFile: "README.md",
    viewers: [
      {
        ...VIEWERS[0],
        topItems: [
          {
            id: "encoding",
            label: "UTF-8",
            menu: [
              {
                id: "reopen",
                label: "Reopen with Encoding",
                menu: [
                  { id: "reopen:utf-8", label: "UTF-8" },
                  { id: "reopen:gbk", label: "GBK" },
                ],
              },
            ],
          },
          { id: "wrap", label: "Word wrap", checked: true },
          { id: "save", label: "Save", disabled: true },
        ],
        bottomItems: [{ id: "export", label: "Export" }],
      },
      VIEWERS[1],
    ],
    handlers: [
      "onReloadFileViewer",
      "onReloadSource",
      "renderFileViewerInfo",
      "onSetDefaultFileViewer",
      "onSelectFileViewerItem",
    ],
  });

  const menu = await openMoreMenu(page);
  assert.deepEqual(await menuTexts(menu), [
    "Reload view",
    "UTF-8",
    "Word wrap",
    "Save",
    "Open with Preview by default for *.md",
    "Export",
  ]);
  assert.equal(await menu.locator('[role="separator"]').count(), 3);
  // 勾选项显示勾选状态，不可用的项不能选择。A checkbox item shows its state; an unavailable entry cannot be chosen.
  const wordWrap = menu.getByRole("menuitemcheckbox", { name: "Word wrap" });
  assert.equal(await wordWrap.getAttribute("aria-checked"), "true");
  // 勾选标记在文字后面，各项的文字左边对齐。The check follows the label, and every label starts at the same left edge.
  const layout = await menu.evaluate((element) =>
    [...element.querySelectorAll('[role="menuitem"], [role="menuitemcheckbox"]')].map((item) => {
      const range = document.createRange();
      range.selectNodeContents(
        [...item.childNodes].find((node) => node.nodeType === Node.TEXT_NODE)!,
      );
      const label = range.getBoundingClientRect();
      const check = item.querySelector(".lucide-check")?.getBoundingClientRect() ?? null;
      return { labelLeft: label.left, labelRight: label.right, checkLeft: check?.left ?? null };
    }),
  );
  assert.equal(new Set(layout.map((item) => item.labelLeft)).size, 1, JSON.stringify(layout));
  const wordWrapLayout = layout[2]!;
  assert.ok(
    wordWrapLayout.checkLeft !== null && wordWrapLayout.checkLeft > wordWrapLayout.labelRight,
    JSON.stringify(wordWrapLayout),
  );
  const save = menu.getByRole("menuitem", { name: "Save" });
  assert.equal(await save.getAttribute("aria-disabled"), "true");
  await save.click({ force: true });
  assert.deepEqual(await calls(page), []);
  await wordWrap.click();
  assert.deepEqual(await calls(page), [
    ["onSelectFileViewerItem", "markdown-preview:preview", "wrap", null],
  ]);
  await page.getByRole("menu").waitFor({ state: "detached" });

  // 子菜单用键盘打开：鼠标斜向移动经过其他项会关闭子菜单。Submenus open by keyboard: a diagonal mouse path over other
  // items closes them.
  const reopened = await openMoreMenu(page);
  await reopened.getByRole("menuitem", { name: "UTF-8" }).focus();
  await page.keyboard.press("ArrowRight");
  await page.getByRole("menuitem", { name: "Reopen with Encoding" }).waitFor();
  await page.keyboard.press("ArrowRight");
  const gbk = page.getByRole("menuitem", { name: "GBK" });
  await gbk.focus();
  await page.keyboard.press("Enter");
  assert.deepEqual((await calls(page)).at(-1), [
    "onSelectFileViewerItem",
    "markdown-preview:preview",
    "encoding",
    "reopen:gbk",
  ]);
  await page.getByRole("menu").first().waitFor({ state: "detached" });

  // 选中 Source 时说明仍为切换前的查看器，菜单中没有它的条目。With Source selected the info still describes the
  // viewer selected before, and the menu has none of its items.
  await selectTab(page, "source");
  const sourceMenu = await openMoreMenu(page);
  assert.equal(await sourceMenu.getByTestId("info").textContent(), "info markdown-preview:preview");
  assert.deepEqual(await menuTexts(sourceMenu), [
    "Reload file",
    "Word wrap",
    "Open with Source by default for *.md",
  ]);
  done();
});

test("FileContent_WithoutReloadHandlers_MoreMenuHasNoReloadEntry", async (t) => {
  const { page, done } = await open(t, {
    selectedFile: "README.md",
    viewers: VIEWERS,
    handlers: ["renderFileViewerInfo"],
  });

  assert.deepEqual(await menuTexts(await openMoreMenu(page)), []);
  await closeMenu(page);
  await selectTab(page, "source");
  assert.deepEqual(await menuTexts(await openMoreMenu(page)), ["Word wrap"]);
  done();
});

test("FileContent_SelectedViewer_ShowsItsSaveStatusAfterFileName", async (t) => {
  const { page, done } = await open(t, {
    selectedFile: "README.md",
    viewers: VIEWERS,
    handlers: ["onReloadFileViewerDocument"],
  });
  const status = () =>
    page.evaluate(() => {
      const element = document.querySelector("[data-file-save-status]");
      return element
        ? {
            status: element.getAttribute("data-file-save-status"),
            text: element.textContent ?? "",
            previous: element.previousElementSibling?.textContent ?? null,
          }
        : null;
    });

  // 状态与文件名在同一行，紧跟文件名。The status sits in the file name's row, right after the name.
  assert.deepEqual(await status(), { status: "saved", text: "Saved", previous: "README.md" });
  await selectTab(page, "other:outline");
  assert.deepEqual(await status(), {
    status: "changed-on-disk",
    text: "Changed on diskReload",
    previous: "README.md",
  });
  await selectTab(page, "source");
  assert.equal(await status(), null);
  done();
});

test("FileContent_SelectSource_ShowsFileTextInMonaco", async (t) => {
  const { page, done } = await open(t, { selectedFile: "README.md", viewers: VIEWERS });

  await selectTab(page, "source");

  assert.equal(await page.getByTestId("viewer").count(), 0);
  await until(async () => (await sourceText(page)) === "# Title", "the Source text");
  done();
});

test("FileContent_WithoutViewers_ShowsTextWithoutTabs", async (t) => {
  const { page, done } = await open(t, { selectedFile: "README.md" });

  assert.deepEqual(await tabs(page), []);
  assert.equal(await page.getByTestId("viewer").count(), 0);
  await until(async () => (await sourceText(page)) === "# Title", "the Source text");
  done();
});

test("FileContent_DefaultViewerIdIsSource_SelectsSourceInitially", async (t) => {
  const { page, done } = await open(t, {
    selectedFile: "README.md",
    viewers: VIEWERS,
    defaultFileViewerId: "source",
  });

  assert.equal(await selectedTab(page), "source");
  assert.equal(await page.getByTestId("viewer").count(), 0);
  done();
});

test("FileContent_MoreMenu_SourceSelectedCanBecomeDefault", async (t) => {
  const { page, done } = await open(t, {
    selectedFile: "Dockerfile",
    viewers: VIEWERS,
    defaultFileViewerId: null,
    handlers: ["onSetDefaultFileViewer"],
  });

  await selectTab(page, "source");
  await chooseMoreItem(page, "Open with Source by default for dockerfile");

  assert.deepEqual(await calls(page), [["onSetDefaultFileViewer", "source"]]);
  done();
});

test("FileContent_MoreMenu_OtherViewerOrSourceSelected_HasNoItemsOfFirstViewer", async (t) => {
  const { page, done } = await open(t, {
    selectedFile: "README.md",
    viewers: VIEWERS_WITH_ITEMS,
    handlers: ["onSelectFileViewerItem", "onSetDefaultFileViewer"],
  });
  await selectTab(page, "other:outline");
  assert.deepEqual(await menuTexts(await openMoreMenu(page)), [
    "Open with Outline by default for *.md",
  ]);
  await closeMenu(page);

  // 没有默认查看器设置时，选中 Source 的 More 菜单只有它自己的 Word wrap。Without the default setting, Source's More
  // menu holds only its own Word wrap.
  await render(page, {
    selectedFile: "README.md",
    viewers: VIEWERS_WITH_ITEMS,
    handlers: ["onSelectFileViewerItem"],
  });
  await selectTab(page, "source");
  assert.deepEqual(await menuTexts(await openMoreMenu(page)), ["Word wrap"]);
  done();
});

const SOURCE_FILE = ["const a = 1;", "const b = 2;", "const c = 3;", "const d = 4;"].join("\n");

test("SourceView_Comments_AddEditAndDeleteProduceLineComments", async (t) => {
  const { page, done } = await open(t, { selectedFile: "src/app.ts", fileContent: SOURCE_FILE });
  await until(async () => (await sourceText(page)) === SOURCE_FILE, "the Source text");

  await addCommentOn(page, SOURCE, 2, "  Why two?  ");
  const added = (await until(async () => {
    const current = await comments(page);
    return current.length === 1 ? current[0] : null;
  }, "the added comment"))!;
  const { id, timestamp, ...fields } = added;
  assert.ok(id && timestamp);
  // 评论带上文件视图的项目、目录与目录名（这里没有目录名）。The comment carries the file view's project, directory and
  // directory name (none here).
  assert.deepEqual(fields, {
    projectId: "project-1",
    directoryId: "workspace",
    directoryName: undefined,
    side: "current",
    filePath: "src/app.ts",
    lineNumber: 2,
    content: "Why two?",
  });
  // 有评论的行带标记（Monaco 在下一帧画出），不再提供 "Add comment"。The commented line is marked (Monaco paints it on
  // the next frame) and offers no "Add comment".
  await until(
    async () => (await page.locator(`${SOURCE} .agw-code-view-marked-line`).count()) > 0,
    "the marked line",
  );
  await page.locator(`${SOURCE} .line-numbers`).getByText("2", { exact: true }).hover();
  assert.equal(
    await page.locator(`${SOURCE} [aria-label="Add comment, line 2"]:not(.is-hidden)`).count(),
    0,
  );

  await page.getByText("Why two?", { exact: true }).dblclick();
  const editor = page.locator('[data-comment-line="2"] textarea');
  await editor.fill("Why two? It should be three.");
  await page.keyboard.press("Control+Enter");
  await until(
    async () => (await comments(page))[0]?.content === "Why two? It should be three.",
    "the edited comment",
  );

  await page.getByTitle("Delete comment").click();
  await until(async () => (await comments(page)).length === 0, "the deleted comment");
  await page.locator('[data-comment-line="2"]').waitFor({ state: "detached" });
  done();
});

test("SourceView_ExistingComments_ShowInTheirZones", async (t) => {
  const { page, done } = await open(t, {
    selectedFile: "src/app.ts",
    fileContent: SOURCE_FILE,
    comments: [
      {
        projectId: "project-1",
        directoryId: "workspace",
        id: "existing",
        side: "current",
        filePath: "src/app.ts",
        lineNumber: 3,
        content: "Existing note",
        timestamp: "2026-10-25T08:00:00.000Z",
      },
      {
        projectId: "project-1",
        directoryId: "workspace",
        id: "other-file",
        side: "current",
        filePath: "src/other.ts",
        lineNumber: 1,
        content: "Other file",
        timestamp: "2026-10-25T08:00:00.000Z",
      },
    ],
  });

  await page.locator('[data-comment-line="3"]').getByText("Existing note").waitFor();
  assert.equal(await page.getByText("Other file").count(), 0);
  done();
});

const ORIGINAL = ["const a = 1;", "const b = 2;", "const c = 3;", ""].join("\n");
const MODIFIED = ["const a = 1;", "const b = 20;", "const c = 3;", "const d = 4;", ""].join("\n");

test("GitDiffView_CommentsOnBothSides_CarryDiffScope", async (t) => {
  const { page, done } = await open(t, {
    selectedFile: "src/calc.ts",
    onlyDiff: true,
    diffScope: "staged",
    diff: {
      diff: "@@ -1,3 +1,4 @@\n const a = 1;\n-const b = 2;\n+const b = 20;\n const c = 3;\n+const d = 4;\n",
      unchanged: false,
      isBinary: false,
      originalContent: ORIGINAL,
      modifiedContent: MODIFIED,
    },
  });
  const original = '[data-code-view="diff"] .editor.original';
  const modified = '[data-code-view="diff"] .editor.modified';
  await page.locator(".line-insert").first().waitFor({ state: "attached" });
  assert.equal(await page.locator('[data-diff-side="original"]').textContent(), "HEAD");
  assert.equal(await page.locator('[data-diff-side="modified"]').textContent(), "Staged");

  await addCommentOn(page, original, 2, "Old value");
  await addCommentOn(page, modified, 4, "New constant");
  const added = (await until(async () => {
    const current = await comments(page);
    return current.length === 2 ? current : null;
  }, "both comments"))!;
  assert.deepEqual(
    added.map(({ side, diffScope, lineNumber, content, filePath }) => ({
      side,
      diffScope,
      lineNumber,
      content,
      filePath,
    })),
    [
      {
        side: "original",
        diffScope: "staged",
        lineNumber: 2,
        content: "Old value",
        filePath: "src/calc.ts",
      },
      {
        side: "modified",
        diffScope: "staged",
        lineNumber: 4,
        content: "New constant",
        filePath: "src/calc.ts",
      },
    ],
  );
  done();
});

test("GitDiffView_UnchangedBinaryAndMetadataDiffs_ShowTheirViews", async (t) => {
  const { page, done } = await open(t, {
    selectedFile: "same.ts",
    onlyDiff: true,
    diffScope: "unstaged",
    diff: {
      diff: "",
      unchanged: true,
      isBinary: false,
      originalContent: "same\n",
      modifiedContent: "same\n",
    },
  });
  await page.getByText("No changes detected").waitFor();
  await until(async () => (await sourceText(page)) === "same\n", "the unchanged text");

  await render(page, {
    selectedFile: "image.png",
    onlyDiff: true,
    diffScope: "unstaged",
    diff: {
      diff: "Binary files a/image.png and b/image.png differ\n",
      unchanged: false,
      isBinary: true,
      originalContent: null,
      modifiedContent: null,
    },
  });
  await page.getByText("Binary file; its contents are not shown.").waitFor();
  assert.ok((await page.getByLabel("Git diff").textContent())!.includes("Binary files"));

  await render(page, {
    selectedFile: "run.sh",
    onlyDiff: true,
    diffScope: "unstaged",
    diff: {
      diff: "diff --git a/run.sh b/run.sh\nold mode 100644\nnew mode 100755\n",
      unchanged: false,
      isBinary: false,
      originalContent: "echo\n",
      modifiedContent: "echo\n",
    },
  });
  assert.ok(
    (await page.getByLabel("Git diff metadata").textContent())!.includes("new mode 100755"),
  );
  assert.equal(await page.locator('[data-code-view="diff"]').count(), 0);
  done();
});

const STATUS_BAR = "[data-code-view-status-bar]";
const UNFORMATTED = "function add(a:number,b:number){\nreturn a+b}\n";
const FORMATTED = "function add(a: number, b: number) {\n    return a + b;\n}\n";
const CLEAN: EditableScenario = {
  status: "clean",
  saveStatus: "saved",
  unsaved: false,
  encodingLabel: "UTF-8",
  error: null,
};

function setDocument(page: Page, update: Partial<EditableScenario>) {
  return page.evaluate((value) => window.fixture.setDocument(value), update);
}

function modelText(page: Page) {
  return page.evaluate(() => window.fixture.modelText());
}

/** 文件名后的保存状态文字；没有时为 null。The save status text after the file name, or null. */
function saveStatusText(page: Page) {
  return page.evaluate(
    () => document.querySelector("[data-file-save-status]")?.textContent ?? null,
  );
}

test("FileContent_ReadOnlySourceWithoutViewers_MoreMenuTogglesWordWrap", async (t) => {
  const longLine = `const text = "${"word ".repeat(120)}";`;
  const { page, done } = await open(t, { selectedFile: "src/long.ts", fileContent: longLine });
  assert.deepEqual(await tabs(page), []);
  const viewLines = () => page.locator(`${SOURCE} .view-line`).count();
  await until(async () => (await viewLines()) === 1, "one view line");

  const menu = await openMoreMenu(page);
  assert.deepEqual(await menuTexts(menu), ["Word wrap"]);
  await menu.getByRole("menuitemcheckbox", { name: "Word wrap" }).click();
  await until(async () => (await viewLines()) > 1, "wrapped view lines");
  const reopened = await openMoreMenu(page);
  assert.equal(
    await reopened
      .getByRole("menuitemcheckbox", { name: "Word wrap" })
      .getAttribute("aria-checked"),
    "true",
  );
  done();
});

test("FileContent_EditableSource_ShowsSaveStatusEncodingAndFormatsAndSavesFromMore", async (t) => {
  const { page, done } = await open(t, {
    selectedFile: "src/app.ts",
    fileContent: UNFORMATTED,
    editable: CLEAN,
  });
  await page.locator(STATUS_BAR).waitFor();
  assert.deepEqual(
    (await calls(page)).filter((call) => call[0] === "show"),
    [["show", "src/app.ts"]],
  );
  assert.equal(await saveStatusText(page), "Saved");
  assert.deepEqual(await page.locator(`${STATUS_BAR} button`).allTextContents(), [
    "UTF-8",
    "LF",
    "TypeScript",
  ]);

  // Prettier 加载后 Format 可用；Save 在没有修改时不可用。Format is available once Prettier loads; Save is not without
  // changes.
  const formatEnabled = async () => {
    const menu = await openMoreMenu(page);
    const texts = await menuTexts(menu);
    const disabled = await menu
      .getByRole("menuitem", { name: "Format" })
      .getAttribute("aria-disabled");
    const save = await menu.getByRole("menuitem", { name: "Save" }).getAttribute("aria-disabled");
    await closeMenu(page);
    assert.deepEqual(texts, ["Reload file", "Format", "Word wrap", "Save"]);
    assert.equal(save, "true");
    return disabled !== "true";
  };
  await until(formatEnabled, "Format to become available");
  await chooseMoreItem(page, "Format");
  await until(async () => (await modelText(page)) === FORMATTED, "the formatted text");

  await setDocument(page, { status: "dirty", saveStatus: "unsaved", unsaved: true });
  assert.equal(await saveStatusText(page), "Unsaved changes");
  await chooseMoreItem(page, "Save");
  await chooseMoreItem(page, "Reload file");
  assert.deepEqual(
    (await calls(page)).filter((call) => call[0] === "save" || call[0] === "reload"),
    [["save"], ["reload"]],
  );
  done();
});

test("FileContent_EditableSource_SaveShortcutWorksOnlyInsideSource", async (t) => {
  const { page, done } = await open(t, {
    selectedFile: "src/app.ts",
    fileContent: UNFORMATTED,
    editable: { ...CLEAN, status: "dirty", saveStatus: "unsaved", unsaved: true },
  });
  await page.locator(STATUS_BAR).waitFor();
  await page.locator(`${SOURCE} .view-lines`).click();
  await page.keyboard.press("ControlOrMeta+s");
  await until(
    async () => (await calls(page)).some((call) => call[0] === "save"),
    "the save from the shortcut",
  );
  await page.getByText("src/app.ts", { exact: true }).last().click();
  await page.keyboard.press("ControlOrMeta+s");
  await delay(200);
  assert.equal((await calls(page)).filter((call) => call[0] === "save").length, 1);
  done();
});

test("FileContent_EditableSource_EncodingMenuReopensAndSavesWithEncoding", async (t) => {
  const { page, done } = await open(t, {
    selectedFile: "notes.txt",
    fileContent: "Café",
    editable: CLEAN,
  });
  const encoding = page.locator(`${STATUS_BAR} button`, { hasText: "UTF-8" });
  await encoding.click();
  await page
    .locator(`${STATUS_BAR} [role="menuitem"]`, { hasText: "Reopen with Encoding…" })
    .click();
  await page.locator(`${STATUS_BAR} [data-encoding="windows-1252"]`).click();
  await encoding.click();
  await page.locator(`${STATUS_BAR} [role="menuitem"]`, { hasText: "Save with Encoding…" }).click();
  await page.locator(`${STATUS_BAR} [data-encoding="utf-8"][data-bom="true"]`).click();
  assert.deepEqual(
    (await calls(page)).filter((call) => String(call[0]).endsWith("WithEncoding")),
    [
      ["reopenWithEncoding", "windows-1252"],
      ["saveWithEncoding", "utf-8", true],
    ],
  );
  assert.equal(await page.locator(`${STATUS_BAR} [role="menu"]`).count(), 0);
  done();
});

test("FileContent_EditableSource_LoadingAndFailedDocumentsShowPanelsWithoutEditorItems", async (t) => {
  const { page, done } = await open(t, {
    selectedFile: "data.txt",
    fileContent: "",
    editable: { ...CLEAN, status: "loading", saveStatus: null, encodingLabel: null },
  });
  await page.getByText("Loading data.txt…").waitFor();
  assert.equal(await page.locator(STATUS_BAR).count(), 0);
  assert.deepEqual(await menuTexts(await openMoreMenu(page)), ["Reload file"]);
  await closeMenu(page);

  await setDocument(page, {
    status: "failed",
    error: { code: "ERR_NOT_TEXT", message: "The file is not text." },
  });
  await page.getByText("This file is binary, so it can't be edited as text.").waitFor();
  assert.equal(await page.getByRole("button", { name: "Reopen with Encoding…" }).count(), 0);

  await setDocument(page, {
    error: { code: "ERR_UNKNOWN_ENCODING", message: "Unknown encoding." },
  });
  await page.getByText("The encoding of this file couldn't be detected.").waitFor();
  assert.equal(await page.getByText("Could not read data.txt").count(), 1);
  await page.getByRole("button", { name: "Reopen with Encoding…" }).click();
  await page.locator('[data-source-error] [data-encoding="windows-1252"]').click();
  assert.deepEqual((await calls(page)).at(-1), ["reopenWithEncoding", "windows-1252"]);
  done();
});

test("FileContent_EditableSource_ChangedOnDiskReloadsThroughTheDocument", async (t) => {
  const { page, done } = await open(t, {
    selectedFile: "src/app.ts",
    fileContent: UNFORMATTED,
    editable: { ...CLEAN, status: "dirty", saveStatus: "changed-on-disk", unsaved: true },
    viewers: VIEWERS,
    defaultFileViewerId: "source",
  });
  await page.getByRole("button", { name: "Reload", exact: true }).click();
  assert.deepEqual((await calls(page)).at(-1), ["reloadChanged"]);
  // Source 有未保存修改时它的切换按钮前有标记。Source's switch button is marked while it has unsaved changes.
  assert.ok((await tabs(page)).find((tab) => tab.id === "source")!.text.startsWith("●"));
  done();
});

test("FileContent_EditableSource_CommentsFollowEditsAndKeepTheirLineAfterReload", async (t) => {
  const { page, done } = await open(t, {
    selectedFile: "src/app.ts",
    fileContent: SOURCE_FILE,
    editable: CLEAN,
    comments: [
      {
        projectId: "project-1",
        directoryId: "workspace",
        id: "existing",
        side: "current",
        filePath: "src/app.ts",
        lineNumber: 3,
        content: "Existing note",
        timestamp: "2026-10-25T08:00:00.000Z",
      },
    ],
  });
  await page.locator('[data-comment-line="3"]').getByText("Existing note").waitFor();
  await page
    .locator(`${SOURCE} .view-line`)
    .filter({ hasText: /const\sa\s=\s1;/u })
    .click();
  await page.keyboard.press("End");
  await page.keyboard.press("Enter");
  await until(async () => (await comments(page))[0]?.lineNumber === 4, "the comment on line 4");
  await page.locator('[data-comment-line="4"]').getByText("Existing note").waitFor();
  done();
});

test("FileContent_OnlyDiff_IgnoresTheEditableSource", async (t) => {
  const { page, done } = await open(t, {
    selectedFile: "src/calc.ts",
    onlyDiff: true,
    diffScope: "staged",
    editable: CLEAN,
    diff: {
      diff: "@@ -1 +1 @@\n-a\n+b\n",
      unchanged: false,
      isBinary: false,
      originalContent: "a\n",
      modifiedContent: "b\n",
    },
  });
  await page.locator(".line-insert").first().waitFor({ state: "attached" });
  assert.equal(await page.getByRole("button", { name: "More" }).count(), 0);
  assert.deepEqual(
    (await calls(page)).filter((call) => call[0] === "show"),
    [],
  );
  done();
});
