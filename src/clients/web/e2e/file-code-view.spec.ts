import { expect, test, type Locator, type Page } from "@playwright/test";

import { mockChatApi, projectId } from "./chat-api";
import { DIFF_MODIFIED, DIFF_ORIGINAL, mockFilesApi, SOURCE_TEXT } from "./files-api";
import {
  fileProjectTest,
  GUIDE_FILE,
  NOTES_FILE,
  STAGED_FILE,
  WORKING_FILE,
} from "./standalone-file-project";
import { fetchFromStandaloneHost, readApiResult } from "./standalone-host";

const SOURCE = '[data-code-view="source"]';
const DIFF = '[data-code-view="diff"]';
const WORKER_FALLBACK = "Could not create web worker";

// Files 视图使用桌面宽度的左右布局，手机宽度的项目跳过。The Files view uses the side-by-side layout of desktop
// widths, so the phone-width project is skipped.
test.skip(({ viewport }) => (viewport?.width ?? 0) < 1024, "Files uses the desktop layout");

/** 收集控制台信息，检查 Monaco 没有退回主线程运行 worker 代码。Collects console text to check Monaco kept its workers. */
function consoleMessages(page: Page): string[] {
  const messages: string[] = [];
  page.on("console", (message) => messages.push(message.text()));
  return messages;
}

async function openFiles(page: Page) {
  await mockChatApi(page);
  await mockFilesApi(page);
  await page.goto(`/chat/?projectId=${projectId}`);
  await page.getByRole("tab", { name: "Files" }).click();
  await expect(page.getByText("app.ts", { exact: true }).first()).toBeVisible();
}

/** Monaco 是否已经加载：环境对象与 codicon 字体样式。Whether Monaco has loaded: its environment and the codicon styles. */
function monacoLoaded(page: Page) {
  return page.evaluate(() => ({
    environment: "MonacoEnvironment" in window,
    styles: [...document.styleSheets].some((sheet) =>
      [...sheet.cssRules].some((rule) => rule.cssText.includes("codicon")),
    ),
  }));
}

/** 编辑器显示的文字，按行的位置排列，Monaco 的不换行空格还原为空格。The editor text in line order. */
function editorText(editor: Locator) {
  return editor.evaluate((root) =>
    [...root.querySelectorAll<HTMLElement>(".view-line")]
      .map(
        (line) =>
          [Number.parseFloat(line.style.top), line.textContent!.replace(/ /gu, " ")] as const,
      )
      .sort((left, right) => left[0] - right[0])
      .map((entry) => entry[1])
      .join("\n"),
  );
}

async function expectReadOnlySource(page: Page, source: Locator, text: string) {
  await source.locator(".view-lines").click();
  await expect
    .poll(() => source.evaluate((element) => element.contains(document.activeElement)))
    .toBe(true);
  await page.keyboard.type("typed");
  expect(await editorText(source)).toBe(text);
}

/** 鼠标移到行号上，点击行号旁出现的 "Add comment"。Hovers a line number and clicks the "Add comment" beside it. */
async function addCommentOn(editor: Locator, line: number) {
  await editor.locator(".line-numbers", { hasText: new RegExp(`^${line}$`, "u") }).hover();
  await editor
    .locator(".agw-code-view-line-action:not(.is-hidden)")
    .and(editor.locator(`[aria-label="Add comment, line ${line}"]`))
    .click();
}

