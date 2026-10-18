export type SaveShortcut = "save" | "saveAs";

/**
 * 识别保存快捷键：macOS 为 Cmd+S，其他平台为 Ctrl+S；再按 Shift 为 Excalidraw 的另存为。
 * Recognizes the save shortcut: Cmd+S on macOS, Ctrl+S elsewhere; adding Shift is Excalidraw's save-as.
 */
export function readSaveShortcut(
  event: Pick<KeyboardEvent, "key" | "metaKey" | "ctrlKey" | "shiftKey" | "altKey">,
  platform: string,
): SaveShortcut | null {
  const primary = platform === "darwin" ? event.metaKey : event.ctrlKey;
  if (!primary || event.altKey || event.key.toLowerCase() !== "s") return null;
  return event.shiftKey ? "saveAs" : "save";
}
