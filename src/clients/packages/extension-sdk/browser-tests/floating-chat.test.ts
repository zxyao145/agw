import assert from "node:assert/strict";
import test from "node:test";
import { build } from "esbuild";
import { chromium, type Page, type ViewportSize } from "playwright";
import { fileURLToPath } from "node:url";

const bundle = await build({
  entryPoints: [fileURLToPath(new URL("../src/sdk/floating-chat.ts", import.meta.url))],
  bundle: true,
  format: "iife",
  globalName: "FloatingChatBundle",
  platform: "browser",
  write: false,
});

async function withBrowserPage(viewport: ViewportSize, run: (page: Page) => Promise<void>) {
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage({ viewport });
    await page.setContent(
      '<!doctype html><html><body><div id="floating-chat-container"></div></body></html>',
    );
    await page.addScriptTag({ content: bundle.outputFiles[0].text });
    await page.addScriptTag({
      content: `
        window.floatingChatOpenCount = 0;
        window.floatingChatPointerId = null;
        window.floatingChatButton = window.FloatingChatBundle.mountFloatingChatButton(
          document.getElementById("floating-chat-container"),
          { onOpen: function () { window.floatingChatOpenCount++; } }
        );
        document.addEventListener("pointerdown", function (event) {
          window.floatingChatPointerId = event.pointerId;
        }, true);
      `,
    });
    await run(page);
  } finally {
    await browser.close();
  }
}

async function openCount(page: Page): Promise<number> {
  return page.evaluate("window.floatingChatOpenCount");
}

test("floating Chat drags across the viewport, docks left, and suppresses the drag click", async () => {
  await withBrowserPage({ width: 800, height: 600 }, async (page) => {
    const button = page.getByRole("button", { name: "Open chat" });
    const initial = await button.boundingBox();
    assert.ok(initial);

    await page.mouse.move(initial.x + initial.width / 2, initial.y + initial.height / 2);
    await page.mouse.down();
    await page.mouse.move(32, 250, { steps: 8 });
    await page.mouse.up();

    const docked = await button.boundingBox();
    assert.ok(docked);
    assert.ok(Math.abs(docked.x - 24) < 1);
    assert.ok(Math.abs(docked.y + docked.height / 2 - 250) < 1);
    assert.equal(await openCount(page), 0);

    await button.press("Enter");
    assert.equal(await openCount(page), 1);
  });
});

test("floating Chat preserves small pointer clicks and keyboard activation", async () => {
  await withBrowserPage({ width: 800, height: 600 }, async (page) => {
    const button = page.getByRole("button", { name: "Open chat" });
    const initial = await button.boundingBox();
    assert.ok(initial);

    const x = initial.x + initial.width / 2;
    const y = initial.y + initial.height / 2;
    await page.mouse.move(x, y);
    await page.mouse.down();
    await page.mouse.move(x + 2, y + 1);
    await page.mouse.up();

    assert.equal(await openCount(page), 1);
    await button.focus();
    await page.keyboard.press("Space");
    assert.equal(await openCount(page), 2);
  });
});

