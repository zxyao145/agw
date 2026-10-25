import assert from "node:assert/strict";
import { after, afterEach, test } from "node:test";

import { createExtensionError, type FileHeaderItem } from "@agw/extension-sdk";

import { connectTestHost } from "../test-host";
import { createEditHost, fileUri, settle, sha256, typeText, until } from "./test-edit-host";

// 编辑器在 jsdom 的全局对象准备好之后才导入。The editor is imported only after the jsdom globals are ready.
const { EditDocument } = await import("./edit-document");
const { publishHeaderItems } = await import("./header-items");

const host = createEditHost();
// 同一个页面中只有一个 Mixed 文档：每个用例结束时释放它打开的文档，选择事件只交给当前用例的条目。
// A page holds one Mixed document: each test releases what it opened, so choices reach only the current test's items.
const cleanups: (() => void)[] = [];
afterEach(async () => {
  for (const cleanup of cleanups.splice(0)) cleanup();
  await settle();
});
after(() => host.close());

/** 打开文件并开始声明条目，等到读取完成。Opens a file, starts declaring the items and waits for the read. */
async function open(name: string, text: string | Uint8Array) {
  host.files.set(name, text);
  const document = new EditDocument(host.api, { uri: fileUri(name), name });
  const items = publishHeaderItems(host.api, document.session);
  cleanups.push(() => {
    items.dispose();
    document.dispose();
  });
  await until(() => document.session.state !== "loading", `${name} to load`);
  await settle();
  return { document, items };
}

function lastItems(): FileHeaderItem[] | undefined {
  return host.fileHeaderItems.at(-1);
}

function encodingItem(label: string): FileHeaderItem {
  return {
    id: "encoding",
    placement: "top",
    label,
    menu: [
      {
        id: "reopen",
        label: "Reopen with Encoding",
        menu: [
          { id: "reopen:utf-8", label: "Unicode (UTF-8)" },
          { id: "reopen:utf-16le", label: "Unicode (UTF-16LE)" },
          { id: "reopen:gb18030", label: "Chinese Simplified (GB18030)" },
        ],
      },
      {
        id: "save",
        label: "Save with Encoding",
        menu: [
          { id: "save:utf-8", label: "Unicode (UTF-8)" },
          { id: "save:utf-8:bom", label: "Unicode (UTF-8) with BOM" },
          { id: "save:utf-16le", label: "Unicode (UTF-16LE)" },
          { id: "save:utf-16le:bom", label: "Unicode (UTF-16LE) with BOM" },
          { id: "save:gb18030", label: "Chinese Simplified (GB18030)" },
        ],
      },
    ],
  };
}

function saveItem(dirty: boolean): FileHeaderItem {
  return { id: "save", placement: "top", label: "Save", disabled: !dirty };
}

test("a loaded document declares the encoding and Save items, and only a state change sends them again", async () => {
  const sent = host.fileHeaderItems.length;
  const { document } = await open("item.md", "Text.\n");
  assert.deepEqual(host.fileHeaderItems.slice(sent), [[encodingItem("UTF-8"), saveItem(false)]]);

  // 第一次输入使 Save 可用，之后的输入不再发送。The first keystroke makes Save available; later ones send nothing.
  document.editor.commands.setTextSelection(3);
  typeText(document.editor, "x");
  await settle();
  typeText(document.editor, "y");
  await settle();
  assert.equal(document.session.state, "dirty");
  assert.deepEqual(host.fileHeaderItems.slice(sent + 1), [[encodingItem("UTF-8"), saveItem(true)]]);
});

test("choosing Save writes the document and makes Save unavailable again", async () => {
  const { document } = await open("save.md", "Text.\n");
  document.editor.commands.setTextSelection(3);
  typeText(document.editor, "x");
  await settle();
  assert.deepEqual(lastItems(), [encodingItem("UTF-8"), saveItem(true)]);

  const writes = host.writes.length;
  host.selectFileHeaderItem("save", null);
  await until(() => document.session.state === "clean", "the save");
  await settle();
  assert.deepEqual(host.writes.slice(writes), [{ name: "save.md", text: "Texxt.\n" }]);
  assert.deepEqual(lastItems(), [encodingItem("UTF-8"), saveItem(false)]);
});

