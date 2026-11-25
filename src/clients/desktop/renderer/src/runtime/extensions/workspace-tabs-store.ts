import type { AgwDesktopBridge, JsonValue } from "@desktop/shared/contracts";
import {
  sameWorkspaceTabScope,
  type WorkspaceTabScope,
} from "@desktop/shared/contracts/workspace-tabs";

import {
  createInitialTabGroup,
  fromPersistedTabGroup,
  newDraftId,
  newTabId,
  tabKey,
  toPersistedTabGroup,
  type ChatTab,
  type FileLocation,
  type FileTab,
  type TabGroup,
  type WorkspaceTab,
} from "./workspace-tab";

export type TabGroupScope = WorkspaceTabScope;

export type WorkspaceTabsSnapshot = {
  scope: TabGroupScope | null;
  group: TabGroup | null;
  error: Error | null;
};

export type TabResources = {
  confirmDiscard(tabIds: readonly string[]): Promise<boolean>;
  release(tabIds: readonly string[], keepUnsaved: boolean): void;
  discardDrafts(scope: TabGroupScope, draftIds: readonly string[]): void;
};

export type WorkspaceTabsBridge = Pick<AgwDesktopBridge, "getWorkspaceTabs" | "saveWorkspaceTabs">;
export type ExtensionTabOptions = { key: string | null; title: string | null; preview: boolean };

export function activeTabOf(group: TabGroup | null): WorkspaceTab | undefined {
  return group?.tabs.find((tab) => tab.id === group.activeTabId);
}

export function workspaceChatState(
  group: TabGroup | null,
  fullViewId: string | null,
  open: boolean,
) {
  if (!group) return { floatingAllowed: false, session: null };
  const tab = fullViewId === null ? activeTabOf(group) : undefined;
  if (tab?.kind === "chat") {
    return { floatingAllowed: false, session: { id: tab.id, conversationId: tab.conversationId } };
  }
  const { draftId, conversationId } = group.floatingChat;
  return { floatingAllowed: true, session: open ? { id: draftId, conversationId } : null };
}

function newFloatingDraft(): TabGroup["floatingChat"] {
  return { conversationId: null, draftId: newDraftId(), draftSubmitted: false };
}

export class WorkspaceTabsStore {
  private snapshot: WorkspaceTabsSnapshot = { scope: null, group: null, error: null };
  private readonly listeners = new Set<() => void>();
  private generation = 0;
  private loading = Promise.resolve();

  public readonly floatingChat = {
    newConversation: this.newFloatingConversation.bind(this),
    acceptConversation: this.acceptFloatingConversation.bind(this),
    setDraftSubmitted: this.setFloatingDraftSubmitted.bind(this),
  };

  public constructor(
    private readonly options: {
      bridge: WorkspaceTabsBridge;
      resources: TabResources;
      reportError(message: string, error: unknown): void;
      onActivate?(): void;
    },
  ) {}

  public getSnapshot = (): WorkspaceTabsSnapshot => this.snapshot;

  public subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  public setScope(scope: TabGroupScope | null): Promise<void> {
    const previous = this.snapshot;
    if (sameWorkspaceTabScope(scope, previous.scope)) return this.loading;

    const generation = ++this.generation;
    if (previous.group) {
      this.options.resources.release(
        previous.group.tabs.map((tab) => tab.id),
        true,
      );
    }
    this.snapshot = { scope, group: null, error: null };
    this.loading = scope ? this.load(scope, generation) : Promise.resolve();
    this.publish();
    return this.loading;
  }

  private async load(scope: TabGroupScope, generation: number): Promise<void> {
    const { serverId, projectId } = scope;
    let saved: Awaited<ReturnType<WorkspaceTabsBridge["getWorkspaceTabs"]>>;
    try {
      saved = await this.options.bridge.getWorkspaceTabs(serverId, projectId);
    } catch (error) {
      if (this.generation !== generation) return;
      this.snapshot = {
        scope,
        group: null,
        error: error instanceof Error ? error : new Error(String(error)),
      };
      this.publish();
      return;
    }
    if (this.generation !== generation) return;
    if (saved === null) {
      this.commit(createInitialTabGroup());
      return;
    }
    this.snapshot = { scope, group: fromPersistedTabGroup(saved), error: null };
    this.publish();
  }

