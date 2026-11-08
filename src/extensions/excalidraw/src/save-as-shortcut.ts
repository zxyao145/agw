import type { HostContext } from "@agw/extension-sdk";

type Platform = HostContext["app"]["platform"];

/** 另存为快捷键需要的按键信息。The key information the save-as shortcut needs. */
export type ShortcutKey = Pick<
  KeyboardEvent,
  "key" | "metaKey" | "ctrlKey" | "shiftKey" | "altKey"
>;

/**
 * Excalidraw 的另存为快捷键：`Cmd+Shift+S`（macOS）或 `Ctrl+Shift+S`（其他平台），按住 Alt 时不算。按键规则与 SDK 的
 * 保存快捷键相同，再加 Shift。
 * Excalidraw's save-as shortcut: `Cmd+Shift+S` on macOS or `Ctrl+Shift+S` elsewhere, not while Alt is held. The key
 * rule is the SDK save shortcut's, plus Shift.
 */
export function isSaveAsShortcut(event: ShortcutKey, platform: Platform): boolean {
  const modifier = platform === "darwin" ? event.metaKey : event.ctrlKey;
  return modifier && event.shiftKey && !event.altKey && event.key.toLowerCase() === "s";
}

/**
 * 在捕获阶段拦下另存为快捷键。Excalidraw 0.18.1 的 saveFileToDisk 动作没有 predicate，`canvasActions.export` 为 false
 * 时它仍响应这个快捷键，打开系统的保存对话框，写到文档会话之外的文件。返回移除监听的函数。
 * Stops the save-as shortcut in the capture phase. Excalidraw 0.18.1's saveFileToDisk action has no predicate, so it
 * still answers this shortcut with `canvasActions.export` false and opens the native save dialog, writing a file
 * outside the document session. Returns a function that removes the listener.
 */
export function blockSaveAsShortcut(target: Window, platform: Platform): () => void {
  const onKeyDown = (event: KeyboardEvent) => {
    if (!isSaveAsShortcut(event, platform)) return;
    event.preventDefault();
    event.stopPropagation();
  };
  target.addEventListener("keydown", onKeyDown, true);
  return () => target.removeEventListener("keydown", onKeyDown, true);
}
