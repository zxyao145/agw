/** 内置视图 id。Built-in view ids. */
export const BUILT_IN_VIEWS = {
  conversations: "agw.conversations",
  files: "agw.files",
  chat: "agw.chat",
  file: "agw.file",
  jobs: "agw.jobs",
  events: "agw.events",
  notifications: "agw.notifications",
} as const;

export const DEFAULT_SIDEBAR_VIEW_ID = BUILT_IN_VIEWS.conversations;
export const FULL_WORKSPACE_VIEWS: ReadonlySet<string> = new Set([
  BUILT_IN_VIEWS.events,
  BUILT_IN_VIEWS.notifications,
]);

/** 侧栏视图的实例键。The instance key of a sidebar view. */
export function sidebarInstanceKey(viewId: string): string {
  return `sidebar:${viewId}`;
}

const TAB_PREFIX = "tab:";
const SETTINGS_PREFIX = "settings:";

/** extension 标签的实例键。The instance key of an extension tab. */
export function tabInstanceKey(tabId: string): string {
  return `${TAB_PREFIX}${tabId}`;
}

/** 文件标签中一个查看器的实例键。The instance key of one viewer in a file tab. */
export function fileViewerInstanceKey(tabId: string, viewId: string): string {
  return `${tabInstanceKey(tabId)}/${viewId}`;
}

export function tabIdOfInstance(key: string): string | null {
  return key.startsWith(TAB_PREFIX) ? key.slice(TAB_PREFIX.length).split("/", 1)[0] : null;
}

/** 设置页的实例键。The instance key of a settings page. */
export function settingsInstanceKey(extensionId: string): string {
  return `${SETTINGS_PREFIX}${extensionId}`;
}

export function settingsExtensionId(key: string): string | null {
  return key.startsWith(SETTINGS_PREFIX) ? key.slice(SETTINGS_PREFIX.length) : null;
}

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
