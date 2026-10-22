import assert from "node:assert/strict";
import { afterEach, beforeEach, test } from "node:test";

import { createExtensionError } from "@agw/extension-sdk";
import { createElement } from "react";

import { settle, type Notification } from "../editor/test-edit-host";
import { connectTestHost, type TestHost } from "../test-host";

// react-dom 在 test-edit-host 准备好 jsdom 全局对象之后才导入。
// react-dom is imported only after test-edit-host has prepared the jsdom globals.
const { act } = await import("react");
const { createRoot } = await import("react-dom/client");
const { SettingsForm } = await import("./settings-form");

const SETTINGS_URI = "file:///storage/text-editor/settings.json";
const DOCUMENT_ID = "text-editor-document-font-size";
const CODE_ID = "text-editor-code-font-size";

const files = new Map<string, Uint8Array>();
const notifications: Notification[] = [];
// 每次写入的内容。The content of every write.
const writes: unknown[] = [];
// 设置后，第 n 次之后的写入失败。When set, writes after this many successful ones fail.
let failWritesAfter: number | null = null;
// 设置后，下一次写入等它完成（拒绝则写入失败）。When set, the next write waits for it (a rejection fails the write).
let heldWrite: Promise<void> | null = null;
// 收到的 fs.writeFile 请求数。The number of fs.writeFile requests received.
let writeRequests = 0;
// 设置后，fs.readFile 以这个错误码失败。When set, fs.readFile fails with this error code.
let failReadsWith: string | null = null;
let host: TestHost;

beforeEach(() => {
  failReadsWith = null;
  files.clear();
  notifications.length = 0;
  writes.length = 0;
  failWritesAfter = null;
  heldWrite = null;
  writeRequests = 0;
  document.body.replaceChildren();
  host = connectTestHost(async (method, params) => {
    const values = params as { uri: string; data?: Uint8Array };
    switch (method) {
      case "fs.readFile": {
        if (failReadsWith !== null) throw createExtensionError(failReadsWith, "no access");
        const data = files.get(values.uri);
        if (data === undefined)
          throw createExtensionError("ENOENT", `${values.uri} does not exist.`);
        return data;
      }
      case "fs.writeFile":
        writeRequests += 1;
        if (heldWrite !== null) {
          const held = heldWrite;
          heldWrite = null;
          await held;
        }
        if (failWritesAfter !== null && writes.length >= failWritesAfter)
          throw createExtensionError("EIO", "The disk is full.");
        assert.equal(values.uri, SETTINGS_URI);
        files.set(values.uri, values.data as Uint8Array);
        writes.push(JSON.parse(new TextDecoder().decode(values.data)));
        return undefined;
      case "ui.notify":
        notifications.push(params as Notification);
        return undefined;
      default:
        throw new Error(`Unexpected request ${method}.`);
    }
  });
});
afterEach(() => host.close());

function putSettings(text: string): void {
  files.set(SETTINGS_URI, new TextEncoder().encode(text));
}

function stored(documentFontSize: number | null, codeFontSize: number | null) {
  return { schemaVersion: 1, documentFontSize, codeFontSize };
}

/** 等待写入队列与读取完成，状态更新在 act 内发生。Waits for reads and the write queue, inside act. */
async function settled(): Promise<void> {
  await act(async () => {
    await settle();
  });
}

async function mount() {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  await act(async () => {
    root.render(createElement(SettingsForm, { api: host.api }));
  });
  await settled();
  return { container, unmount: () => act(async () => root.unmount()) };
}

function select(container: HTMLElement, id: string): HTMLSelectElement {
  const element = container.querySelector<HTMLSelectElement>(`#${id}`);
  if (element === null) throw new Error(`The select ${id} is missing.`);
  return element;
}

function button(container: HTMLElement, name: string): HTMLButtonElement {
  const found = [...container.querySelectorAll("button")].find(
    (item) => item.getAttribute("aria-label") === name || item.textContent === name,
  );
  if (!found) throw new Error(`The button ${name} is missing.`);
  return found;
}

function selectedText(element: HTMLSelectElement): string {
  return element.selectedOptions[0]?.textContent ?? "";
}

