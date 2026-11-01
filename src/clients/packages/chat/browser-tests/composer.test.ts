import assert from "node:assert/strict";
import { resolve } from "node:path";
import { after, before, test, type TestContext } from "node:test";
import { startBrowserFixture, type BrowserFixture } from "@agw/test-harness/browser";
import type { Page } from "playwright";
import type {} from "./composer-fixture/main";

let fixture: BrowserFixture;

before(async () => {
  fixture = await startBrowserFixture({
    root: resolve("browser-tests/composer-fixture"),
    outDir: resolve(".scratch/composer-browser"),
  });
});

after(async () => {
  await fixture?.close();
});

async function open(t: TestContext, image = false, empty = false, short = false, queue = false) {
  const { page, errors } = await fixture.newPage();
  t.after(async () => {
    await page.close();
    assert.deepEqual(errors, []);
  });
  await page.goto(
    `${fixture.origin}?${image ? "image&" : ""}${empty ? "empty&" : ""}${short ? "short&" : ""}${queue ? "queue" : ""}`,
  );
  await page.getByRole("button", { name: "Expand input" }).waitFor();
  await page.getByTestId("conversation-scroll").evaluate((element) => {
    element.scrollTop = element.scrollHeight;
  });
  return page;
}

async function dimensions(page: Page) {
  return page.locator("textarea").evaluate((textarea) => {
    const box = textarea.parentElement!.getBoundingClientRect();
    const panel = document.getElementById("root")!.getBoundingClientRect();
    const text = textarea.getBoundingClientRect();
    const column = textarea.closest(".agw-chat-column")?.getBoundingClientRect();
    return {
      box: {
        top: box.top,
        left: box.left,
        right: box.right,
        bottom: box.bottom,
        width: box.width,
        height: box.height,
      },
      panelHeight: panel.height,
      textHeight: text.height,
      column: column
        ? { top: column.top, left: column.left, right: column.right, bottom: column.bottom }
        : null,
      scrollHeight: textarea.scrollHeight,
      clientHeight: textarea.clientHeight,
    };
  });
}

async function conversationLayout(page: Page) {
  return page.getByTestId("conversation-scroll").evaluate((scroll) => {
    const list = scroll.querySelector('[data-testid="conversation-list"]')!;
    return {
      list: list.getBoundingClientRect().toJSON(),
      scrollTop: scroll.scrollTop,
      scrollHeight: scroll.scrollHeight,
      clientHeight: scroll.clientHeight,
    };
  });
}

for (const short of [false, true]) {
  test(`expanding overlays a ${short ? "short" : "long"} conversation without changing its layout`, async (t) => {
    const page = await open(t, false, false, short);
    const initial = await conversationLayout(page);
    const initialInput = await dimensions(page);

    await page.getByRole("button", { name: "Expand input" }).click();

    assert.deepEqual(await conversationLayout(page), initial);
    const expanded = await dimensions(page);
    assert.equal(expanded.box.bottom, initialInput.box.bottom);
    assert.ok(expanded.box.top < initialInput.box.top);

    await page.getByRole("button", { name: "Collapse input" }).click();

    assert.deepEqual(await conversationLayout(page), initial);
    assert.deepEqual((await dimensions(page)).box, initialInput.box);
  });
}

test("expanding preserves the scroll position while reading earlier messages", async (t) => {
  const page = await open(t);
  await page.getByTestId("conversation-scroll").evaluate((scroll) => {
    scroll.scrollTop = 400;
  });
  const initial = await conversationLayout(page);

  await page.getByRole("button", { name: "Expand input" }).click();
  assert.deepEqual(await conversationLayout(page), initial);
  await page.getByRole("button", { name: "Collapse input" }).click();
  assert.deepEqual(await conversationLayout(page), initial);
});

