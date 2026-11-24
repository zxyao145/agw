import assert from "node:assert/strict";
import { test } from "node:test";

import { Schema } from "@tiptap/pm/model";
import { EditorState } from "@tiptap/pm/state";

import {
  createSearchQuery,
  EMPTY_QUERY,
  findShortcut,
  type FindCommand,
  type Platform,
  type ShortcutEvent,
} from "./find-query";

function press(
  key: string,
  code: string,
  modifiers: Partial<Pick<ShortcutEvent, "metaKey" | "ctrlKey" | "altKey" | "shiftKey">> = {},
): ShortcutEvent {
  return {
    key,
    code,
    metaKey: false,
    ctrlKey: false,
    altKey: false,
    shiftKey: false,
    ...modifiers,
  };
}

function commands(platform: Platform, events: Record<string, ShortcutEvent>) {
  return Object.fromEntries(
    Object.entries(events).map(([name, event]) => [name, findShortcut(event, platform)]),
  ) as Record<string, FindCommand | null>;
}

test("findShortcut maps Monaco's macOS shortcuts and reads Option combinations from the key code", () => {
  assert.deepEqual(
    commands("darwin", {
      "Cmd+F": press("f", "KeyF", { metaKey: true }),
      "Cmd+Option+F": press("ƒ", "KeyF", { metaKey: true, altKey: true }),
      "Cmd+G": press("g", "KeyG", { metaKey: true }),
      "Cmd+Shift+G": press("G", "KeyG", { metaKey: true, shiftKey: true }),
      F3: press("F3", "F3"),
      "Shift+F3": press("F3", "F3", { shiftKey: true }),
      "Cmd+Option+C": press("ç", "KeyC", { metaKey: true, altKey: true }),
      "Cmd+Option+W": press("∑", "KeyW", { metaKey: true, altKey: true }),
      "Cmd+Option+R": press("®", "KeyR", { metaKey: true, altKey: true }),
      "Ctrl+F": press("f", "KeyF", { ctrlKey: true }),
      "Cmd+H": press("h", "KeyH", { metaKey: true }),
      "Cmd+Shift+F": press("F", "KeyF", { metaKey: true, shiftKey: true }),
      "Option+C": press("ç", "KeyC", { altKey: true }),
      "Cmd+F3": press("F3", "F3", { metaKey: true }),
      F: press("f", "KeyF"),
    }),
    {
      "Cmd+F": "find",
      "Cmd+Option+F": "replace",
      "Cmd+G": "next",
      "Cmd+Shift+G": "previous",
      F3: "next",
      "Shift+F3": "previous",
      "Cmd+Option+C": "toggleCase",
      "Cmd+Option+W": "toggleWholeWord",
      "Cmd+Option+R": "toggleRegexp",
      "Ctrl+F": null,
      "Cmd+H": null,
      "Cmd+Shift+F": null,
      "Option+C": null,
      "Cmd+F3": null,
      F: null,
    },
  );
});

test("findShortcut maps Monaco's Windows and Linux shortcuts", () => {
  for (const platform of ["win32", "linux"] as const) {
    assert.deepEqual(
      commands(platform, {
        "Ctrl+F": press("f", "KeyF", { ctrlKey: true }),
        "Ctrl+H": press("h", "KeyH", { ctrlKey: true }),
        "Ctrl+G": press("g", "KeyG", { ctrlKey: true }),
        "Ctrl+Shift+G": press("G", "KeyG", { ctrlKey: true, shiftKey: true }),
        F3: press("F3", "F3"),
        "Shift+F3": press("F3", "F3", { shiftKey: true }),
        "Alt+C": press("c", "KeyC", { altKey: true }),
        "Alt+W": press("w", "KeyW", { altKey: true }),
        "Alt+R": press("r", "KeyR", { altKey: true }),
        "Meta+F": press("f", "KeyF", { metaKey: true }),
        "Ctrl+Alt+F": press("f", "KeyF", { ctrlKey: true, altKey: true }),
        "Alt+F": press("f", "KeyF", { altKey: true }),
        "Ctrl+Alt+C": press("c", "KeyC", { ctrlKey: true, altKey: true }),
      }),
      {
        "Ctrl+F": "find",
        "Ctrl+H": "replace",
        "Ctrl+G": "next",
        "Ctrl+Shift+G": "previous",
        F3: "next",
        "Shift+F3": "previous",
        "Alt+C": "toggleCase",
        "Alt+W": "toggleWholeWord",
        "Alt+R": "toggleRegexp",
        "Meta+F": null,
        "Ctrl+Alt+F": null,
        "Alt+F": null,
        "Ctrl+Alt+C": null,
      },
      platform,
    );
  }
});

test("findShortcut follows the keyboard layout for letters without Alt", () => {
  // AZERTY 等布局中 F 键的位置不同，按 key 识别。The F key sits elsewhere on layouts like AZERTY, so key decides.
  assert.equal(findShortcut(press("f", "KeyD", { metaKey: true }), "darwin"), "find");
  assert.equal(findShortcut(press("d", "KeyF", { metaKey: true }), "darwin"), null);
});

const schema = new Schema({
  nodes: { doc: { content: "block+" }, block: { content: "text*" }, text: {} },
});

function matches(text: string, search: string, regexp: boolean): string[] {
  const doc = schema.node("doc", null, [schema.node("block", null, [schema.text(text)])]);
  const state = EditorState.create({ doc });
  const query = createSearchQuery({ ...EMPTY_QUERY, search, regexp });
  const found: string[] = [];
  for (let pos = 0; ;) {
    const result = query.findNext(state, pos);
    if (!result) break;
    found.push(doc.textBetween(result.from, result.to));
    pos = Math.max(result.to, pos + 1);
  }
  return found;
}

test("createSearchQuery skips zero-length matches", () => {
  assert.deepEqual(matches("xx a xxx", "^", true), []);
  assert.deepEqual(matches("xx a xxx", "x*", true), ["xx", "xxx"]);
  assert.deepEqual(matches("xx a xxx", "x", false), ["x", "x", "x", "x", "x"]);
});
