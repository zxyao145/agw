// code-view 在 Chromium 中的行为：外观、状态栏、可编辑模型、Format、行区域、键盘隔离、行操作与 diff 对齐（设计文档测试计划）。
// code-view behavior in Chromium: appearance, the status bar, the editable model, Format, line zones, key isolation,
// the line action and diff alignment (design test plan).
import assert from "node:assert/strict";
import { resolve } from "node:path";
import { after, before, test, type TestContext } from "node:test";
import { setTimeout as delay } from "node:timers/promises";

import { type BrowserFixture, startBrowserFixture } from "@agw/test-harness/browser";
import type { Page } from "playwright";

import type { FixtureState } from "./fixture/main";

const WORKER_FALLBACK = "Could not create web worker";
const SOURCE = '[data-code-view="source"]';
const STATUS_BAR = "[data-code-view-status-bar]";
let fixture: BrowserFixture;

before(async () => {
  fixture = await startBrowserFixture({
    root: resolve("browser-tests/fixture"),
    outDir: resolve(".scratch/code-view-browser"),
  });
});

after(async () => {
  await fixture?.close();
});

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

async function open(
  t: TestContext,
  update: Partial<FixtureState> = {},
  text?: { text: string; eol: "\n" | "\r\n"; name: string },
) {
  const { page, errors, console } = await fixture.newPage();
  const workers: string[] = [];
  page.on("worker", (worker) => workers.push(worker.url()));
  t.after(async () => {
    await page.close();
  });
  await page.goto(fixture.origin);
  await set(page, update);
  if (text !== undefined) await load(page, text.text, text.eol, text.name);
  await page.locator(".monaco-editor").first().waitFor({ state: "attached" });
  const done = () => {
    assert.deepEqual(errors, []);
    assert.deepEqual(
      console.filter((message) => message.includes(WORKER_FALLBACK)),
      [],
    );
  };
  return { page, workers, done };
}

function set(page: Page, update: Partial<FixtureState>) {
  return page.evaluate((value) => window.fixture.set(value), update);
}

/** 像宿主的文档会话一样读入文字。Loads text the way the host's document session does. */
function load(page: Page, text: string, eol: "\n" | "\r\n", name: string) {
  return page.evaluate(({ text, eol, name }) => window.fixture.text.load(text, eol, name), {
    text,
    eol,
    name,
  });
}

function modelValue(page: Page) {
  return page.evaluate(() => window.fixture.text.value());
}

function calls(page: Page) {
  return page.evaluate(() => window.fixture.calls);
}

function statusText(page: Page) {
  return page.locator(STATUS_BAR).evaluate((element) => element.textContent ?? "");
}

/** 浏览器把颜色画到画布上得到的 rgb。The rgb the browser paints for a color. */
function renderedColor(page: Page, scope: string, variable: string) {
  return page.evaluate(
    ({ scope, variable }) => {
      const value = getComputedStyle(document.querySelector(scope)!).getPropertyValue(variable);
      const context = document.createElement("canvas").getContext("2d")!;
      context.fillStyle = value;
      context.fillRect(0, 0, 1, 1);
      const [red, green, blue] = context.getImageData(0, 0, 1, 1).data;
      return `rgb(${red}, ${green}, ${blue})`;
    },
    { scope, variable },
  );
}

function editorBackground(page: Page) {
  return page
    .locator(".monaco-editor-background")
    .first()
    .evaluate((element) => getComputedStyle(element).backgroundColor);
}

/** 行号元素的位置；`editor` 是编辑器的选择器。The box of a line number in the editor `editor`. */
function lineBox(page: Page, editor: string, line: number) {
  return page.evaluate(
    ({ editor, line }) => {
      const element = [...document.querySelectorAll(`${editor} .line-numbers`)].find(
        (item) => item.textContent === String(line),
      );
      if (!element) return null;
      const rect = element.getBoundingClientRect();
      return { top: rect.top, bottom: rect.bottom };
    },
    { editor, line },
  );
}

function zoneBox(page: Page, zone: string) {
  return page.evaluate((zone) => {
    const frame = document.querySelector(`[data-zone="${zone}"]`)?.closest(".agw-code-view-zone");
    if (!frame) return null;
    const rect = frame.getBoundingClientRect();
    return { top: rect.top, bottom: rect.bottom, left: rect.left, height: rect.height };
  }, zone);
}