test("a long queue stays accessible above the overlay while preserving an unsaved edit", async (t) => {
  const page = await open(t, false, false, true, true);
  await page.getByRole("button", { name: "Edit queued message 1" }).click();
  const editor = page.getByRole("textbox", { name: "Edit queued message" });
  const draft = "queue draft\n".repeat(20);
  await editor.fill(draft);
  const initial = await conversationLayout(page);
  await page.getByRole("button", { name: "Expand input" }).click();

  const panel = await page.getByTestId("chat-panel").boundingBox();
  const queueBounds = await page.locator(".agw-input-queue").boundingBox();
  assert.ok(panel && queueBounds);
  assert.ok(queueBounds.y >= panel.y, "The entire queue viewport stays inside the chat panel");
  assert.deepEqual(await conversationLayout(page), initial);
  assert.equal(await editor.inputValue(), draft);
  await page.getByRole("button", { name: "Save", exact: true }).click();
  assert.deepEqual(await page.evaluate(() => window.composerFixture.queueEdits), [draft]);
  await page.getByRole("button", { name: "Edit queued message 8" }).click();
  assert.equal(await editor.inputValue(), "Queued message 8");
  await editor.fill("last edited draft");
  await page.getByRole("button", { name: "Collapse input" }).click();
  assert.equal(await editor.inputValue(), "last edited draft");
});

for (const viewport of [
  { width: 1440, height: 900 },
  { width: 1100, height: 760 },
  { width: 680, height: 760 },
  { width: 360, height: 760 },
]) {
  test(`composer at ${viewport.width}x${viewport.height} keeps its width while expanding to 70%`, async (t) => {
    const page = await open(t);
    await page.setViewportSize(viewport);
    const input = page.locator("textarea");
    await input.fill("keep this draft");
    await input.evaluate((element) => (element as HTMLTextAreaElement).setSelectionRange(5, 9));
    const initial = await dimensions(page);
    const expand = await page.getByRole("button", { name: "Expand input" }).boundingBox();
    const send = await page.getByRole("button", { name: "Send message" }).boundingBox();
    assert.ok(expand && send);
    assert.ok(expand.x < send.x);
    assert.equal(expand.y, send.y);

    await page.getByRole("button", { name: "Expand input" }).click();
    const expanded = await dimensions(page);
    assert.ok(expanded.column, "The expanded input stays inside agw-chat-column");
    assert.ok(Math.abs(expanded.box.height - expanded.panelHeight * 0.7) < 1);
    assert.equal(expanded.box.left, initial.box.left);
    assert.equal(expanded.box.right, initial.box.right);
    assert.equal(expanded.box.width, initial.box.width);
    assert.equal(expanded.box.bottom, initial.box.bottom);
    assert.ok(expanded.box.left >= expanded.column.left);
    assert.ok(expanded.box.right <= expanded.column.right);
    assert.ok(expanded.box.top >= expanded.column.top);
    assert.ok(expanded.box.bottom <= expanded.column.bottom);
    assert.ok(expanded.textHeight > initial.textHeight * 3);
    const collapse = await page.getByRole("button", { name: "Collapse input" }).boundingBox();
    const expandedSend = await page.getByRole("button", { name: "Send message" }).boundingBox();
    assert.ok(collapse && expandedSend);
    assert.equal(collapse.x, expand.x);
    assert.equal(collapse.y, expandedSend.y);
    assert.equal(
      expanded.box.bottom - collapse.y - collapse.height,
      initial.box.bottom - expand.y - expand.height,
    );
    assert.equal(await input.inputValue(), "keep this draft");
    assert.deepEqual(
      await input.evaluate((element) => {
        const textarea = element as HTMLTextAreaElement;
        return [textarea.selectionStart, textarea.selectionEnd];
      }),
      [5, 9],
    );

    await page.setViewportSize({ width: viewport.width, height: viewport.height - 160 });
    const resized = await dimensions(page);
    assert.ok(Math.abs(resized.box.height - resized.panelHeight * 0.7) < 1);
    assert.equal(resized.box.width, initial.box.width);
    await page.getByRole("button", { name: "Collapse input" }).click();
    assert.equal((await dimensions(page)).box.height, initial.box.height);
    assert.equal(await input.inputValue(), "keep this draft");
    assert.deepEqual(
      await input.evaluate((element) => {
        const textarea = element as HTMLTextAreaElement;
        return [textarea.selectionStart, textarea.selectionEnd];
      }),
      [5, 9],
    );
    assert.equal(await input.evaluate((element) => document.activeElement === element), true);
  });
}

