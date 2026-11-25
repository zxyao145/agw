import type { JsonValue } from "@agw/extension-sdk/workspace-params";

export type { JsonValue } from "@agw/extension-sdk/workspace-params";

export type WorkspaceTabScope = { serverId: string; projectId: string };

export function sameWorkspaceTabScope(
  left: WorkspaceTabScope | null | undefined,
  right: WorkspaceTabScope | null | undefined,
): boolean {
  return left?.serverId === right?.serverId && left?.projectId === right?.projectId;
}

export type PersistedChatTab = {
  id: string;
  kind: "chat";
  preview: boolean;
  conversationId: string | null;
  draftId: string;
  draftSubmitted: boolean;
  title: string | null;
};

export type PersistedFileTab = {
  id: string;
  kind: "file";
  preview: boolean;
  directoryId: string | null;
  path: string;
  diffScope: "staged" | "unstaged" | null;
  diffFileView: boolean;
};

export type PersistedJobsTab = { id: string; kind: "jobs"; preview: false };

export type PersistedExtensionTab = {
  id: string;
  kind: "extension";
  preview: boolean;
  viewId: string;
  key: string | null;
  title: string | null;
  params: JsonValue;
};

export type PersistedTab =
  | PersistedChatTab
  | PersistedFileTab
  | PersistedJobsTab
  | PersistedExtensionTab;

export type PersistedFloatingChat = {
  conversationId: string | null;
  draftId: string;
  draftSubmitted: boolean;
};

export type PersistedTabGroup = {
  tabs: PersistedTab[];
  activeTabId: string | null;
  recentTabIds: string[];
  floatingChat: PersistedFloatingChat;
};

export type WorkspaceTabsFileContent = {
  schemaVersion: 1;
  servers: Record<string, Record<string, PersistedTabGroup>>;
};

export type WorkspaceCommand = "closeTab" | "nextTab" | "previousTab";