test("Source loads Monaco on demand and shows the file read-only with highlighting", async ({
  page,
}) => {
  const messages = consoleMessages(page);
  await openFiles(page);
  expect(await monacoLoaded(page)).toEqual({ environment: false, styles: false });

  await page.getByText("app.ts", { exact: true }).first().click();
  const source = page.locator(SOURCE);
  await expect(source.locator(".monaco-editor")).toBeVisible();
  await expect.poll(() => editorText(source)).toBe(SOURCE_TEXT);
  expect(await monacoLoaded(page)).toEqual({ environment: true, styles: true });
  // TypeScript 语法着色：除默认颜色外还有其他 token 颜色。TypeScript highlighting uses token colors beyond the default.
  await expect
    .poll(() => source.locator('.view-line span[class*="mtk"]:not(.mtk1)').count())
    .toBeGreaterThan(0);

  await expectReadOnlySource(page, source, SOURCE_TEXT);
  expect(messages.filter((message) => message.includes(WORKER_FALLBACK))).toEqual([]);

  // 状态栏与 Desktop 相同，只读时没有编码，换行为文字。The status bar matches Desktop; read-only has no encoding and shows
  // the line break as text.
  const statusBar = page.locator("[data-code-view-status-bar]");
  await expect(statusBar).toContainText("Spaces: 2");
  await expect(statusBar).toContainText("LF");
  await expect(statusBar.locator("button")).toHaveText(["TypeScript"]);
});

test("Source comments are added beside line numbers and keep keys away from Monaco", async ({
  page,
}) => {
  await openFiles(page);
  await page.getByText("app.ts", { exact: true }).first().click();
  const source = page.locator(SOURCE);
  await expect.poll(() => editorText(source)).toBe(SOURCE_TEXT);

  await addCommentOn(source, 2);
  const input = page.getByPlaceholder("Write a comment...");
  await expect(input).toBeFocused();
  // 评论框中的 Ctrl/Cmd+F 不打开编辑器的查找。Ctrl/Cmd+F in the comment box does not open the editor's find.
  await page.keyboard.press("ControlOrMeta+f");
  await expect(source.locator(".find-widget.visible")).toHaveCount(0);
  await input.fill("Check the greeting");
  await page.keyboard.press("Control+Enter");
  const zone = page.locator('[data-comment-line="2"]');
  await expect(zone.getByText("Check the greeting")).toBeVisible();
  await expect(zone.getByPlaceholder("Write a comment...")).toHaveCount(0);
  await expect(source.locator(".agw-code-view-marked-line").first()).toBeAttached();

  // 有评论的行不再提供 "Add comment"；Esc 关闭新评论框。A commented line offers no "Add comment"; Esc closes a new box.
  await source.locator(".line-numbers", { hasText: /^2$/u }).hover();
  await expect(source.locator('[aria-label="Add comment, line 2"]:not(.is-hidden)')).toHaveCount(0);
  await addCommentOn(source, 1);
  await expect(page.getByPlaceholder("Write a comment...")).toBeFocused();
  await page.keyboard.press("Escape");
  await expect(page.locator('[data-comment-line="1"]')).toHaveCount(0);
  await expect(zone.getByText("Check the greeting")).toBeVisible();
});

test("ChatFilePane_DiffMode_RendersBothSidesWithMonacoWorker", async ({ page }) => {
  const messages = consoleMessages(page);
  const workers: string[] = [];
  page.on("worker", (worker) => workers.push(worker.url()));
  await openFiles(page);
  await page.getByRole("switch", { name: "Diff" }).click();
  await changedFile(page, "Unstaged", "calc.ts").click();

  const diff = page.locator(DIFF);
  await expect(diff.locator(".monaco-diff-editor")).toBeVisible();
  await expect(page.locator('[data-diff-side="original"]')).toHaveText("Staged");
  await expect(page.locator('[data-diff-side="modified"]')).toHaveText("Working Tree");
  await expect(diff.locator(".line-insert").first()).toBeAttached();
  await expect(diff.locator(".line-delete").first()).toBeAttached();
  await expect.poll(() => editorText(diff.locator(".editor.original"))).toBe(DIFF_ORIGINAL);
  await expect.poll(() => editorText(diff.locator(".editor.modified"))).toBe(DIFF_MODIFIED);
  expect(workers.length).toBeGreaterThan(0);

  // 修改侧的评论。A comment on the modified side.
  await addCommentOn(diff.locator(".editor.modified"), 4);
  const input = page.getByPlaceholder("Write a comment for modified...");
  await expect(input).toBeFocused();
  await input.fill("New constant");
  await page.keyboard.press("Control+Enter");
  await expect(page.locator('[data-comment-line="4"]').getByText("New constant")).toBeVisible();
  expect(messages.filter((message) => message.includes(WORKER_FALLBACK))).toEqual([]);
});

