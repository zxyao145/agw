// 测试用：jsdom 提供 window 与 document，SDK 的 `createExtensionApi` 经 MessageChannel 调用这里的宿主端 RPC 处理函数，
// 宿主端的文件保存在内存中，写入时检查写入前提。
// For tests: jsdom provides window and document; the real SDK `createExtensionApi` calls the host-side RPC handler here
// over a MessageChannel. Host files live in memory and writes check their precondition.
import { createHash } from "node:crypto";

import { createExtensionError } from "@agw/extension-sdk";
import type { Editor } from "@tiptap/core";
import { JSDOM } from "jsdom";

import { connectTestHost, type TestHost } from "../test-host";

const { window } = new JSDOM("<!doctype html><html><body></body></html>", {
  pretendToBeVisual: true,
  url: "http://localhost/",
});
// jsdom 没有排版，Range 也没有几何方法；与 jsdom 的元素一样返回空的结果，ProseMirror 计算浮层位置时才能运行。
// jsdom has no layout and Range lacks geometry methods; they return empty results like jsdom's elements do, so
// ProseMirror can compute popover positions.
window.Range.prototype.getClientRects = function getClientRects() {
  return [] as unknown as DOMRectList;
};
window.Range.prototype.getBoundingClientRect = function getBoundingClientRect() {
  return new window.DOMRect();
};
// lib0（drag handle 经 yjs 引用）读取 localStorage，这里提供 jsdom 的存储。
// lib0 (pulled in by the drag handle through yjs) reads localStorage; this provides jsdom's storage.
Object.defineProperty(globalThis, "localStorage", {
  configurable: true,
  value: window.localStorage,
});
Object.assign(globalThis, {
  window,
  document: window.document,
  DOMParser: window.DOMParser,
  DOMRect: window.DOMRect,
  Element: window.Element,
  HTMLElement: window.HTMLElement,
  Node: window.Node,
  getComputedStyle: window.getComputedStyle.bind(window),
  requestAnimationFrame: window.requestAnimationFrame.bind(window),
  cancelAnimationFrame: window.cancelAnimationFrame.bind(window),
  IS_REACT_ACT_ENVIRONMENT: true,
});

export const ROOT = "agw-cloud://local/project-1/workspace/";

export function sha256(text: string): string {
  return createHash("sha256").update(text).digest("hex");
}

export type Notification = { type: string; message: string };

export type EditHost = TestHost & {
  /** 宿主端的文件，键为文件名。Host-side files keyed by name. */
  readonly files: Map<string, string | Uint8Array>;
  /** 每次写入的文件名与文字。The name and text of every write. */
  readonly writes: { name: string; text: string }[];
  readonly notifications: Notification[];
  /** 每次 `setUnsavedChanges` 的值。Every `setUnsavedChanges` value. */
  readonly unsaved: boolean[];
  /** 每次报告的保存状态（`documents.setSaveStatus`）。Every reported save status (`documents.setSaveStatus`). */
  readonly saveStatuses: (string | null)[];
  readonly opened: { method: string; target: string }[];
  /** 下一次对话框的回答（按钮下标）。The answer (button index) to the next message box. */
  answers: number[];
  readonly messageBoxes: unknown[];
  /** 设置后，下一次写入以这个错误码失败。When set, the next write fails with this code. */
  failNextWrite: string | null;
};

function nameOf(uri: string): string {
  if (!uri.startsWith(ROOT)) throw new Error(`Not a test file URI: ${uri}`);
  return uri.slice(ROOT.length);
}

export function fileUri(name: string): string {
  return `${ROOT}${name}`;
}

