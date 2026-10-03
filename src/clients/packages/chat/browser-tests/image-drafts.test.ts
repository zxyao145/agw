import assert from "node:assert/strict";
import { after, before, test, type TestContext } from "node:test";
import { createServer, type Server } from "node:http";
import { mkdir, mkdtemp, rm } from "node:fs/promises";
import { resolve } from "node:path";
import { build } from "esbuild";
import { chromium, type BrowserContext, type Page } from "playwright";
import type {} from "./image-drafts-fixture";

const artifacts = resolve(".scratch/image-draft-tests");
const scope = { serverId: "server-a", projectId: "project-a" };
let server: Server;
let origin: string;

before(async () => {
  await mkdir(artifacts, { recursive: true });
  const bundle = await build({
    entryPoints: ["browser-tests/image-drafts-fixture.tsx"],
    bundle: true,
    write: false,
    jsx: "automatic",
    platform: "browser",
    format: "iife",
    loader: { ".css": "empty" },
    define: { "process.env.NODE_ENV": '"development"', "process.env": "{}" },
  });
  server = createServer((request, response) => {
    if (request.url === "/fixture.js") {
      response.writeHead(200, { "content-type": "text/javascript" });
      response.end(bundle.outputFiles[0].contents);
    } else if (request.url === "/") {
      response.writeHead(200, { "content-type": "text/html" });
      response.end('<!doctype html><div id="root"></div><script src="/fixture.js"></script>');
    } else {
      response.writeHead(404);
      response.end();
    }
  });
  await new Promise<void>((ready) => server.listen(0, "127.0.0.1", ready));
  const address = server.address();
  assert.ok(address && typeof address === "object");
  origin = `http://127.0.0.1:${address.port}`;
});

after(async () => {
  if (server) await new Promise<void>((done) => server.close(() => done()));
});

async function openFixture(t: TestContext) {
  const profile = await mkdtemp(resolve(artifacts, "profile-"));
  let context: BrowserContext;
  const start = async (quotaSize?: number) => {
    context = await chromium.launchPersistentContext(profile, {
      headless: true,
      artifactsDir: artifacts,
      downloadsPath: artifacts,
    });
    context.setDefaultTimeout(10_000);
    const page = context.pages()[0];
    if (quotaSize !== undefined) {
      const cdp = await context.newCDPSession(page);
      await cdp.send("Storage.overrideQuotaForOrigin", { origin, quotaSize });
    }
    page.on("pageerror", (error) => assert.fail(error.stack));
    await page.goto(origin);
    await ready(page);
    return page;
  };
  t.after(async () => {
    await context?.close();
    await rm(profile, { recursive: true, force: true });
  });
  const page = await start();
  return {
    page,
    restart: async (quotaSize?: number) => {
      await context.close();
      return start(quotaSize);
    },
  };
}

async function ready(page: Page) {
  await page.waitForFunction(
    () => document.querySelector('output[aria-label="Image draft state"]')?.textContent === "ready",
  );
}

async function paste(page: Page, name: string) {
  await page.locator("textarea").evaluate((textarea, filename) => {
    const clipboardData = new DataTransfer();
    clipboardData.items.add(window.imageDraftFixture.createImageFile(filename));
    textarea.dispatchEvent(new ClipboardEvent("paste", { bubbles: true, clipboardData }));
  }, name);
  await page.getByRole("img", { name, exact: true }).waitFor();
  await ready(page);
}

async function names(page: Page) {
  return page
    .locator('ul[aria-label="Pasted images"] img')
    .evaluateAll((images) => images.map((image) => image.getAttribute("alt")));
}

async function select(page: Page, selection: Parameters<Window["imageDraftFixture"]["select"]>[0]) {
  await page.evaluate((next) => window.imageDraftFixture.select(next), selection);
  await ready(page);
}