async function openProjectDiff(page: Page, id: string) {
  await page.goto(`/chat/?projectId=${id}`, { waitUntil: "domcontentloaded" });
  await page.getByRole("tab", { name: "Files" }).click();
  await page.getByRole("switch", { name: "Diff" }).click();
}

function changedFile(page: Page, group: "Staged" | "Unstaged", name: string) {
  return page
    .getByRole("button", { name: new RegExp(`^${group} `) })
    .locator("..")
    .getByText(name, { exact: true });
}

function observeFileReads(page: Page) {
  const reads: URL[] = [];
  page.on("request", (request) => {
    const url = new URL(request.url());
    if (url.pathname === "/api/files/read" || url.pathname === "/api/files/diff") reads.push(url);
  });
  return reads;
}

function viewFile(page: Page) {
  return page.getByRole("button", { name: "View file", exact: true }).click();
}

function viewChanges(page: Page) {
  return page.getByRole("button", { name: "View changes", exact: true }).click();
}

function expectModifiedText(page: Page, text: string) {
  return expect.poll(() => editorText(page.locator(DIFF).locator(".editor.modified"))).toBe(text);
}

fileProjectTest(
  "ChatFilePane_DiffMode_ShowsWorkingFileAndReadsEachScopeOnce",
  async ({ page, fileProject }) => {
    const reads = observeFileReads(page);
    await openProjectDiff(page, fileProject.id);
    const staged = changedFile(page, "Staged", "calc.ts");
    const unstaged = changedFile(page, "Unstaged", "calc.ts");
    const [response] = await Promise.all([
      page.waitForResponse((response) => {
        const url = new URL(response.url());
        return (
          url.pathname === "/api/files/diff" &&
          url.searchParams.get("path") === "calc.ts" &&
          url.searchParams.get("scope") === "staged"
        );
      }),
      staged.click(),
    ]);
    expect(await readApiResult<{ modifiedContent: string }>(response)).toMatchObject({
      modifiedContent: STAGED_FILE,
    });
    await expectModifiedText(page, STAGED_FILE);
    expect(reads.map((url) => url.searchParams.get("scope"))).toEqual(["staged"]);

    await viewFile(page);
    const source = page.locator(SOURCE);
    await expect.poll(() => editorText(source)).toBe(WORKING_FILE);
    await expectReadOnlySource(page, source, WORKING_FILE);
    await expect(page.locator(DIFF)).toHaveCount(0);
    await staged.click();
    await expect(page.getByRole("button", { name: "View changes" })).toBeVisible();

    await unstaged.click();
    await expectModifiedText(page, WORKING_FILE);
    await viewFile(page);
    await expect.poll(() => editorText(source)).toBe(WORKING_FILE);
    await page.getByRole("button", { name: "Stage file calc.ts", exact: true }).click();
    await expect(unstaged).toHaveCount(0);
    await expect(page.getByRole("button", { name: "View changes" })).toBeVisible();

    await viewChanges(page);
    await expect(page.locator('[data-diff-side="modified"]')).toHaveText("Staged");
    await expectModifiedText(page, WORKING_FILE);
    await expect(source).toHaveCount(0);
    expect(reads.map((url) => [url.pathname, url.searchParams.get("scope")])).toEqual([
      ["/api/files/diff", "staged"],
      ["/api/files/read", null],
      ["/api/files/diff", "unstaged"],
      ["/api/files/read", null],
      ["/api/files/read", null],
      ["/api/files/diff", "staged"],
    ]);
  },
);