/** 内存中的宿主：Edit 用到的 RPC。An in-memory host answering the RPCs Edit uses. */
export function createEditHost(): EditHost {
  const files = new Map<string, string | Uint8Array>();
  const host: Omit<EditHost, keyof TestHost> = {
    files,
    writes: [],
    notifications: [],
    unsaved: [],
    saveStatuses: [],
    opened: [],
    answers: [],
    messageBoxes: [],
    failNextWrite: null,
  };
  const testHost = connectTestHost(async (method, params) => {
    const values = params as Record<string, unknown>;
    switch (method) {
      case "fs.readTextDocument": {
        const name = nameOf(String(values.uri));
        const text = files.get(name);
        if (text === undefined) throw createExtensionError("ENOENT", `${name} does not exist.`);
        if (typeof text !== "string")
          throw createExtensionError("ERR_NOT_TEXT", `${name} is binary.`);
        return {
          text,
          encoding: "utf-8",
          bom: false,
          eol: text.includes("\r\n") ? "\r\n" : "\n",
          sha256: sha256(text),
        };
      }
      case "fs.writeTextDocument": {
        const name = nameOf(String(values.uri));
        const current = files.get(name);
        if (host.failNextWrite !== null) {
          const code = host.failNextWrite;
          host.failNextWrite = null;
          throw createExtensionError(code, `The write of ${name} failed.`);
        }
        if (typeof current !== "string" || sha256(current) !== values.expectedSha256) {
          throw createExtensionError("ERR_FILE_CHANGED", `${name} changed.`);
        }
        const text = String(values.text).replaceAll("\n", String(values.eol));
        files.set(name, text);
        host.writes.push({ name, text });
        return { sha256: sha256(text) };
      }
      case "fs.readFile": {
        const name = nameOf(String(values.uri));
        const bytes = files.get(name);
        if (!(bytes instanceof Uint8Array))
          throw createExtensionError("ENOENT", `${name} does not exist.`);
        return bytes;
      }
      case "fs.listTextEncodings":
        return [{ id: "utf-8", label: "Unicode (UTF-8)" }];
      case "workspace.setUnsavedChanges":
        host.unsaved.push(Boolean(values.unsaved));
        return undefined;
      case "documents.setSaveStatus":
        host.saveStatuses.push(values.status as string | null);
        return undefined;
      case "ui.notify":
        host.notifications.push(params as Notification);
        return undefined;
      case "workspace.openFile":
        host.opened.push({ method, target: String(values.uri) });
        return undefined;
      case "ui.openExternal":
        host.opened.push({ method, target: String(values.url) });
        return undefined;
      case "dialog.showMessageBox": {
        host.messageBoxes.push(params);
        const answer = host.answers.shift();
        if (answer === undefined) throw new Error("No answer was prepared for the message box.");
        return answer;
      }
      default:
        throw new Error(`Unexpected RPC ${method}`);
    }
  });
  return Object.assign(testHost, host);
}

/**
 * 像浏览器输入文字一样逐个字符交给编辑器的 `handleTextInput`（输入规则在这里运行），没有处理时插入文字。
 * Hands every character to the editor's `handleTextInput` as browser typing does (input rules run there) and inserts
 * the text when nothing handled it.
 */
export function typeText(editor: Editor, text: string): void {
  for (const char of text) {
    const { view } = editor;
    const { from, to } = view.state.selection;
    const insert = () => view.state.tr.insertText(char, from, to);
    if (!view.someProp("handleTextInput", (handle) => handle(view, from, to, char, insert))) {
      view.dispatch(insert());
    }
  }
}

// 与 prosemirror-keymap 相同的平台判断：Mod 在 macOS 上是 Meta，其他平台是 Ctrl。
// The same platform check as prosemirror-keymap: Mod is Meta on macOS and Ctrl elsewhere.
const MAC = /Mac|iP(hone|[oa]d)/.test(globalThis.navigator?.platform ?? "");

/** 在编辑器上按键（keydown）。Presses a key (keydown) in the editor. */
export function pressKey(
  editor: Editor,
  key: string,
  modifiers: { mod?: boolean; shift?: boolean; alt?: boolean } = {},
): KeyboardEvent {
  const event = new window.KeyboardEvent("keydown", {
    key,
    bubbles: true,
    cancelable: true,
    metaKey: Boolean(modifiers.mod) && MAC,
    ctrlKey: Boolean(modifiers.mod) && !MAC,
    shiftKey: Boolean(modifiers.shift),
    altKey: Boolean(modifiers.alt),
  });
  editor.view.dom.dispatchEvent(event);
  return event;
}

/**
 * 在编辑器上粘贴：`data` 为剪贴板中每种类型的内容。jsdom 没有 DataTransfer，事件的 clipboardData 只提供 ProseMirror 读取的
 * `types`、`getData` 与 `files`。
 * Pastes into the editor; `data` holds the clipboard content per type. jsdom has no DataTransfer, so the event's
 * clipboardData offers only the `types`, `getData` and `files` that ProseMirror reads.
 */
export function paste(editor: Editor, data: Record<string, string>): void {
  const event = new window.Event("paste", { bubbles: true, cancelable: true });
  Object.defineProperty(event, "clipboardData", {
    value: { types: Object.keys(data), getData: (type: string) => data[type] ?? "", files: [] },
  });
  editor.view.dom.dispatchEvent(event);
}

/** 等待条件成立；每次检查之间让出事件循环。Waits for a condition, yielding to the event loop between checks. */
export async function until(condition: () => boolean, what: string): Promise<void> {
  for (let attempt = 0; attempt < 300; attempt += 1) {
    if (condition()) return;
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
  throw new Error(`Timed out waiting for ${what}.`);
}

/** 让页面与宿主之间的 RPC 往返完成。Lets page/host RPC round trips finish. */
export async function settle(): Promise<void> {
  for (let round = 0; round < 5; round += 1) await new Promise((resolve) => setTimeout(resolve, 0));
}