test("conversation, project, server and new-conversation image drafts stay independent", async (t) => {
  const { page } = await openFixture(t);
  await paste(page, "a.png");
  await page.locator("textarea").fill("Draft A");
  const selections = [
    { conversationId: "conversation-b" },
    { conversationId: null },
    { ...scope, conversationId: "conversation-a", projectId: "project-b" },
    { ...scope, conversationId: "conversation-a", serverId: "server-b" },
  ];
  for (const [index, selection] of selections.entries()) {
    await select(page, selection);
    assert.deepEqual(await names(page), []);
    await paste(page, `${index}.png`);
  }
  await select(page, { ...scope, conversationId: "conversation-a" });
  assert.deepEqual(await names(page), ["a.png"]);
  await select(page, { conversationId: "conversation-b" });
  assert.deepEqual(await names(page), ["0.png"]);
  await select(page, { conversationId: null });
  assert.deepEqual(await names(page), ["1.png"]);
});

test("refreshing and restarting Chromium restore saved images and removals", async (t) => {
  const fixture = await openFixture(t);
  await paste(fixture.page, "keep.png");
  await paste(fixture.page, "remove.png");
  await fixture.page.reload();
  await ready(fixture.page);
  assert.deepEqual(await names(fixture.page), ["keep.png", "remove.png"]);
  await fixture.page.getByRole("button", { name: "Remove remove.png", exact: true }).click();
  await ready(fixture.page);
  const page = await fixture.restart();
  assert.deepEqual(await names(page), ["keep.png"]);
  assert.equal(
    await page
      .getByRole("img", { name: "keep.png", exact: true })
      .evaluate((image: HTMLImageElement) => image.naturalWidth),
    1,
  );
});

test("submission acceptance clears only submitted images and rejection keeps the draft", async (t) => {
  const { page } = await openFixture(t);
  await paste(page, "send.png");
  await page.locator("textarea").fill("Send this image");
  await page.evaluate(() => {
    window.imageDraftFixture.acceptSubmission = false;
  });
  await page.locator("textarea").press("Control+Enter");
  assert.equal(await page.locator("textarea").inputValue(), "Send this image");
  assert.deepEqual(await names(page), ["send.png"]);
  await page.evaluate(() => {
    window.imageDraftFixture.acceptSubmission = true;
  });
  await page.locator("textarea").press("Control+Enter");
  await ready(page);
  assert.deepEqual(await names(page), []);
  assert.equal(await page.locator("textarea").inputValue(), "");
  assert.deepEqual(
    await page.evaluate(() =>
      window.imageDraftFixture.submissions.map((item) => [item.text, item.images[0].name]),
    ),
    [["Send this image", "send.png"]],
  );
  await paste(page, "next.png");
  await page.reload();
  await ready(page);
  assert.deepEqual(await names(page), ["next.png"]);
});

test("a paste started before switching finishes in its original conversation", async (t) => {
  const { page } = await openFixture(t);
  await page.evaluate(async () => {
    const fixture = window.imageDraftFixture;
    const pending = fixture.current!.add([fixture.createImageFile("original.png")]);
    fixture.select({ conversationId: "conversation-b" });
    await pending;
  });
  await ready(page);
  assert.deepEqual(await names(page), []);
  await select(page, { conversationId: "conversation-a" });
  assert.deepEqual(await names(page), ["original.png"]);
});

test("accepting a new conversation moves persisted images and its in-flight paste", async (t) => {
  const { page } = await openFixture(t);
  await select(page, { conversationId: null });
  await paste(page, "saved.png");
  await page.evaluate(async (scope) => {
    const fixture = window.imageDraftFixture;
    const pending = fixture.current!.add([fixture.createImageFile("reading.png")]);
    const accepted = fixture.drafts.accept(scope, "accepted");
    fixture.select({ conversationId: "accepted" });
    await Promise.all([pending, accepted]);
  }, scope);
  await ready(page);
  assert.deepEqual(await names(page), ["saved.png", "reading.png"]);
  await select(page, { conversationId: null });
  assert.deepEqual(await names(page), []);
  await page.reload();
  await ready(page);
  await select(page, { conversationId: "accepted" });
  assert.deepEqual(await names(page), ["saved.png", "reading.png"]);
});