/** 行区域位于 `line` 与下一行之间。The zone sits between `line` and the next line. */
async function zoneBetween(page: Page, editor: string, zone: string, line: number) {
  const box = await zoneBox(page, zone);
  const above = await lineBox(page, editor, line);
  const below = await lineBox(page, editor, line + 1);
  return box && above && below && near(box.top, above.bottom) && near(below.top, box.bottom)
    ? box
    : null;
}

/** 点击编辑器中文字为 `text` 的一行的末尾。Clicks at the end of the editor line showing `text`. */
async function clickLineEnd(page: Page, text: RegExp) {
  await page.locator(`${SOURCE} .view-line`).filter({ hasText: text }).click();
  await page.keyboard.press("End");
}

const near = (left: number, right: number) => Math.abs(left - right) <= 1;

test("CodeViewer_HostColors_FollowOklchColorMixCardAndDarkMode", async (t) => {
  const { page, done } = await open(t);
  await until(
    async () =>
      (await editorBackground(page)) === (await renderedColor(page, "[data-host]", "--background")),
    "the oklch background",
  );

  await set(page, { card: true, revision: 1 });
  assert.equal(await renderedColor(page, "[data-host]", "--background"), "rgb(255, 204, 204)");
  await until(
    async () => (await editorBackground(page)) === "rgb(255, 204, 204)",
    "the card background",
  );

  await page.evaluate(() => document.documentElement.classList.add("dark"));
  await set(page, { card: false, revision: 2 });
  await until(
    async () =>
      (await editorBackground(page)) === (await renderedColor(page, "[data-host]", "--background")),
    "the dark background",
  );
  assert.equal(await page.locator(".monaco-editor.vs-dark").count(), 1);
  done();
});

test("CodeViewer_FontSize_FollowsTheCodeSizeVariableWithMonacoLineHeight", async (t) => {
  const { page, done } = await open(t);
  // Monaco 的默认行高：macOS 为字号的 1.5 倍，其他平台为 1.35 倍。Monaco's default line height: 1.5 times the size on
  // macOS, 1.35 times elsewhere.
  const metrics = () =>
    page.evaluate(() => {
      const line = document.querySelector<HTMLElement>(".view-line")!;
      const ratio = navigator.userAgent.includes("Macintosh") ? 1.5 : 1.35;
      const fontSize = Number.parseFloat(getComputedStyle(line).fontSize);
      return {
        fontSize,
        matchesDefault: line.getBoundingClientRect().height === Math.round(fontSize * ratio),
      };
    });
  // 没有变量时为 0.875rem。Without the variable the size is 0.875rem.
  await until(async () => (await metrics()).fontSize === 14, "14px text");
  assert.deepEqual(await metrics(), { fontSize: 14, matchesDefault: true });

  await page.evaluate(() =>
    document.documentElement.style.setProperty("--agw-font-size-code", "18px"),
  );
  await set(page, { revision: 1 });
  await until(async () => (await metrics()).fontSize === 18, "18px text");
  assert.deepEqual(await metrics(), { fontSize: 18, matchesDefault: true });
  done();
});

test("CodeViewer_ReadOnly_StatusBarShowsTextLineBreakAndIgnoresTyping", async (t) => {
  const { page, done } = await open(t, { filePath: "src/sample.ts" });
  await page.locator(STATUS_BAR).waitFor();
  const status = await statusText(page);
  assert.ok(status.includes("Ln 1, Col 1") && status.includes("Spaces: 4"), status);
  assert.equal(await page.locator(`${STATUS_BAR} [data-encoding-slot]`).count(), 0);
  // 换行是文字，语言是按钮。The line break is text and the language a button.
  assert.deepEqual(await page.locator(`${STATUS_BAR} button`).allTextContents(), ["TypeScript"]);
  assert.ok(status.includes("LF"), status);

  await clickLineEnd(page, /value1\s=\s1;/u);
  await page.keyboard.type("typed");
  await until(async () => (await statusText(page)).includes("Ln 1, Col 25"), "the cursor");
  assert.equal(await page.locator(`${SOURCE} .view-line`).filter({ hasText: "typed" }).count(), 0);
  done();
});

test("CodeViewer_WordWrap_WrapsLongLinesWhenOn", async (t) => {
  const longLine = `const text = "${"word ".repeat(120)}";`;
  const { page, done } = await open(t, { text: `${longLine}\nconst next = 1;`, width: 600 });
  const viewLines = () => page.locator(`${SOURCE} .view-line`).count();
  await until(async () => (await viewLines()) === 2, "two view lines without wrapping");
  await set(page, { wordWrap: true });
  await until(async () => (await viewLines()) > 3, "wrapped view lines");
  done();
});

