import assert from "node:assert/strict";
import test from "node:test";

import { readSaveShortcut } from "./save-shortcut";

const keys = { key: "s", metaKey: false, ctrlKey: false, shiftKey: false, altKey: false };

test("readSaveShortcut uses Cmd on macOS and Ctrl elsewhere", () => {
  assert.equal(readSaveShortcut({ ...keys, metaKey: true }, "darwin"), "save");
  assert.equal(readSaveShortcut({ ...keys, ctrlKey: true }, "darwin"), null);
  assert.equal(readSaveShortcut({ ...keys, ctrlKey: true }, "win32"), "save");
  assert.equal(readSaveShortcut({ ...keys, ctrlKey: true }, "linux"), "save");
  assert.equal(readSaveShortcut({ ...keys, metaKey: true }, "linux"), null);
});

test("readSaveShortcut reports Shift as save-as", () => {
  assert.equal(
    readSaveShortcut({ ...keys, key: "S", metaKey: true, shiftKey: true }, "darwin"),
    "saveAs",
  );
  assert.equal(
    readSaveShortcut({ ...keys, key: "S", ctrlKey: true, shiftKey: true }, "win32"),
    "saveAs",
  );
});

test("readSaveShortcut ignores other keys and Alt", () => {
  assert.equal(readSaveShortcut(keys, "darwin"), null);
  assert.equal(readSaveShortcut({ ...keys, key: "d", metaKey: true }, "darwin"), null);
  assert.equal(readSaveShortcut({ ...keys, metaKey: true, altKey: true }, "darwin"), null);
});
