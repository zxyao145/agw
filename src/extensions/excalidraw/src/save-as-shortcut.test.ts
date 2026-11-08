import assert from "node:assert/strict";
import test from "node:test";

import { JSDOM } from "jsdom";

import { blockSaveAsShortcut, isSaveAsShortcut } from "./save-as-shortcut";

const keys = { key: "s", metaKey: false, ctrlKey: false, shiftKey: false, altKey: false };

test("isSaveAsShortcut is Cmd+Shift+S on macOS and Ctrl+Shift+S elsewhere", () => {
  assert.equal(
    isSaveAsShortcut({ ...keys, key: "S", metaKey: true, shiftKey: true }, "darwin"),
    true,
  );
  assert.equal(isSaveAsShortcut({ ...keys, metaKey: true, shiftKey: true }, "darwin"), true);
  assert.equal(
    isSaveAsShortcut({ ...keys, key: "S", ctrlKey: true, shiftKey: true }, "darwin"),
    false,
  );
  for (const platform of ["win32", "linux"] as const) {
    const ctrl = { ...keys, key: "S", ctrlKey: true, shiftKey: true };
    assert.equal(isSaveAsShortcut(ctrl, platform), true, platform);
    const meta = { ...keys, key: "S", metaKey: true, shiftKey: true };
    assert.equal(isSaveAsShortcut(meta, platform), false, platform);
  }
});

test("isSaveAsShortcut leaves the save shortcut, Alt and other keys alone", () => {
  assert.equal(isSaveAsShortcut({ ...keys, metaKey: true }, "darwin"), false);
  const withAlt = { ...keys, key: "S", metaKey: true, shiftKey: true, altKey: true };
  assert.equal(isSaveAsShortcut(withAlt, "darwin"), false);
  assert.equal(
    isSaveAsShortcut({ ...keys, key: "D", metaKey: true, shiftKey: true }, "darwin"),
    false,
  );
});

test("the capture-phase listener stops Mod+Shift+S before the page's listeners and lets other keys through", () => {
  const { window } = new JSDOM("<!doctype html><html><body><canvas></canvas></body></html>");
  const canvas = window.document.querySelector("canvas")!;
  // handleKeyboardGlobally 让 Excalidraw 在 document 上监听按键。handleKeyboardGlobally makes Excalidraw listen for
  // keys on the document.
  const reached: string[] = [];
  window.document.addEventListener("keydown", (event) => reached.push(event.key));
  const press = (init: KeyboardEventInit) => {
    const event = new window.KeyboardEvent("keydown", { bubbles: true, cancelable: true, ...init });
    canvas.dispatchEvent(event);
    return event;
  };
  const dispose = blockSaveAsShortcut(window as unknown as Window, "darwin");
  assert.equal(press({ key: "S", metaKey: true, shiftKey: true }).defaultPrevented, true);
  assert.equal(press({ key: "s", metaKey: true }).defaultPrevented, false);
  assert.equal(press({ key: "S", ctrlKey: true, shiftKey: true }).defaultPrevented, false);
  assert.deepEqual(reached, ["s", "S"]);
  // 移除监听后按键照常到达页面。Once the listener is removed the key reaches the page as usual.
  dispose();
  assert.equal(press({ key: "S", metaKey: true, shiftKey: true }).defaultPrevented, false);
  assert.deepEqual(reached, ["s", "S", "S"]);
});
