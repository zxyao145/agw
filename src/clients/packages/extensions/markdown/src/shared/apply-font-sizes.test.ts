import assert from "node:assert/strict";
import { afterEach, beforeEach, test } from "node:test";

import { createExtensionError } from "@agw/extension-sdk";

import { settle } from "../editor/test-edit-host";
import { connectTestHost, type TestHost } from "../test-host";
import { applyDocumentFontSize } from "./apply-font-sizes";
import { readTextEditorSettings } from "./text-editor-settings";

// react-dom 在 test-edit-host 准备好 jsdom 全局对象之后才导入。
// react-dom is imported only after test-edit-host has prepared the jsdom globals.
const { act } = await import("react");
const { createRoot } = await import("react-dom/client");
const { createSettingsGate } = await import("./settings-gate");

const SETTINGS_URI = "file:///storage/01a173b5-526c-74d8-864c-2fa2035185e4/settings.json";

// 宿主端的文件系统，键为 URI。The host-side file system, keyed by URI.
const files = new Map<string, Uint8Array>();
const requests: { method: string; uri: string }[] = [];

// 每个测试使用新的宿主，上一个测试的主题监听器与主题不会保留。
// Every test uses a new host, so the previous test's theme listeners and theme do not stay.
let host: TestHost;
beforeEach(() => {
  host = connectTestHost((method, params) => {
    const { uri } = params as { uri: string };
    requests.push({ method, uri });
    const data = files.get(uri);
    if (method !== "fs.readFile" || data === undefined) {
      throw createExtensionError("ENOENT", `${uri} does not exist.`);
    }
    return data;
  });
  files.clear();
  requests.length = 0;
  document.documentElement.style.removeProperty("--agw-font-size-document");
});
afterEach(() => host.close());

function putSettings(documentFontSize: number | null): void {
  files.set(
    SETTINGS_URI,
    new TextEncoder().encode(JSON.stringify({ schemaVersion: 2, documentFontSize })),
  );
}

function documentFontSize(): string {
  return document.documentElement.style.getPropertyValue("--agw-font-size-document");
}

test("applyDocumentFontSize writes the stored document size and keeps it when the base size changes", async () => {
  putSettings(18);
  applyDocumentFontSize(host.api, await readTextEditorSettings(host.api));
  assert.equal(documentFontSize(), "18px");

  host.changeTheme({
    id: "default",
    title: "Default",
    kind: "light",
    tokens: { "--agw-font-size-base": "20px" },
  });
  await settle();
  assert.equal(documentFontSize(), "18px");
});

test("a viewer started through createSettingsGate follows the base size on a theme change with one settings read", async () => {
  putSettings(null);
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  const viewer = createSettingsGate(root, (api, settings) => {
    const font = applyDocumentFontSize(api, settings);
    return {
      show: () => undefined,
      dispose: () => {
        font.dispose();
        root.unmount();
      },
    };
  });
  await act(() => viewer.onLoad(host.api, "a.md"));
  assert.equal(documentFontSize(), "16px");

  host.changeTheme({
    id: "default",
    title: "Default",
    kind: "light",
    tokens: { "--agw-font-size-base": "20px" },
  });
  await settle();
  assert.equal(documentFontSize(), "20px");
  assert.deepEqual(requests, [{ method: "fs.readFile", uri: SETTINGS_URI }]);
  await act(async () => viewer.onUnload());
});

test("applyDocumentFontSize writes the smallest allowed stored size", async () => {
  putSettings(14);
  applyDocumentFontSize(host.api, await readTextEditorSettings(host.api));
  assert.equal(documentFontSize(), "14px");
});