test("clearing and deleting invalidate unfinished reads", async (t) => {
  const { page } = await openFixture(t);
  await paste(page, "clear.png");
  await page.evaluate(async (scope) => {
    const fixture = window.imageDraftFixture;
    const pending = fixture.current!.add([fixture.createImageFile("late.png")]);
    await Promise.all([pending, fixture.current!.clear()]);
    const next = fixture.current!.add([fixture.createImageFile("deleted.png")]);
    await Promise.all([next, fixture.drafts.remove(scope, "conversation-a")]);
  }, scope);
  await page.reload();
  await ready(page);
  assert.deepEqual(await names(page), []);
});

test("clearing a project's conversations preserves new drafts and other scopes", async (t) => {
  const { page } = await openFixture(t);
  await paste(page, "existing.png");
  await select(page, { conversationId: null });
  await paste(page, "new.png");
  await select(page, { projectId: "project-b", conversationId: "conversation-a" });
  await paste(page, "other-project.png");
  await page.evaluate((scope) => window.imageDraftFixture.drafts.removeConversations(scope), scope);
  await page.reload();
  await ready(page);
  assert.deepEqual(await names(page), []);
  await select(page, { conversationId: null });
  assert.deepEqual(await names(page), ["new.png"]);
  await select(page, { projectId: "project-b", conversationId: "conversation-a" });
  assert.deepEqual(await names(page), ["other-project.png"]);
});

test("the full 10 MiB attachment allowance survives a reload", async (t) => {
  const { page } = await openFixture(t);
  await page.evaluate(async () => {
    const fixture = window.imageDraftFixture;
    await fixture.current!.add([
      fixture.createImageFile("first.png", 5 * 1024 * 1024),
      fixture.createImageFile("second.png", 5 * 1024 * 1024),
    ]);
  });
  await page.reload();
  await ready(page);
  assert.deepEqual(await names(page), ["first.png", "second.png"]);
  assert.equal(
    await page.evaluate(() =>
      window.imageDraftFixture
        .current!.getSnapshot()
        .attachments.reduce((total, image) => total + image.size, 0),
    ),
    10 * 1024 * 1024,
  );
  assert.deepEqual(
    await page
      .locator('ul[aria-label="Pasted images"] img')
      .evaluateAll((images) => images.map((image) => (image as HTMLImageElement).naturalWidth)),
    [1, 1],
  );
});

test("a real storage quota failure preserves the previous images and blocks submission", async (t) => {
  const fixture = await openFixture(t);
  await paste(fixture.page, "saved.png");
  // 新进程中的 IndexedDB 连接读取本次测试的存储额度。
  // The IndexedDB connection in a new process reads this test's storage quota.
  const page = await fixture.restart(100_000);
  await page.locator("textarea").evaluate((textarea) => {
    const clipboardData = new DataTransfer();
    clipboardData.items.add(
      window.imageDraftFixture.createImageFile("too-large.png", 5 * 1024 * 1024),
    );
    textarea.dispatchEvent(new ClipboardEvent("paste", { bubbles: true, clipboardData }));
  });
  await page.waitForFunction(() => Boolean(window.imageDraftFixture.current!.getSnapshot().error));
  assert.equal(
    await page.evaluate(
      () => (window.imageDraftFixture.current!.getSnapshot().error as DOMException).name,
    ),
    "QuotaExceededError",
  );
  await page.getByText("There is not enough local storage to save these images.").waitFor();
  assert.deepEqual(await names(page), ["saved.png"]);
  await page.locator("textarea").fill("Still here");
  await page.locator("textarea").press("Control+Enter");
  assert.equal(await page.evaluate(() => window.imageDraftFixture.submissions.length), 0);
  await page.reload();
  await ready(page);
  assert.deepEqual(await names(page), ["saved.png"]);
});

test("transient composers reset on conversation changes and do not persist", async (t) => {
  const { page } = await openFixture(t);
  await select(page, { persist: false });
  await paste(page, "transient.png");
  await select(page, { conversationId: "conversation-b" });
  assert.deepEqual(await names(page), []);
  await page.reload();
  await ready(page);
  assert.deepEqual(await names(page), []);
});