async function choose(element: HTMLSelectElement, value: string): Promise<void> {
  await act(async () => {
    element.value = value;
    element.dispatchEvent(new window.Event("change", { bubbles: true }));
  });
  await settled();
}

async function click(element: HTMLButtonElement): Promise<void> {
  await act(async () => {
    element.click();
  });
  await settled();
}

test("defaults follow the base and document sizes", async () => {
  const { container, unmount } = await mount();
  const documentSelect = select(container, DOCUMENT_ID);
  const codeSelect = select(container, CODE_ID);
  assert.equal(documentSelect.value, "follow");
  assert.equal(selectedText(documentSelect), "Follow base size (16 px)");
  assert.equal(codeSelect.value, "follow");
  assert.equal(selectedText(codeSelect), "Follow document size (14 px)");
  assert.deepEqual(
    Array.from(documentSelect.options).map((option) => option.value),
    ["follow", ...Array.from({ length: 15 }, (_, index) => String(14 + index))],
  );
  assert.equal(
    Array.from(documentSelect.options).some((option) => option.disabled),
    false,
  );
  assert.equal(writes.length, 0);
  await unmount();
});

test("the rows show their labels and descriptions", async () => {
  const { container, unmount } = await mount();
  const text = container.textContent ?? "";
  assert.ok(text.includes("Document font size"));
  assert.ok(text.includes("Used by Edit and Preview."));
  assert.ok(text.includes("Code editor font size"));
  assert.ok(text.includes("Used by Code."));
  await unmount();
});

test("choosing a document size writes it and updates the code follow text", async () => {
  const { container, unmount } = await mount();
  await choose(select(container, DOCUMENT_ID), "20");
  assert.deepEqual(writes, [stored(20, null)]);
  assert.equal(select(container, DOCUMENT_ID).value, "20");
  assert.equal(selectedText(select(container, CODE_ID)), "Follow document size (17.5 px)");
  await unmount();
});

test("a stored size of 14 is selected and shown as a plain size", async () => {
  putSettings(JSON.stringify(stored(14, null)));
  const { container, unmount } = await mount();
  const documentSelect = select(container, DOCUMENT_ID);
  assert.equal(documentSelect.value, "14");
  assert.equal(selectedText(documentSelect), "14 px");
  assert.equal(selectedText(select(container, CODE_ID)), "Follow document size (14 px)");

  await choose(documentSelect, "15");
  assert.deepEqual(writes, [stored(15, null)]);
  await unmount();
});

test("Reset document font size writes null for the document size only", async () => {
  putSettings(JSON.stringify(stored(20, 15)));
  const { container, unmount } = await mount();
  await click(button(container, "Reset document font size"));
  assert.deepEqual(writes, [stored(null, 15)]);
  assert.equal(select(container, DOCUMENT_ID).value, "follow");
  assert.equal(select(container, CODE_ID).value, "15");
  await unmount();
});

test("Reset code editor font size writes null for the code size only", async () => {
  putSettings(JSON.stringify(stored(20, 15)));
  const { container, unmount } = await mount();
  await click(button(container, "Reset code editor font size"));
  assert.deepEqual(writes, [stored(20, null)]);
  assert.equal(select(container, CODE_ID).value, "follow");
  await unmount();
});

test("Reset all writes the defaults", async () => {
  putSettings(JSON.stringify(stored(20, 15)));
  const { container, unmount } = await mount();
  await click(button(container, "Reset all"));
  assert.deepEqual(writes, [stored(null, null)]);
  assert.equal(select(container, DOCUMENT_ID).value, "follow");
  assert.equal(select(container, CODE_ID).value, "follow");
  await unmount();
});

test("a failed write notifies and returns to the last saved values", async () => {
  const { container, unmount } = await mount();
  await choose(select(container, DOCUMENT_ID), "18");
  failWritesAfter = 1;
  await choose(select(container, CODE_ID), "15");
  assert.deepEqual(writes, [stored(18, null)]);
  assert.deepEqual(notifications, [{ type: "error", message: "The disk is full." }]);
  assert.equal(select(container, DOCUMENT_ID).value, "18");
  assert.equal(select(container, CODE_ID).value, "follow");
  await unmount();
});

