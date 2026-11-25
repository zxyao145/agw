import { createUuidV7 } from "@agw/api";
import type { GitDiffScope } from "@agw/projects-core";
import type {
  JsonValue,
  PersistedFloatingChat,
  PersistedTabGroup,
} from "@desktop/shared/contracts";
import { BUILT_IN_VIEWS } from "./workspace-ids";

export { tabKey } from "@desktop/shared/workspace-tab-key";

export type ChatTab = {
  id: string;
  kind: "chat";
  preview: boolean;
  /** 草稿为 null。Null for a draft. */
  conversationId: string | null;
  /** UUIDv7，用于标识输入草稿和首条消息使用的对话。UUIDv7 used for the composer draft and the conversation of the first message. */
  draftId: string;
  /** 第一条消息已经提交，正在等待确认受理。The first message was submitted and acceptance is pending. */
  draftSubmitted: boolean;
  /** 最近读取到的对话标题。The latest conversation title read. */
  title: string | null;
};

export type FileLocation = {
  directoryId: string | null;
  path: string;
  diffScope: GitDiffScope | null;
};

export type FileTab = {
  id: string;
  kind: "file";
  preview: boolean;
  /** Project 主目录为 null。Null for the Project's primary directory. */
  directoryId: string | null;
  path: string;
  diffScope: GitDiffScope | null;
  diffFileView: boolean;
};

export type JobsTab = { id: string; kind: "jobs"; preview: false };

export type ExtensionTab = {
  id: string;
  kind: "extension";
  preview: boolean;
  viewId: string;
  key: string | null;
  title: string | null;
  params: JsonValue;
  /** 每次打开增加 1，已加载的页面收到 `view.onShow`。Increments on each open so a loaded page receives `view.onShow`. */
  sequence: number;
};

export type WorkspaceTab = ChatTab | FileTab | JobsTab | ExtensionTab;

export type TabGroup = {
  tabs: readonly WorkspaceTab[];
  activeTabId: string | null;
  recentTabIds: readonly string[];
  floatingChat: PersistedFloatingChat;
};

export function newDraftId(): string {
  return createUuidV7();
}

export function newTabId(): string {
  return crypto.randomUUID();
}

export function createInitialTabGroup(): TabGroup {
  const tab: ChatTab = {
    id: newTabId(),
    kind: "chat",
    preview: false,
    conversationId: null,
    draftId: newDraftId(),
    draftSubmitted: false,
    title: null,
  };
  return {
    tabs: [tab],
    activeTabId: tab.id,
    recentTabIds: [tab.id],
    floatingChat: { conversationId: null, draftId: newDraftId(), draftSubmitted: false },
  };
}

export function toPersistedTabGroup(group: TabGroup): PersistedTabGroup {
  return {
    ...group,
    tabs: group.tabs.map((tab) => {
      if (tab.kind !== "extension") return tab;
      const { sequence: _sequence, ...persisted } = tab;
      return persisted;
    }),
    recentTabIds: [...group.recentTabIds],
  };
}

export function fromPersistedTabGroup(group: PersistedTabGroup): TabGroup {
  return {
    ...group,
    tabs: group.tabs.map((tab) => (tab.kind === "extension" ? { ...tab, sequence: 0 } : tab)),
  };
}

export function activeViewIdOf(tab: WorkspaceTab): string {
  return tab.kind === "extension" ? tab.viewId : BUILT_IN_VIEWS[tab.kind];
}

export function hasVisibleWorkspaceTabs(tabs: readonly WorkspaceTab[]): boolean {
  const [first] = tabs;
  if (!first) return false;
  return tabs.length > 1 || first.kind !== "chat" || first.conversationId !== null;
}
