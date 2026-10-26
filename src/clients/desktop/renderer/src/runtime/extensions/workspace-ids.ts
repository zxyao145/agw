/** 内置视图 id。Built-in view ids. */
export const BUILT_IN_VIEWS = {
  conversations: "agw.conversations",
  files: "agw.files",
  chat: "agw.chat",
  file: "agw.file",
} as const;

export const DEFAULT_SIDEBAR_VIEW_ID = BUILT_IN_VIEWS.conversations;
export const DEFAULT_MAIN_VIEW_ID = BUILT_IN_VIEWS.chat;

export function extensionViewId(extensionId: string, localId: string): string {
  return `${extensionId}:${localId}`;
}

/**
 * 完整 id（含 `:` 或以 `agw.` 开头）原样返回，本地 id 加上 `<extensionId>:` 前缀。
 * Full ids (with `:` or starting with `agw.`) are kept; local ids get the `<extensionId>:` prefix.
 */
export function resolveViewId(extensionId: string, id: string): string {
  return id.includes(":") || id.startsWith("agw.") ? id : extensionViewId(extensionId, id);
}

export function isBuiltInViewId(id: string): boolean {
  return id.startsWith("agw.");
}