  public async whenLoaded(): Promise<void> {
    const generation = this.generation;
    await this.loading;
    if (generation !== this.generation) return;
    if (this.snapshot.error) throw this.snapshot.error;
    this.requireGroup();
  }

  public openConversation(conversationId: string, options: { preview: boolean }): void {
    const group = this.requireGroup();
    const pending = group.tabs.find(
      (tab): tab is ChatTab =>
        tab.kind === "chat" &&
        tab.conversationId === null &&
        tab.draftSubmitted &&
        tab.draftId === conversationId,
    );
    if (pending) {
      this.open({ ...pending, preview: options.preview }, group);
      return;
    }
    const { floatingChat } = group;
    if (floatingChat.draftSubmitted && floatingChat.draftId === conversationId) {
      this.open(
        { ...floatingChat, id: newTabId(), kind: "chat", title: null, ...options },
        { ...group, floatingChat: newFloatingDraft() },
      );
      return;
    }
    const floating = floatingChat.conversationId === conversationId;
    this.open(
      {
        ...newFloatingDraft(),
        id: newTabId(),
        kind: "chat",
        conversationId,
        title: null,
        ...options,
      },
      floating ? { ...group, floatingChat: newFloatingDraft() } : group,
    );
  }

  public openNewConversation(): void {
    this.open({ ...newFloatingDraft(), id: newTabId(), kind: "chat", title: null, preview: false });
  }

  public openFile(
    file: FileLocation,
    options: { preview: boolean; replaceDiffView?: boolean },
  ): void {
    this.open(
      { ...file, preview: options.preview, id: newTabId(), kind: "file", diffFileView: false },
      this.requireGroup(),
      options.replaceDiffView,
    );
  }

  public openJobs(): void {
    this.open({ id: newTabId(), kind: "jobs", preview: false });
  }

  public openExtensionView(viewId: string, params: JsonValue, options: ExtensionTabOptions): void {
    this.open({ id: newTabId(), kind: "extension", viewId, params, sequence: 0, ...options });
  }

  public activate(tabId: string): void {
    const group = this.requireGroup();
    if (!group.tabs.some((tab) => tab.id === tabId)) throw new Error(`Unknown tab: ${tabId}.`);
    this.activateGroup(group, tabId);
  }

  public activateAdjacent(direction: 1 | -1): void {
    const group = this.requireGroup();
    const { tabs } = group;
    if (tabs.length === 0) return;
    const index = tabs.findIndex((tab) => tab.id === group.activeTabId);
    const adjacent =
      index < 0
        ? direction === 1
          ? 0
          : tabs.length - 1
        : (index + direction + tabs.length) % tabs.length;
    this.activateGroup(group, tabs[adjacent].id);
  }

  public pin(tabId: string): void {
    this.updateTab(tabId, (tab) => (tab.preview ? { ...tab, preview: false } : tab));
  }

  public move(tabId: string, index: number): void {
    const group = this.requireGroup();
    const previousIndex = group.tabs.findIndex((tab) => tab.id === tabId);
    if (previousIndex < 0) throw new Error(`Unknown tab: ${tabId}.`);
    const tab = group.tabs[previousIndex];
    const target = Math.min(Math.max(index, 0), group.tabs.length - 1);
    if (previousIndex === target && !tab.preview) return;
    const tabs = [...group.tabs];
    tabs.splice(previousIndex, 1);
    tabs.splice(target, 0, { ...tab, preview: false });
    this.commit({ ...group, tabs });
  }

  public async close(tabIds: readonly string[]): Promise<boolean> {
    const group = this.requireGroup();
    const generation = this.generation;
    const closing = group.tabs.filter((tab) => tabIds.includes(tab.id)).map((tab) => tab.id);
    if (closing.length === 0) return true;
    if (!(await this.options.resources.confirmDiscard(closing)) || this.generation !== generation) {
      return false;
    }
    this.remove(this.requireGroup(), (tab) => closing.includes(tab.id));
    return true;
  }