test("CodeViewer_Editable_TypingMovesZonesWithTheirStateAndUndoReturnsToTheSnapshot", async (t) => {
  const text = Array.from({ length: 30 }, (_, index) => `line ${index + 1}`).join("\n");
  const { page, done } = await open(t, { zones: [5] }, { text, eol: "\n", name: "notes.txt" });
  const zone = page.getByLabel("note current zone:0");
  await zone.fill("draft");
  await until(() => zoneBetween(page, SOURCE, "current:5", 5), "the zone below line 5");
  const snapshot = await page.evaluate(() => window.fixture.text.snapshot());

  // Monaco 把空格显示为不换行空格。Monaco shows spaces as non-breaking spaces.
  await clickLineEnd(page, /^line\s1$/u);
  await page.keyboard.press("Enter");
  await until(() => zoneBetween(page, SOURCE, "current:6", 6), "the zone below line 6");
  assert.deepEqual(
    (await calls(page)).find((call) => call[0] === "lines"),
    ["lines", [{ startLineNumber: 1, endLineNumber: 1, insertedLineCount: 1 }]],
  );
  // 同一个 key 的区域没有重新挂载，输入中的文字还在。The zone kept its key, so the draft is still there.
  assert.equal(await page.getByLabel("note current zone:0").inputValue(), "draft");
  assert.equal(await page.evaluate((value) => window.fixture.text.matches(value), snapshot), false);

  await page.locator(`${SOURCE} .view-lines`).click();
  await page.keyboard.press("ControlOrMeta+z");
  await until(
    () => page.evaluate((value) => window.fixture.text.matches(value), snapshot),
    "undo back to the snapshot",
  );
  assert.equal(await modelValue(page), text);
  done();
});

test("CodeViewer_Editable_StatusBarSwitchesLineBreakAndLanguageAndShowsTheEncoding", async (t) => {
  const { page, done } = await open(t, {}, { text: "a\r\nb\r\n", eol: "\r\n", name: "notes.txt" });
  await page.locator(STATUS_BAR).waitFor();
  assert.equal(await page.locator(`${STATUS_BAR} [data-encoding-slot]`).textContent(), "UTF-8");
  const button = (name: string) =>
    page.locator(`${STATUS_BAR} button`).filter({ hasText: new RegExp(`^${name}$`, "u") });

  await button("CRLF").click();
  await button("LF").waitFor();
  assert.equal(await page.evaluate(() => window.fixture.text.eol()), "\n");

  await button("Plain Text").click();
  const python = page.locator(`${STATUS_BAR} [role="menu"] [data-language="python"]`);
  await python.click();
  await button("Python").waitFor();
  assert.equal(await page.locator(`${STATUS_BAR} [role="menu"]`).count(), 0);
  done();
});

test("CodeViewer_Editable_FormatsTypeScriptWithPrettierAndJsonWithItsServiceAndReportsErrors", async (t) => {
  const { page, done } = await open(
    t,
    {},
    { text: "function add(a:number,b:number){\nreturn a+b}\n", eol: "\n", name: "app.ts" },
  );
  const formatAvailable = async () =>
    (await calls(page)).some((call) => call[0] === "format" && call[1] === true) &&
    (await page.evaluate(() => window.fixture.format !== null));
  await until(formatAvailable, "Prettier formatting");
  await page.evaluate(() => window.fixture.format!());
  await until(
    async () =>
      (await modelValue(page)) === "function add(a: number, b: number) {\n    return a + b;\n}\n",
    "the Prettier output",
  );

  await page.evaluate(() => (window.fixture.calls = []));
  await load(page, '{"a":1}', "\n", "config.json");
  await until(formatAvailable, "JSON formatting");
  await page.evaluate(() => window.fixture.format!());
  await until(async () => (await modelValue(page)) === '{\n    "a": 1\n}', "the JSON output");

  await page.evaluate(() => (window.fixture.calls = []));
  await load(page, "const = 1;\n", "\n", "bad.ts");
  await until(formatAvailable, "Prettier formatting of bad.ts");
  await page.evaluate(() => window.fixture.format!());
  const error = await until(
    async () => (await calls(page)).find((call) => call[0] === "error") ?? null,
    "the Prettier error",
  );
  assert.match(String(error![1]), /^Could not format bad\.ts: /u);
  assert.equal(await modelValue(page), "const = 1;\n");
  done();
});