for (const width of [1440, 680]) {
  test(`an empty conversation at ${width}px expands within agw-chat-column at the same width`, async (t) => {
    const page = await open(t, false, true);
    await page.setViewportSize({ width, height: 760 });
    const initial = await dimensions(page);
    await page.getByRole("button", { name: "Expand input" }).click();
    const expanded = await dimensions(page);
    const panel = await page.getByTestId("chat-panel").boundingBox();
    assert.ok(expanded.column && panel);
    assert.equal(expanded.box.left, initial.box.left);
    assert.equal(expanded.box.right, initial.box.right);
    assert.equal(expanded.box.width, initial.box.width);
    assert.ok(expanded.box.height > initial.box.height);
    assert.ok(Math.abs(expanded.box.height - panel.height * 0.7) < 1);
    assert.ok(expanded.box.top >= expanded.column.top);
    assert.ok(expanded.box.bottom <= expanded.column.bottom);
    assert.ok(expanded.box.top >= panel.y);
    assert.ok(expanded.box.bottom <= panel.y + panel.height);
    await page.getByRole("button", { name: "Collapse input" }).click();
    assert.deepEqual((await dimensions(page)).box, initial.box);
  });
}

test("sending from an expanded empty conversation immediately collapses the input", async (t) => {
  const page = await open(t, false, true);
  const collapsedHeight = (await dimensions(page)).box.height;
  await page.getByRole("button", { name: "Expand input" }).click();
  await page.locator("textarea").fill("first message");
  await page.getByRole("button", { name: "Send message" }).click();
  await page.getByTestId("conversation-list").waitFor();

  assert.ok(await page.getByRole("button", { name: "Expand input" }).isVisible());
  assert.equal((await dimensions(page)).box.height, collapsedHeight);
  assert.deepEqual(await page.evaluate(() => window.composerFixture.submissions), [
    "first message",
  ]);
});

test("the expanded composer follows the original width when the conversation column resizes", async (t) => {
  const page = await open(t);
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.getByRole("button", { name: "Expand input" }).click();
  await page.setViewportSize({ width: 680, height: 900 });
  await page.waitForFunction(() => {
    const textarea = document.querySelector("textarea")!;
    const box = textarea.parentElement!.getBoundingClientRect();
    const column = document
      .querySelector('[data-testid="conversation-list"]')!
      .getBoundingClientRect();
    return box.left === column.left + 12 && box.width === column.width - 24;
  });
  const expanded = await dimensions(page);
  assert.ok(expanded.column);
  assert.ok(expanded.box.left >= expanded.column.left);
  assert.ok(expanded.box.right <= expanded.column.right);
  await page.getByRole("button", { name: "Collapse input" }).click();
  const collapsed = await dimensions(page);
  assert.equal(expanded.box.left, collapsed.box.left);
  assert.equal(expanded.box.right, collapsed.box.right);
  assert.equal(expanded.box.width, collapsed.box.width);
});

test("the composer sends with Enter when collapsed and inserts newlines when expanded", async (t) => {
  const page = await open(t);
  const input = page.locator("textarea");
  await input.fill("collapsed message");
  await input.press("Shift+Enter");
  assert.equal(await input.inputValue(), "collapsed message\n");
  assert.deepEqual(await page.evaluate(() => window.composerFixture.submissions), []);
  await input.press("Enter");
  assert.deepEqual(await page.evaluate(() => window.composerFixture.submissions), [
    "collapsed message\n",
  ]);

  await page.getByRole("button", { name: "Expand input" }).click();
  for (const key of [
    "Enter",
    "Shift+Enter",
    "Control+Enter",
    "Control+Shift+Enter",
    "Meta+Enter",
  ]) {
    await input.fill("first line");
    await input.press(key);
    assert.equal(await input.inputValue(), "first line\n", key);
    assert.equal(await page.evaluate(() => window.composerFixture.submissions.length), 1);
  }
  await page.getByRole("button", { name: "Send message" }).click();
  assert.deepEqual(await page.evaluate(() => window.composerFixture.submissions), [
    "collapsed message\n",
    "first line\n",
  ]);
  assert.equal(await input.inputValue(), "");
  assert.ok(await page.getByRole("button", { name: "Expand input" }).isVisible());
  await input.fill("collapsed again");
  await input.press("Enter");
  assert.equal(
    await page.evaluate(() => window.composerFixture.submissions.at(-1)),
    "collapsed again",
  );
});

