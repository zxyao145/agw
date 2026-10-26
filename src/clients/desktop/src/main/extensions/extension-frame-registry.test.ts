import assert from "node:assert/strict";
import test from "node:test";

import { ExtensionFrameRegistry } from "./extension-frame-registry";
import { ExtensionResourceTracker } from "./extension-resource-tracker";

function createRegistry() {
  let next = 0;
  const registry = new ExtensionFrameRegistry(() => `page-${++next}`);
  registry.register({
    frameId: "frame-1",
    extensionId: "sample",
    kind: "mainView",
    viewId: "app",
    webContentsId: 11,
  });
  return registry;
}

test("attaching the same host is a no-op, another host requires a reset, and a cleared host rejects requests", () => {
  const registry = new ExtensionFrameRegistry();
  assert.equal(registry.attachState("host-a"), "free");
  registry.setHost("host-a");
  assert.equal(registry.attachState("host-a"), "unchanged");
  assert.equal(registry.attachState("host-b"), "reset-required");
  registry.clearHost();
  assert.equal(registry.isCurrentHost("host-a"), false);
  assert.throws(() => registry.assertHost("host-a"), /no longer attached/u);
  registry.setHost("host-b");
  assert.throws(() => registry.assertHost("host-a"), /no longer attached/u);
  registry.assertHost("host-b");
});

test("capability callers need a live frame of the same extension and its current page", () => {
  const registry = createRegistry();
  const hello = registry.recordHello(11)!;
  assert.equal(hello.pageId, "page-1");
  assert.equal(
    registry.validateCaller({ extensionId: "sample", frameId: "frame-1", pageId: "page-1" })
      .frameId,
    "frame-1",
  );
  for (const caller of [
    { extensionId: "sample", frameId: "missing", pageId: "page-1" },
    { extensionId: "other", frameId: "frame-1", pageId: "page-1" },
    { extensionId: "sample", frameId: "frame-1", pageId: "page-0" },
  ]) {
    assert.throws(() => registry.validateCaller(caller), { code: "ERR_FRAME_GONE" });
  }
});

test("page replacement signals in either order replace each page exactly once and before the new page", () => {
  for (const order of [
    ["navigate", "hello"],
    ["hello", "navigate"],
  ] as const) {
    const registry = createRegistry();
    const events: string[] = [];
    const apply = (signal: "navigate" | "hello") => {
      if (signal === "navigate") {
        const replaced = registry.recordNavigation(11);
        if (replaced) events.push(`replaced:${replaced.replacedPageId}`);
      } else {
        const hello = registry.recordHello(11)!;
        if (hello.replacement) events.push(`replaced:${hello.replacement.replacedPageId}`);
        events.push(`port:${hello.pageId}`);
      }
    };
    // 初次加载。Initial load.
    for (const signal of order) apply(signal);
    // 两次重新加载。Two reloads.
    for (const signal of order) apply(signal);
    for (const signal of order) apply(signal);

    assert.deepEqual(
      events,
      ["port:page-1", "replaced:page-1", "port:page-2", "replaced:page-2", "port:page-3"],
      order.join(","),
    );
    assert.equal(registry.get("frame-1")?.pageId, "page-3");
  }
});

test("a navigation that never commits leaves the page untouched", () => {
  const registry = createRegistry();
  registry.recordHello(11);
  // 被拒绝、取消或成为下载的导航没有 did-navigate，也没有新的 hello。
  // A rejected, canceled or downloaded navigation has neither did-navigate nor a new hello.
  assert.equal(registry.get("frame-1")?.pageId, "page-1");
  assert.equal(registry.isCurrentPage("frame-1", "page-1"), true);
});

test("resources of a replaced page are released and the new page starts empty", () => {
  const registry = createRegistry();
  const released: string[] = [];
  const watchers = new ExtensionResourceTracker<string>((name) => released.push(name));
  assert.equal(registry.recordNavigation(11), null);
  const first = registry.recordHello(11)!;
  const owner = { extensionId: "sample", frameId: "frame-1", pageId: first.pageId };
  watchers.add("w1", owner, "watch-a");
  watchers.add("w2", owner, "watch-b");

  const replacement = registry.recordNavigation(11)!;
  watchers.releasePage(replacement.frame.frameId, replacement.replacedPageId);
  const second = registry.recordHello(11)!;

  assert.deepEqual(released.sort(), ["watch-a", "watch-b"]);
  assert.equal(
    watchers.count((item) => item.pageId === first.pageId),
    0,
  );
  assert.equal(
    watchers.count((item) => item.pageId === second.pageId),
    0,
  );
  assert.throws(
    () =>
      registry.validateCaller({ extensionId: "sample", frameId: "frame-1", pageId: first.pageId }),
    { code: "ERR_FRAME_GONE" },
  );
});

test("unregistered frames are no longer found by WebContents", () => {
  const registry = createRegistry();
  assert.equal(registry.findByWebContents(11)?.frameId, "frame-1");
  registry.unregister("frame-1");
  assert.equal(registry.findByWebContents(11), undefined);
  assert.equal(registry.recordHello(11), null);
  assert.equal(registry.recordNavigation(11), null);
});