test("an invalid file shows the error and only Reset all works", async () => {
  putSettings(JSON.stringify({ schemaVersion: 1, documentFontSize: 11, codeFontSize: null }));
  const { container, unmount } = await mount();
  assert.ok((container.textContent ?? "").includes("Text Editor settings are invalid:"));
  assert.ok((container.textContent ?? "").includes("settings.json"));
  assert.ok((container.textContent ?? "").includes("Use Reset all to restore the defaults."));
  assert.equal(select(container, DOCUMENT_ID).disabled, true);
  assert.equal(select(container, CODE_ID).disabled, true);
  assert.equal(button(container, "Reset document font size").disabled, true);
  assert.equal(button(container, "Reset code editor font size").disabled, true);
  assert.equal(button(container, "Reset all").disabled, false);

  await click(button(container, "Reset all"));
  assert.deepEqual(writes, [stored(null, null)]);
  assert.ok(!(container.textContent ?? "").includes("Text Editor settings are invalid"));
  assert.equal(select(container, DOCUMENT_ID).disabled, false);
  assert.equal(select(container, CODE_ID).disabled, false);
  assert.equal(select(container, DOCUMENT_ID).value, "follow");
  await unmount();
});

test("a read error other than an invalid file also shows the error and only Reset all works", async () => {
  failReadsWith = "EACCES";
  const { container, unmount } = await mount();
  const text = container.textContent ?? "";
  assert.ok(text.includes("Text Editor settings are invalid: no access"), text);
  assert.ok(text.includes("Use Reset all to restore the defaults."), text);
  assert.equal(select(container, DOCUMENT_ID).disabled, true);
  assert.equal(select(container, CODE_ID).disabled, true);
  assert.equal(button(container, "Reset all").disabled, false);

  failReadsWith = null;
  await click(button(container, "Reset all"));
  assert.deepEqual(writes, [stored(null, null)]);
  assert.equal(select(container, DOCUMENT_ID).disabled, false);
  await unmount();
});

test("a failed write drops the writes queued behind it and keeps the file in step with the UI", async () => {
  putSettings(JSON.stringify(stored(18, null)));
  const fileBefore = files.get(SETTINGS_URI);
  const { container, unmount } = await mount();
  let rejectHeld!: (error: Error) => void;
  heldWrite = new Promise<void>((_, reject) => (rejectHeld = reject));

  // A 的写入被挂起，B 在它之后入队。A's write is held pending and B is queued behind it.
  await act(async () => {
    const documentSelect = select(container, DOCUMENT_ID);
    documentSelect.value = "20";
    documentSelect.dispatchEvent(new window.Event("change", { bubbles: true }));
  });
  await choose(select(container, CODE_ID), "15");
  assert.equal(writeRequests, 1);

  await act(async () => rejectHeld(createExtensionError("EIO", "The disk is full.")));
  await settled();
  assert.equal(writeRequests, 1);
  assert.deepEqual(writes, []);
  assert.deepEqual(notifications, [{ type: "error", message: "The disk is full." }]);
  assert.equal(select(container, DOCUMENT_ID).value, "18");
  assert.equal(select(container, CODE_ID).value, "follow");
  assert.equal(files.get(SETTINGS_URI), fileBefore);

  // 失败之后的修改仍然写入。A change made after the failure is still written.
  await choose(select(container, CODE_ID), "16");
  assert.deepEqual(writes, [stored(18, 16)]);
  await unmount();
});

test("a theme change updates the follow texts", async () => {
  const { container, unmount } = await mount();
  assert.equal(selectedText(select(container, DOCUMENT_ID)), "Follow base size (16 px)");

  await act(async () => {
    host.changeTheme({
      kind: "light",
      tokens: { "--agw-font-size-base": "20px", "--agw-font-size-min": "14px" },
    });
  });
  await settled();
  const documentSelect = select(container, DOCUMENT_ID);
  assert.equal(selectedText(documentSelect), "Follow base size (20 px)");
  assert.equal(selectedText(select(container, CODE_ID)), "Follow document size (17.5 px)");
  await unmount();
});