test("floating Chat remains docked and visible when the viewport shrinks", async () => {
  await withBrowserPage({ width: 800, height: 600 }, async (page) => {
    const button = page.getByRole("button", { name: "Open chat" });
    const initial = await button.boundingBox();
    assert.ok(initial);

    await page.mouse.move(initial.x + initial.width / 2, initial.y + initial.height / 2);
    await page.mouse.down();
    await page.mouse.move(40, 260, { steps: 6 });
    await page.mouse.up();

    let docked = await button.boundingBox();
    assert.ok(docked);
    assert.ok(Math.abs(docked.x - 24) < 1);

    await page.mouse.move(docked.x + docked.width / 2, docked.y + docked.height / 2);
    await page.mouse.down();
    await page.mouse.move(798, 598, { steps: 8 });
    await page.mouse.up();

    docked = await button.boundingBox();
    assert.ok(docked);
    assert.ok(Math.abs(docked.x + docked.width - 776) < 1);
    assert.ok(docked.y + docked.height <= 600);

    await page.setViewportSize({ width: 320, height: 180 });
    await page.waitForFunction(
      "window.innerWidth === 320 && document.querySelector('button').getBoundingClientRect().bottom <= 180",
    );
    docked = await button.boundingBox();
    assert.ok(docked);
    assert.ok(Math.abs(docked.x + docked.width - 296) < 1);
    assert.ok(docked.x >= 0 && docked.y >= 0);
    assert.ok(docked.x + docked.width <= 320);
    assert.ok(docked.y + docked.height <= 180);

    await page.setViewportSize({ width: 640, height: 480 });
    await page.waitForFunction(
      "window.innerWidth === 640 && document.querySelector('button').getBoundingClientRect().right === 616",
    );
    docked = await button.boundingBox();
    assert.ok(docked);
    assert.ok(Math.abs(docked.x + docked.width - 616) < 1);
    assert.ok(docked.y + docked.height <= 480);
  });
});

test("floating Chat stays docked after its container hides during a viewport resize", async () => {
  await withBrowserPage({ width: 800, height: 600 }, async (page) => {
    const button = page.getByRole("button", { name: "Open chat" });
    const initial = await button.boundingBox();
    assert.ok(initial);

    await page.mouse.move(initial.x + initial.width / 2, initial.y + initial.height / 2);
    await page.mouse.down();
    await page.mouse.move(760, 260, { steps: 6 });
    await page.mouse.up();

    let docked = await button.boundingBox();
    assert.ok(docked);
    assert.ok(Math.abs(docked.x + docked.width - 776) < 1);

    await page.evaluate(
      "document.getElementById('floating-chat-container').style.display = 'none'",
    );
    await page.setViewportSize({ width: 320, height: 180 });
    await page.evaluate("document.getElementById('floating-chat-container').style.display = ''");
    await page.waitForFunction(
      "window.innerWidth === 320 && document.querySelector('button').getBoundingClientRect().right === 296 && document.querySelector('button').getBoundingClientRect().bottom <= 180",
    );

    docked = await button.boundingBox();
    assert.ok(docked);
    assert.ok(Math.abs(docked.x + docked.width - 296) < 1);
    assert.ok(docked.y >= 0 && docked.y + docked.height <= 180);
  });
});

test("floating Chat ends a cancelled drag and disposes during pointer capture", async () => {
  await withBrowserPage({ width: 800, height: 600 }, async (page) => {
    const button = page.getByRole("button", { name: "Open chat" });
    const initial = await button.boundingBox();
    assert.ok(initial);

    await page.mouse.move(initial.x + initial.width / 2, initial.y + initial.height / 2);
    await page.mouse.down();
    await page.mouse.move(40, 260, { steps: 6 });
    const pointerId = await page.evaluate("window.floatingChatPointerId");
    assert.notEqual(pointerId, null);
    await page.evaluate(
      `document.querySelector('button').dispatchEvent(new PointerEvent("pointercancel", { bubbles: true, pointerId: ${pointerId} }))`,
    );
    await page.mouse.up();

    let docked = await button.boundingBox();
    assert.ok(docked);
    assert.ok(Math.abs(docked.x - 24) < 1);
    assert.equal(await openCount(page), 0);

    await button.focus();
    await page.keyboard.press("Enter");
    assert.equal(await openCount(page), 1);

    const resizedStart = await button.boundingBox();
    assert.ok(resizedStart);
    await page.mouse.move(
      resizedStart.x + resizedStart.width / 2,
      resizedStart.y + resizedStart.height / 2,
    );
    await page.mouse.down();
    await page.mouse.move(120, 300, { steps: 4 });
    await page.evaluate("window.floatingChatButton.dispose()");
    await page.mouse.move(200, 350);
    await page.mouse.up();

    assert.equal(await page.getByRole("button", { name: "Open chat" }).count(), 0);
    assert.equal(await openCount(page), 1);
  });
});