test("Ctrl+Enter replaces the selection with a newline and keeps the caret in the expanded draft", async (t) => {
  const page = await open(t);
  await page.getByRole("button", { name: "Expand input" }).click();
  const input = page.locator("textarea");
  await input.fill("first replace last");
  await input.evaluate((element) => (element as HTMLTextAreaElement).setSelectionRange(6, 14));
  await input.press("Control+Enter");
  await input.press("x");

  assert.equal(await input.inputValue(), "first \nxlast");
  assert.deepEqual(await page.evaluate(() => window.composerFixture.submissions), []);
  assert.ok(await page.getByRole("button", { name: "Collapse input" }).isVisible());
});

test("images, tools and long text stay inside the expanded input area", async (t) => {
  const page = await open(t, true);
  await page.getByRole("img", { name: "draft.png" }).waitFor();
  await page.getByRole("button", { name: "Expand input" }).click();
  await page.locator("textarea").fill("long message\n".repeat(200));
  const expanded = await dimensions(page);
  assert.ok(Math.abs(expanded.box.height - expanded.panelHeight * 0.7) < 1);
  assert.ok(expanded.scrollHeight > expanded.clientHeight);

  for (const element of [
    page.getByRole("img", { name: "draft.png" }),
    page.getByRole("button", { name: "Add", exact: true }),
    page.getByRole("combobox", { name: "Tool permission mode" }),
    page.getByRole("button", { name: "Send message" }),
  ]) {
    const bounds = await element.boundingBox();
    assert.ok(bounds);
    assert.ok(bounds.y >= expanded.box.top);
    assert.ok(bounds.y + bounds.height <= expanded.box.bottom);
  }
  await page.getByRole("button", { name: "Collapse input" }).click();
  assert.equal(await page.getByRole("img", { name: "draft.png" }).count(), 1);
  assert.equal(await page.locator("textarea").inputValue(), "long message\n".repeat(200));
});

test("expanded toolbar menus remain usable and collapsing preserves the draft", async (t) => {
  const page = await open(t);
  await page.locator("textarea").fill("keep this message");
  await page.getByRole("button", { name: "Expand input" }).click();
  await page.getByRole("combobox", { name: "Tool permission mode" }).click();
  await page.getByRole("option", { name: "Always ask", exact: true }).press("Escape");
  assert.ok(await page.getByRole("button", { name: "Collapse input" }).isVisible());
  assert.equal(await page.getByRole("listbox").count(), 0);
  await page.getByRole("combobox", { name: "Tool permission mode" }).click();
  await page.getByRole("option", { name: "Always ask", exact: true }).click();
  assert.equal(
    await page.getByRole("combobox", { name: "Tool permission mode" }).textContent(),
    "Always ask",
  );
  await page.getByRole("button", { name: "Collapse input" }).click();
  assert.equal(await page.locator("textarea").inputValue(), "keep this message");
  assert.equal(
    await page.locator("textarea").evaluate((element) => document.activeElement === element),
    true,
  );
  assert.equal(
    await page.getByRole("combobox", { name: "Tool permission mode" }).textContent(),
    "Always ask",
  );
});

test("expanding stays within agw-chat-column and keeps neighboring panels interactive", async (t) => {
  const page = await open(t);
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.locator("textarea").fill("scoped draft");
  await page.getByRole("button", { name: "Expand input" }).click();
  assert.ok((await dimensions(page)).column);
  const side = page.getByTestId("neighbor-panel");
  assert.equal(await side.getAttribute("aria-hidden"), null);
  await side.getByRole("button", { name: "Side action 0" }).click();
  assert.ok(await side.getByRole("button", { name: "Side action 1" }).isVisible());
  assert.ok(await page.getByRole("button", { name: "Collapse input" }).isVisible());
  assert.equal(await page.locator("textarea").inputValue(), "scoped draft");
  const expanded = await dimensions(page);
  assert.ok(expanded.column);
  assert.ok(expanded.box.left >= expanded.column.left);
  assert.ok(expanded.box.right <= expanded.column.right);
  await page.getByRole("button", { name: "Collapse input" }).click();
  const collapsed = await dimensions(page);
  assert.equal(collapsed.box.left, expanded.box.left);
  assert.equal(collapsed.box.width, expanded.box.width);
});
