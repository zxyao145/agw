import type { HostContext } from "@agw/extension-sdk";
import type { EditorState } from "@tiptap/pm/state";
import { SearchQuery, type SearchResult } from "prosemirror-search";

// 搜索栏的搜索条件、结果与快捷键（Markdown 搜索替换设计第 2 节）。Mixed 与 Preview 用同一个 SearchQuery 判断匹配。
// The find bar's search conditions, results and shortcuts (Markdown find and replace design, section 2). Mixed and
// Preview decide matches with the same SearchQuery.

export type Platform = HostContext["app"]["platform"];

export type FindQuery = {
  search: string;
  replace: string;
  caseSensitive: boolean;
  wholeWord: boolean;
  regexp: boolean;
};

export type FindResult =
  | { status: "idle" }
  | { status: "invalid" }
  | { status: "matches"; count: number; current: number | null };

export const IDLE_RESULT: FindResult = { status: "idle" };

export const EMPTY_QUERY: FindQuery = {
  search: "",
  replace: "",
  caseSensitive: false,
  wholeWord: false,
  regexp: false,
};

/**
 * 创建 prosemirror-search 的条件。长度为 0 的匹配（例如正则表达式 `^`）不算匹配，`exclude` 返回 true 的匹配也不算。
 * Creates the prosemirror-search query. Zero-length matches (such as the regular expression `^`) do not count, nor do
 * matches `exclude` returns true for.
 */
export function createSearchQuery(
  query: FindQuery,
  exclude?: (state: EditorState, result: SearchResult) => boolean,
): SearchQuery {
  return new SearchQuery({
    search: query.search,
    replace: query.replace,
    caseSensitive: query.caseSensitive,
    wholeWord: query.wholeWord,
    regexp: query.regexp,
    filter: (state, result) => result.to > result.from && !exclude?.(state, result),
  });
}

/** 搜索文字与三个选项相同（不比较替换文字）。The search text and the three options are equal (the replace text aside). */
export function sameSearch(a: FindQuery, b: FindQuery): boolean {
  return (
    a.search === b.search &&
    a.caseSensitive === b.caseSensitive &&
    a.wholeWord === b.wholeWord &&
    a.regexp === b.regexp
  );
}

export type FindCommand =
  | "find"
  | "replace"
  | "next"
  | "previous"
  | "toggleCase"
  | "toggleWholeWord"
  | "toggleRegexp";

export type ShortcutEvent = Pick<
  KeyboardEvent,
  "key" | "code" | "metaKey" | "ctrlKey" | "altKey" | "shiftKey"
>;

/** Mod：macOS 为 Cmd，其他平台为 Ctrl。Mod: Cmd on macOS, Ctrl elsewhere. */
export function modHeld(event: Pick<KeyboardEvent, "metaKey" | "ctrlKey">, platform: Platform) {
  return platform === "darwin" ? event.metaKey : event.ctrlKey;
}

const TOGGLES: Record<string, FindCommand> = {
  c: "toggleCase",
  w: "toggleWholeWord",
  r: "toggleRegexp",
};

/**
 * 按键对应的搜索栏命令，与 Monaco 的默认快捷键相同。字母键在 Alt 未按下时按 `key` 识别（随键盘布局），按下时按 `code`
 * 识别（macOS 的 Option 会改变 `key`）。
 * The find bar command for a key press, matching Monaco's default shortcuts. Letters are read from `key` (following
 * the keyboard layout) without Alt and from `code` with Alt (Option changes `key` on macOS).
 */
export function findShortcut(event: ShortcutEvent, platform: Platform): FindCommand | null {
  const mod = modHeld(event, platform);
  const otherModifier = platform === "darwin" ? event.ctrlKey : event.metaKey;
  if (otherModifier) return null;
  if (event.key === "F3") return mod || event.altKey ? null : event.shiftKey ? "previous" : "next";
  const letter = event.altKey
    ? /^Key([A-Z])$/u.exec(event.code)?.[1]?.toLowerCase()
    : event.key.toLowerCase();
  if (letter === undefined) return null;
  if (mod && !event.altKey) {
    if (letter === "g") return event.shiftKey ? "previous" : "next";
    if (event.shiftKey) return null;
    if (letter === "f") return "find";
    if (letter === "h" && platform !== "darwin") return "replace";
    return null;
  }
  if (event.shiftKey || !event.altKey || mod !== (platform === "darwin")) return null;
  if (platform === "darwin" && letter === "f") return "replace";
  return TOGGLES[letter] ?? null;
}

export type ShortcutLabel = FindCommand | "close" | "replaceOne" | "replaceAll";

const DARWIN_LABELS: Record<ShortcutLabel, string> = {
  find: "⌘F",
  replace: "⌥⌘F",
  next: "Enter",
  previous: "⇧Enter",
  toggleCase: "⌥⌘C",
  toggleWholeWord: "⌥⌘W",
  toggleRegexp: "⌥⌘R",
  close: "Escape",
  replaceOne: "Enter",
  replaceAll: "⌘Enter",
};

const OTHER_LABELS: Record<ShortcutLabel, string> = {
  find: "Ctrl+F",
  replace: "Ctrl+H",
  next: "Enter",
  previous: "Shift+Enter",
  toggleCase: "Alt+C",
  toggleWholeWord: "Alt+W",
  toggleRegexp: "Alt+R",
  close: "Escape",
  replaceOne: "Enter",
  replaceAll: "Ctrl+Enter",
};

/** tooltip 中显示的快捷键文字。The shortcut text shown in tooltips. */
export function shortcutText(label: ShortcutLabel, platform: Platform): string {
  return (platform === "darwin" ? DARWIN_LABELS : OTHER_LABELS)[label];
}