  public acceptConversation(tabId: string, conversationId: string): void {
    const group = this.requireGroup();
    if (
      group.tabs.some(
        (tab) => tab.id !== tabId && tab.kind === "chat" && tab.conversationId === conversationId,
      )
    ) {
      throw new Error(`Conversation ${conversationId} already has a tab.`);
    }
    this.updateTab(tabId, (tab) => {
      if (tab.kind !== "chat" || tab.draftId !== conversationId) {
        throw new Error("The accepted conversation id must match the draft id.");
      }
      return tab.conversationId === conversationId
        ? tab
        : { ...tab, conversationId, draftSubmitted: false };
    });
  }

  public setDraftSubmitted(tabId: string, submitted: boolean): void {
    this.updateTab(tabId, (tab) =>
      tab.kind === "chat" && tab.conversationId === null && tab.draftSubmitted !== submitted
        ? { ...tab, draftSubmitted: submitted }
        : tab,
    );
  }

  public setChatTitle(tabId: string, title: string): void {
    this.updateTab(tabId, (tab) =>
      tab.kind === "chat" && tab.title !== title ? { ...tab, title } : tab,
    );
  }

  public setFileView(tabId: string, view: Pick<FileTab, "diffScope" | "diffFileView">): void {
    this.updateTab(tabId, (tab) =>
      tab.kind === "file" &&
      (tab.diffScope !== view.diffScope || tab.diffFileView !== view.diffFileView)
        ? { ...tab, ...view }
        : tab,
    );
  }

  public removeConversations(conversationIds: readonly string[] | "all"): void {
    const group = this.requireGroup();
    const matches = ({ conversationId, draftId, draftSubmitted }: TabGroup["floatingChat"]) =>
      conversationIds === "all" ||
      (conversationId !== null && conversationIds.includes(conversationId)) ||
      (draftSubmitted && conversationIds.includes(draftId));
    this.remove(
      matches(group.floatingChat) ? this.replaceFloatingDraft(group) : group,
      (tab) => tab.kind === "chat" && matches(tab),
    );
  }

  public removeFiles(directoryId: string | null, path: string): void {
    this.remove(
      this.requireGroup(),
      (tab) =>
        tab.kind === "file" &&
        tab.directoryId === directoryId &&
        (tab.path === path || tab.path.startsWith(`${path}/`)),
    );
  }

  private newFloatingConversation(): void {
    this.commit(this.replaceFloatingDraft(this.requireGroup()));
  }

  private acceptFloatingConversation(conversationId: string): void {
    const group = this.requireGroup();
    const { floatingChat } = group;
    if (floatingChat.draftId !== conversationId) {
      throw new Error("The accepted conversation id must match the floating draft id.");
    }
    if (floatingChat.conversationId === conversationId) return;
    if (group.tabs.some((tab) => tab.kind === "chat" && tab.conversationId === conversationId)) {
      throw new Error(`Conversation ${conversationId} already has a tab.`);
    }
    this.commit({
      ...group,
      floatingChat: { ...floatingChat, conversationId, draftSubmitted: false },
    });
  }

  private setFloatingDraftSubmitted(submitted: boolean): void {
    const group = this.requireGroup();
    const { floatingChat } = group;
    if (floatingChat.conversationId !== null || floatingChat.draftSubmitted === submitted) return;
    this.commit({ ...group, floatingChat: { ...floatingChat, draftSubmitted: submitted } });
  }

  private open(tab: WorkspaceTab, group = this.requireGroup(), replaceDiffView = false): void {
    const key = tabKey(tab);
    const existing = group.tabs.find((item) => tabKey(item) === key);
    if (existing) {
      const next = this.reopenedTab(existing, tab, replaceDiffView);
      this.activateGroup(
        next === existing
          ? group
          : { ...group, tabs: group.tabs.map((item) => (item === existing ? next : item)) },
        existing.id,
      );
      return;
    }

    const replaced = tab.preview ? group.tabs.find((item) => item.preview) : undefined;
    const tabs = [...group.tabs];
    let recentTabIds = group.recentTabIds;
    if (replaced) {
      tabs.splice(tabs.indexOf(replaced), 1, tab);
      recentTabIds = recentTabIds.filter((id) => id !== replaced.id);
      this.release([replaced]);
    } else {
      const active = activeTabOf(group);
      tabs.splice(active ? tabs.indexOf(active) + 1 : tabs.length, 0, tab);
    }
    this.activateGroup({ ...group, tabs, recentTabIds }, tab.id);
  }