fileProjectTest(
  "ChatFilePane_SelectionChangesInDiffMode_LoadsTheSelectedFile",
  async ({ page, fileProject }) => {
    const reads = observeFileReads(page);
    await openProjectDiff(page, fileProject.id);
    await changedFile(page, "Unstaged", "calc.ts").click();
    await expectModifiedText(page, WORKING_FILE);
    await viewFile(page);
    await expect(page.locator(SOURCE)).toBeVisible();
    await changedFile(page, "Unstaged", "guide.ts").click();
    await expectModifiedText(page, GUIDE_FILE);

    const diffSwitch = page.getByRole("switch", { name: "Diff" });
    await viewFile(page);
    await expect(page.locator(SOURCE)).toBeVisible();
    await diffSwitch.click();
    await expect(page.getByRole("button", { name: "View changes" })).toHaveCount(0);
    await diffSwitch.click();
    await expectModifiedText(page, GUIDE_FILE);

    await viewFile(page);
    await expect(page.locator(SOURCE)).toBeVisible();
    await page.getByRole("combobox", { name: "Project directory" }).click();
    await page.getByRole("option", { name: /^extra /u }).click();
    await expect(page.getByText("Select a file to view its contents")).toBeVisible();
    await changedFile(page, "Unstaged", "notes.ts").click();
    await expectModifiedText(page, NOTES_FILE);
    expect(
      reads
        .filter((url) => url.searchParams.get("directoryId") === fileProject.extraDirectoryId)
        .map((url) => url.searchParams.get("path")),
    ).toEqual(["notes.ts"]);
  },
);

fileProjectTest(
  "ChatFilePane_DelayedSourceError_KeepsTheLatestDiff",
  async ({ page, fileProject }) => {
    await openProjectDiff(page, fileProject.id);
    await changedFile(page, "Unstaged", "deleted.ts").click();
    const diff = page.locator(DIFF).locator(".monaco-diff-editor");
    await expect(diff).toBeVisible();

    const readHeld = Promise.withResolvers<void>();
    const releaseRead = Promise.withResolvers<void>();
    const isSourceRead = (url: URL) => url.pathname === "/api/files/read";
    await page.route(
      isSourceRead,
      async (route) => {
        const response = await fetchFromStandaloneHost(route, fileProject.origin);
        readHeld.resolve();
        await releaseRead.promise;
        await route.fulfill({ response });
      },
      { times: 1 },
    );

    try {
      await viewFile(page);
      await readHeld.promise;
      await viewChanges(page);
      await expect(diff).toBeVisible();
      const readFinished = page.waitForEvent("requestfinished", (request) =>
        isSourceRead(new URL(request.url())),
      );
      releaseRead.resolve();
      await readFinished;
    } finally {
      releaseRead.resolve();
    }
    await page.evaluate(
      () =>
        new Promise<void>((done) =>
          requestAnimationFrame(() => requestAnimationFrame(() => done())),
        ),
    );
    await expect(diff).toBeVisible();
    await expect(page.getByText(/^Error loading file:/u)).toHaveCount(0);

    await viewFile(page);
    await expect(page.getByText(/^Error loading file:/u)).toBeVisible();
    await viewChanges(page);
    await expect(diff).toBeVisible();
  },
);

test("the editor follows the dark mode", async ({ page }) => {
  await page.emulateMedia({ colorScheme: "light" });
  await openFiles(page);
  await page.getByText("app.ts", { exact: true }).first().click();
  const background = page.locator(`${SOURCE} .monaco-editor-background`);
  await expect(background).toBeVisible();
  const light = await background.evaluate((element) => getComputedStyle(element).backgroundColor);

  await page.emulateMedia({ colorScheme: "dark" });
  await expect(page.locator("html")).toHaveClass(/dark/u);
  await expect
    .poll(() => background.evaluate((element) => getComputedStyle(element).backgroundColor))
    .not.toBe(light);
  // 编辑器背景与宿主的 --background 相同。The editor background equals the host's --background.
  const host = await page.locator(SOURCE).evaluate((element) => {
    const probe = document.createElement("div");
    probe.style.backgroundColor = "var(--background)";
    element.append(probe);
    const color = getComputedStyle(probe).backgroundColor;
    probe.remove();
    return color;
  });
  await expect
    .poll(() => background.evaluate((element) => getComputedStyle(element).backgroundColor))
    .toBe(host);
});
