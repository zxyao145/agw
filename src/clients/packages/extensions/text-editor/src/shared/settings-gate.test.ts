import assert from "node:assert/strict";
import { afterEach, beforeEach, test } from "node:test";

import { createExtensionError } from "@agw/extension-sdk";

import { settle } from "../editor/test-edit-host";
import { connectTestHost, type TestHost } from "../test-host";

// react-dom 在 test-edit-host 准备好 jsdom 全局对象之后才导入。
// react-dom is imported only after test-edit-host has prepared the jsdom globals.
const { act } = await import("react");
const { createRoot } = await import("react-dom/client");
const { applyDocumentFontSize } = await import("./apply-font-sizes");
const { createSettingsGate } = await import("./settings-gate");

const SETTINGS_URI = "file:///storage/text-editor/settings.json";
const FONT_SIZE = "--agw-font-size-document";

const files = new Map<string, Uint8Array>();
const requests: string[] = [];
// 设置后，settings.json 的读取等它完成再回复。When set, the settings read waits for it before replying.
let gate: Promise<void> | null = null;
let host: TestHost;

beforeEach(() => {
  files.clear();
  requests.length = 0;
  gate = null;
  document.documentElement.style.removeProperty(FONT_SIZE);
  host = connectTestHost(async (method, params) => {
    requests.push(method);
    const { uri } = params as { uri: string };
    assert.equal(method, "fs.readFile");
    assert.equal(uri, SETTINGS_URI);
    if (gate) await gate;
    const data = files.get(uri);
    if (data === undefined) throw createExtensionError("ENOENT", `${uri} does not exist.`);
    return data;
  });
});
afterEach(() => host.close());

function putSettings(text: string): void {
  files.set(SETTINGS_URI, new TextEncoder().encode(text));
}

function fontSize(): string {
  return document.documentElement.style.getPropertyValue(FONT_SIZE);
}

/** 带一个 `createRoot` 创建的 root 的查看器，`open` 记录调用并应用文档字号。A viewer with a root made by `createRoot`; `open` records and applies the size. */
function mountViewer() {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  const events: string[] = [];
  const viewer = createSettingsGate(root, (api, settings, params) => {
    events.push(`open ${String(params)}`);
    const font = applyDocumentFontSize(api, settings);
    return {
      show: (next) => {
        events.push(`show ${String(next)}`);
      },
      dispose: () => {
        events.push("dispose");
        font.dispose();
        root.unmount();
      },
    };
  });
  return { container, root, events, viewer };
}

test("an early onShow opens the file only after the settings are read", async () => {
  putSettings(JSON.stringify({ schemaVersion: 1, documentFontSize: 18, codeFontSize: null }));
  const { events, viewer } = mountViewer();
  let release!: () => void;
  gate = new Promise<void>((resolve) => (release = resolve));

  const loading = viewer.onLoad(host.api, "a.md");
  viewer.onShow("a.md");
  await settle();
  assert.deepEqual(events, []);

  release();
  await act(() => loading);
  await settle();
  assert.deepEqual(events, ["open a.md", "show a.md"]);
  assert.equal(fontSize(), "18px");
  await act(async () => viewer.onUnload());
});

test("unloading while the settings read is pending creates no session and no listener", async () => {
  putSettings(JSON.stringify({ schemaVersion: 1, documentFontSize: 18, codeFontSize: null }));
  const { events, viewer } = mountViewer();
  let release!: () => void;
  gate = new Promise<void>((resolve) => (release = resolve));

  const loading = viewer.onLoad(host.api, "a.md");
  await act(async () => viewer.onUnload());
  release();
  await act(() => loading);
  assert.deepEqual(events, []);

  host.changeTheme({
    kind: "light",
    tokens: { "--agw-font-size-base": "20px", "--agw-font-size-min": "14px" },
  });
  await settle();
  assert.equal(fontSize(), "");
});

test("onUnload disposes what was opened, including the font-size listener", async () => {
  putSettings(JSON.stringify({ schemaVersion: 1, documentFontSize: null, codeFontSize: null }));
  const { events, viewer } = mountViewer();
  await act(() => viewer.onLoad(host.api, "a.md"));
  assert.equal(fontSize(), "16px");

  await act(async () => viewer.onUnload());
  assert.deepEqual(events, ["open a.md", "dispose"]);
  host.changeTheme({
    kind: "light",
    tokens: { "--agw-font-size-base": "20px", "--agw-font-size-min": "14px" },
  });
  await settle();
  assert.equal(fontSize(), "16px");
});

test("invalid settings show the panel, never open, and ignore a later onShow", async () => {
  putSettings("not json");
  const { container, events, viewer } = mountViewer();

  await act(() => viewer.onLoad(host.api, "a.md"));
  viewer.onShow("a.md");
  await settle();

  assert.deepEqual(events, []);
  const text = container.textContent ?? "";
  assert.ok(
    text.includes("Text Editor settings are invalid: settings.json is not valid JSON"),
    text,
  );
  assert.ok(text.includes("Open Text Editor settings to reset them."), text);
  // 宿主只收到 settings.json 的读取，没有文档的打开或读取请求。
  // The host received only the settings.json read, no document open or read request.
  assert.deepEqual(requests, ["fs.readFile"]);
  await act(async () => viewer.onUnload());
});

test("onShow before onLoad throws", () => {
  const { viewer } = mountViewer();
  assert.throws(() => viewer.onShow("a.md"), /before onLoad/);
});

test("a read error other than invalid settings shows the panel, never opens, and ignores a later onShow", async () => {
  const { container, events, viewer } = mountViewer();
  const failing = connectTestHost(() => {
    throw createExtensionError("EACCES", "no access");
  });
  try {
    await act(() => viewer.onLoad(failing.api, "a.md"));
    viewer.onShow("a.md");
    await settle();
  } finally {
    failing.close();
  }
  assert.deepEqual(events, []);
  const text = container.textContent ?? "";
  assert.ok(text.includes("Text Editor settings are invalid: no access"), text);
  assert.ok(text.includes("Open Text Editor settings to reset them."), text);
  await act(async () => viewer.onUnload());
});
