import { expect, test, type Locator, type Page } from "@playwright/test";

import { mockChatApi, projectId } from "./chat-api";
import { DIFF_MODIFIED, DIFF_ORIGINAL, mockFilesApi, SOURCE_TEXT } from "./files-api";

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

  await source.locator(".view-lines").click();
  await page.keyboard.type("typed");
  expect(await editorText(source)).toBe(SOURCE_TEXT);
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

test("Only diff shows both sides in Monaco's diff editor with its worker", async ({ page }) => {
  const messages = consoleMessages(page);
  const workers: string[] = [];
  page.on("worker", (worker) => workers.push(worker.url()));
  await openFiles(page);
  await page.getByRole("switch").click();
  await page.getByText("calc.ts", { exact: true }).first().click();

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