test("CodeViewer_Editable_JsonValidationMarksInvalidEditsWithTheJsonWorker", async (t) => {
  const { page, workers, done } = await open(
    t,
    {},
    { text: '{\n  // note\n  "port": 8080,\n}\n', eol: "\n", name: "config.json" },
  );
  const squiggles = () => page.locator(`${SOURCE} [class*="squiggly-"]`).count();
  await delay(1500);
  assert.equal(await squiggles(), 0);
  await page.locator(`${SOURCE} .view-line`).first().click();
  await page.keyboard.press("Home");
  await page.keyboard.type("x");
  await until(async () => (await squiggles()) > 0, "an error mark for invalid JSON");
  assert.ok(
    workers.some((url) => /json\.worker/u.test(url)),
    JSON.stringify(workers),
  );
  done();
});

test("CodeViewer_Editable_HiddenUntilShownThenFocused", async (t) => {
  const { page, done } = await open(
    t,
    { shown: false },
    { text: "hello", eol: "\n", name: "a.txt" },
  );
  assert.equal(await page.locator(SOURCE).isHidden(), true);
  assert.equal(await page.locator(STATUS_BAR).count(), 0);
  await set(page, { shown: true });
  await page.locator(STATUS_BAR).waitFor();
  await until(
    () =>
      page.evaluate(
        (source) => Boolean(document.querySelector(source)?.contains(document.activeElement)),
        SOURCE,
      ),
    "the focused editor",
  );
  done();
});

test("CodeViewer_LineZones_SitBelowTheirLineAndFollowContentHeight", async (t) => {
  const { page, done } = await open(t, { zones: [3] });
  await until(() => zoneBox(page, "current:3"), "the zone");
  const before = (await until(
    () => zoneBetween(page, SOURCE, "current:3", 3),
    "the zone between lines 3 and 4",
  ))!;
  const contentLeft = await page.evaluate(
    () => document.querySelector(".view-lines")!.getBoundingClientRect().left,
  );
  assert.ok(near(before.left, contentLeft), JSON.stringify({ before, contentLeft }));
  assert.ok((await page.locator(".agw-code-view-marked-line").count()) > 0);

  await set(page, { zoneHeight: 120 });
  const grown = (await until(async () => {
    const zone = await zoneBetween(page, SOURCE, "current:3", 3);
    return zone && zone.height > before.height + 100 ? zone : null;
  }, "the taller zone"))!;
  assert.ok(near(grown.height, before.height + 120), JSON.stringify({ before, grown }));
  done();
});

test("CodeViewer_ZoneTextarea_KeepsKeysAndMouseFromMonaco", async (t) => {
  const { page, done } = await open(t, { zones: [3] });
  const textarea = page.getByLabel("note current zone:0");
  await textarea.click();
  assert.equal(await textarea.evaluate((element) => element === document.activeElement), true);

  await page.keyboard.press("ControlOrMeta+f");
  await delay(200);
  assert.equal(await page.locator(".find-widget.visible").count(), 0);
  await page.keyboard.type("x");
  assert.equal(await textarea.inputValue(), "x");
  assert.equal(await page.locator(".monaco-editor-overlaymessage").count(), 0);
  await page.keyboard.press("Escape");
  await page.keyboard.press("Control+Enter");
  const keys = (await calls(page)).filter((call) => call[0] === "key").map((call) => call[3]);
  assert.ok(keys.includes("Escape") && keys.includes("Control+Enter"), JSON.stringify(keys));

  await textarea.click({ button: "right" });
  await delay(200);
  assert.equal(await page.locator(".monaco-menu").count(), 0);
  done();
});