test("choosing an encoding entry reopens or saves in that encoding, and the label follows the session", async () => {
  const { document } = await open("choose.md", "Text.\n");
  const { session } = document;

  host.selectFileHeaderItem("encoding", "reopen:gb18030");
  await until(() => session.format?.encoding === "gb18030", "the file reopened as GB18030");
  await settle();
  assert.deepEqual(lastItems(), [encodingItem("GB18030"), saveItem(false)]);

  const writes = host.writes.length;
  host.selectFileHeaderItem("encoding", "save:utf-8:bom");
  await until(() => session.format?.bom === true, "the file saved as UTF-8 with BOM");
  await settle();
  assert.deepEqual(host.writes.slice(writes), [{ name: "choose.md", text: "Text.\n" }]);
  assert.deepEqual(session.format, { encoding: "utf-8", bom: true, eol: "\n" });
  assert.deepEqual(lastItems(), [encodingItem("UTF-8 with BOM"), saveItem(false)]);

  // 其他条目、编码条目本身与已经不存在的菜单项都不执行操作。Other items, the encoding item itself and entries that no
  // longer exist do nothing.
  const notifications = host.notifications.length;
  host.selectFileHeaderItem("other", "reopen:gb18030");
  host.selectFileHeaderItem("encoding", null);
  host.selectFileHeaderItem("encoding", "reopen:latin1");
  await settle();
  assert.deepEqual(session.format, { encoding: "utf-8", bom: true, eol: "\n" });
  assert.equal(host.writes.length, writes + 1);
  assert.equal(host.notifications.length, notifications);
});

test("a failed session declares no items, and disposing clears the declared items", async () => {
  const sent = host.fileHeaderItems.length;
  const binary = await open("binary.md", new Uint8Array([0, 1, 2]));
  assert.equal(binary.document.session.state, "failed");
  assert.equal(host.fileHeaderItems.length, sent);

  const text = await open("dispose.md", "Text.\n");
  assert.deepEqual(lastItems(), [encodingItem("UTF-8"), saveItem(false)]);
  text.items.dispose();
  await settle();
  assert.deepEqual(lastItems(), []);
  // 释放后会话的变化不再发送条目。Session changes after disposal send no items.
  const afterDispose = host.fileHeaderItems.length;
  await text.document.session.reload();
  await settle();
  assert.equal(host.fileHeaderItems.length, afterDispose);
});

test("when the encoding list cannot be read, a notification shows and only Save is declared", async () => {
  const notifications: string[] = [];
  const declared: unknown[] = [];
  const failing = connectTestHost(async (method, params) => {
    const values = params as Record<string, unknown>;
    switch (method) {
      case "fs.readTextDocument":
        return {
          text: "Text.\n",
          encoding: "utf-8",
          bom: false,
          eol: "\n",
          sha256: sha256("Text.\n"),
        };
      case "fs.listTextEncodings":
        throw createExtensionError("ERR_UNAVAILABLE", "The text codec is unavailable.");
      case "ui.notify":
        notifications.push(String(values.message));
        return undefined;
      case "workspace.setFileHeaderItems":
        declared.push(values.items);
        return undefined;
      case "documents.setSaveStatus":
      case "workspace.setUnsavedChanges":
        return undefined;
      default:
        throw new Error(`Unexpected RPC ${method}`);
    }
  });
  const document = new EditDocument(failing.api, {
    uri: fileUri("failing.md"),
    name: "failing.md",
  });
  const items = publishHeaderItems(failing.api, document.session);
  await until(() => notifications.length > 0, "the notification");
  await until(() => document.session.state === "clean", "failing.md to load");
  await settle();
  assert.deepEqual(notifications, ["The text codec is unavailable."]);
  assert.deepEqual(declared, [[saveItem(false)]]);
  items.dispose();
  document.dispose();
  await settle();
  failing.close();
});
