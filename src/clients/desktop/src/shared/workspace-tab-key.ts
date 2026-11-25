export type TabIdentity =
  | { kind: "chat"; conversationId: string | null; draftId: string }
  | { kind: "file"; directoryId: string | null; path: string }
  | { kind: "jobs" }
  | { kind: "extension"; viewId: string; key: string | null };

export function tabKey(tab: TabIdentity): string {
  switch (tab.kind) {
    case "chat":
      return tab.conversationId === null ? `draft:${tab.draftId}` : `chat:${tab.conversationId}`;
    case "file":
      return `file:${tab.directoryId ?? ""}:${tab.path}`;
    case "jobs":
      return "jobs";
    case "extension":
      return tab.key === null ? `view:${tab.viewId}` : `view:${tab.viewId}:${tab.key}`;
  }
}