test("CodeViewer_LineAction_ShowsOnRunnableLinesAndRunsFromButtonAndMenu", async (t) => {
  const { page, done } = await open(t, { blocked: [2] });
  const lineNumber = (line: number) =>
    page.locator(`${SOURCE} .line-numbers`).getByText(String(line), { exact: true });

  // 编辑器没有 glyph margin；按钮在行号列左侧。The editor has no glyph margin; the button sits at the left of the
  // line-number column.
  assert.equal(
    await page.locator(`${SOURCE} .glyph-margin`).evaluate((element) => element.clientWidth),
    0,
  );
  await lineNumber(5).hover();
  const button = page.locator(
    '.agw-code-view-line-action[aria-label="Add note, line 5"]:not(.is-hidden)',
  );
  await button.waitFor();
  const [buttonBox, numberBox] = await Promise.all([
    button.boundingBox(),
    page.locator(`${SOURCE} .margin`).boundingBox(),
  ]);
  assert.ok(
    buttonBox && numberBox && near(buttonBox.x, numberBox.x + 2),
    JSON.stringify({ buttonBox, numberBox }),
  );
  await button.click();
  assert.deepEqual((await calls(page)).at(-1), ["run", "current", 5]);

  await lineNumber(2).hover();
  await until(
    async () => (await page.locator(".agw-code-view-line-action:not(.is-hidden)").count()) === 0,
    "the button to hide on a blocked line",
  );

  // `.view-line` 的 DOM 顺序不一定是行的顺序，按文字找第 6 行。The DOM order of `.view-line` need not follow the
  // lines, so line 6 is found by its text.
  await page
    .locator(`${SOURCE} .view-line`)
    .filter({ hasText: /value6\s=\s6;/u })
    .click({ button: "right" });
  const addNote = page.locator(".monaco-menu").getByText("Add note");
  await addNote.waitFor();
  // Monaco 在菜单出现后稍等才接受鼠标松开，避免误点。Monaco accepts mouse-up shortly after the menu appears.
  await delay(300);
  await addNote.click();
  await until(async () => (await calls(page)).length === 2, "the menu action");
  assert.deepEqual((await calls(page)).at(-1), ["run", "current", 6]);
  done();
});

test("CodeViewer_ContextMenuNearBottomOfClippedCard_IsFullyVisible", async (t) => {
  const { page, done } = await open(t, { card: true, revision: 1 });
  const lines = page.locator(`${SOURCE} .view-line`);
  await lines.last().click({ button: "right" });
  const copy = page.locator(".monaco-menu").getByText("Copy", { exact: true });
  await copy.click({ trial: true });
  const box = await copy.boundingBox();
  assert.ok(box && box.y + box.height <= 800, JSON.stringify(box));
  done();
});

test("CodeViewer_AutofocusedZone_DoesNotScrollTheEditor", async (t) => {
  const { page, done } = await open(t, { zones: [3], autoFocusZone: 3 });
  const textarea = page.getByLabel("note current zone:0");
  await until(() => textarea.evaluate((element) => element === document.activeElement), "focus");
  const scroll = await page.evaluate(() => ({
    guard: document.querySelector(".overflow-guard")!.scrollTop,
    first: document.querySelector(".line-numbers")!.textContent,
  }));
  assert.deepEqual(scroll, { guard: 0, first: "1" });
  done();
});

test("CodeDiffViewer_ZonesOnEitherSide_AlignTheFollowingLines", async (t) => {
  const { page, workers, done } = await open(t, { mode: "diff", zones: [22], zoneHeight: 60 });
  const original = '[data-code-view="diff"] .editor.original';
  const modified = '[data-code-view="diff"] .editor.modified';
  await page.locator(".line-insert").first().waitFor({ state: "attached" });
  assert.ok(workers.length > 0);
  assert.equal(await page.locator('[data-diff-side="original"]').textContent(), "Before");
  assert.equal(await page.locator('[data-diff-side="modified"]').textContent(), "After");

  // 修改侧第 22 行之后的区域：修改侧第 23 行与原始侧第 22 行对齐。A zone after modified line 22 aligns modified
  // line 23 with original line 22.
  await until(async () => {
    const left = await lineBox(page, original, 22);
    const right = await lineBox(page, modified, 23);
    const zone = await zoneBox(page, "modified:22");
    return left && right && zone && zone.height > 60 && near(left.top, right.top);
  }, "the aligned lines after the modified zone");

  await set(page, { originalZones: [19] });
  await until(async () => {
    const left = await lineBox(page, original, 20);
    const right = await lineBox(page, modified, 20);
    const zone = await zoneBox(page, "original:19");
    return left && right && zone && near(left.top, right.top) && near(zone.bottom, left.top);
  }, "the aligned lines after the original zone");
  done();
});

test("CodeDiffViewer_NarrowWidth_KeepsBothSides", async (t) => {
  const { page, done } = await open(t, { mode: "diff", width: 500 });
  await page.locator(".line-insert").first().waitFor({ state: "attached" });
  const widths = await page.evaluate(() =>
    [".editor.original", ".editor.modified"].map(
      (selector) => document.querySelector(selector)?.getBoundingClientRect().width ?? 0,
    ),
  );
  assert.ok(
    widths.every((width) => width > 100),
    JSON.stringify(widths),
  );
  done();
});