  private reopenedTab(
    existing: WorkspaceTab,
    opened: WorkspaceTab,
    replaceDiffView: boolean,
  ): WorkspaceTab {
    const preview = existing.preview && opened.preview;
    if (existing.kind === "extension" && opened.kind === "extension") {
      return {
        ...existing,
        preview,
        params: opened.params,
        title: opened.title ?? existing.title,
        sequence: existing.sequence + 1,
      };
    }
    const diffViewChanged =
      replaceDiffView &&
      existing.kind === "file" &&
      opened.kind === "file" &&
      (existing.diffScope !== opened.diffScope || existing.diffFileView);
    if (diffViewChanged) {
      return { ...existing, preview, diffScope: opened.diffScope, diffFileView: false };
    }
    return preview === existing.preview ? existing : { ...existing, preview: false };
  }

  private activateGroup(group: TabGroup, tabId: string): void {
    const next =
      group.activeTabId === tabId && group.recentTabIds[0] === tabId
        ? group
        : {
            ...group,
            activeTabId: tabId,
            recentTabIds: [tabId, ...group.recentTabIds.filter((id) => id !== tabId)],
          };
    if (next !== this.snapshot.group) this.commit(next);
    this.options.onActivate?.();
  }

  private updateTab(tabId: string, update: (tab: WorkspaceTab) => WorkspaceTab): void {
    const group = this.requireGroup();
    const previous = group.tabs.find((tab) => tab.id === tabId);
    if (!previous) throw new Error(`Unknown tab: ${tabId}.`);
    const next = update(previous);
    if (next !== previous) {
      this.commit({ ...group, tabs: group.tabs.map((tab) => (tab === previous ? next : tab)) });
    }
  }

  private remove(group: TabGroup, matches: (tab: WorkspaceTab) => boolean): void {
    const closed = group.tabs.filter(matches);
    if (closed.length === 0) {
      if (group !== this.snapshot.group) this.commit(group);
      return;
    }
    const ids = new Set(closed.map((tab) => tab.id));
    const tabs = group.tabs.filter((tab) => !ids.has(tab.id));
    const recentTabIds = group.recentTabIds.filter((id) => !ids.has(id));
    this.release(closed);
    this.commit({
      ...group,
      tabs,
      recentTabIds,
      activeTabId:
        group.activeTabId && ids.has(group.activeTabId)
          ? (recentTabIds[0] ?? null)
          : group.activeTabId,
    });
  }

  private release(tabs: readonly WorkspaceTab[]): void {
    this.options.resources.release(
      tabs.map((tab) => tab.id),
      false,
    );
    const drafts = tabs.flatMap((tab) =>
      tab.kind === "chat" && tab.conversationId === null ? [tab.draftId] : [],
    );
    if (drafts.length > 0) this.options.resources.discardDrafts(this.snapshot.scope!, drafts);
  }

  private replaceFloatingDraft(group: TabGroup): TabGroup {
    if (group.floatingChat.conversationId === null) {
      this.options.resources.discardDrafts(this.snapshot.scope!, [group.floatingChat.draftId]);
    }
    return { ...group, floatingChat: newFloatingDraft() };
  }

  private requireGroup(): TabGroup {
    if (!this.snapshot.group) throw new Error("The tab group is not loaded.");
    return this.snapshot.group;
  }

  private commit(group: TabGroup): void {
    const { scope } = this.snapshot;
    if (!scope) throw new Error("The tab group is not loaded.");
    this.snapshot = { scope, group, error: null };
    void this.options.bridge
      .saveWorkspaceTabs(scope.serverId, scope.projectId, toPersistedTabGroup(group))
      .catch((error: unknown) => this.options.reportError("Unable to save open tabs", error));
    this.publish();
  }

  private publish(): void {
    for (const listener of this.listeners) listener();
  }
}
