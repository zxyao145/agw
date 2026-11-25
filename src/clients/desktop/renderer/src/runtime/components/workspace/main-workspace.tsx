"use client";

import * as React from "react";
import { useChatProject } from "@agw/chat";
import {
  executionSessionManager,
  type ConversationStatus,
  type ExecutionStatus,
} from "@agw/chat-runtime";
import { EventsPage } from "@agw/events";
import { NotificationsPage } from "@agw/notifications";

import type { ExtensionHost, ExtensionHostSnapshot } from "../../extensions/extension-host";
import type { ExtensionLayoutSampler } from "../../extensions/extension-layout-sampler";
import { BUILT_IN_VIEWS, tabIdOfInstance } from "../../extensions/workspace-ids";
import type { WorkspaceLayoutState } from "../../extensions/workspace-layout-store";
import { activeViewIdOf, type TabGroup, type WorkspaceTab } from "../../extensions/workspace-tab";
import {
  activeTabOf,
  workspaceChatState,
  type WorkspaceTabsSnapshot,
} from "../../extensions/workspace-tabs-store";
import { ChatTabPanel, type ChatSelection, type DesktopChatActions } from "./chat-tab-panel";
import { DesktopFloatingChat } from "./desktop-floating-chat";
import { EmptyWorkspace } from "./empty-workspace";
import { ExtensionTabView } from "./extension-view";
import { FileTabPanel } from "./file-tab-panel";
import { JobsWorkspace } from "./jobs-workspace";
import { WindowFloatingChatButton } from "./window-floating-chat-button";
import { describeTabs, WorkspaceTabBar } from "./workspace-tab-bar";

export type MainWorkspaceProps = {
  host: ExtensionHost;
  layout: ExtensionLayoutSampler;
  snapshot: ExtensionHostSnapshot;
  workspaceLayout: WorkspaceLayoutState;
  tabs: WorkspaceTabsSnapshot;
  fileViewerDefaults: Readonly<Record<string, string>>;
  onSetDefaultFileViewer?: (fileName: string, viewerId: string | null) => void;
  getChatActions(conversationId: string | null): DesktopChatActions;
  onChatSelection(id: string, selection: ChatSelection): void;
};

function notificationIdFromRequest(value: unknown): string | null {
  if (typeof value !== "object" || value === null || !("notificationId" in value)) return null;
  return typeof value.notificationId === "string" ? value.notificationId : null;
}

function useMountedTabs(tabs: readonly WorkspaceTab[], activeTabId: string | null) {
  const [mounted, setMounted] = React.useState<ReadonlySet<string>>(() => new Set());
  React.useEffect(() => {
    setMounted((current) => {
      const next = new Set(
        tabs.filter((tab) => current.has(tab.id) || tab.id === activeTabId).map((tab) => tab.id),
      );
      return next.size === current.size && [...next].every((id) => current.has(id))
        ? current
        : next;
    });
  }, [tabs, activeTabId]);
  return mounted;
}

function TabPanel({
  workspace,
  tab,
  active,
}: {
  workspace: MainWorkspaceProps;
  tab: WorkspaceTab;
  active: boolean;
}) {
  const { host, layout, snapshot } = workspace;
  switch (tab.kind) {
    case "chat":
      return (
        <ChatTabPanel
          host={host}
          tab={tab}
          active={active}
          {...workspace.getChatActions(tab.conversationId)}
          onSelectionChange={(selection) => workspace.onChatSelection(tab.id, selection)}
        />
      );
    case "file":
      return (
        <FileTabPanel
          host={host}
          layout={layout}
          snapshot={snapshot}
          tab={tab}
          active={active}
          fileViewerDefaults={workspace.fileViewerDefaults}
          onSetDefaultFileViewer={workspace.onSetDefaultFileViewer}
        />
      );
    case "jobs":
      return <JobsWorkspace />;
    case "extension":
      return (
        <ExtensionTabView
          host={host}
          layout={layout}
          snapshot={snapshot}
          tab={tab}
          active={active}
          onClose={() => void host.tabs.close([tab.id])}
        />
      );
  }
}

function TabPanels({
  workspace,
  group,
  activeTabId,
  tabStripVisible,
}: {
  workspace: MainWorkspaceProps;
  group: TabGroup;
  activeTabId: string | null;
  tabStripVisible: boolean;
}) {
  const mounted = useMountedTabs(group.tabs, activeTabId);
  return group.tabs.map((tab) => {
    const active = tab.id === activeTabId;
    return (
      <div
        key={tab.id}
        id={`workspace-tab-panel-${tab.id}`}
        role={tabStripVisible ? "tabpanel" : undefined}
        aria-labelledby={tabStripVisible ? `workspace-tab-${tab.id}` : undefined}
        data-workspace-tab-panel={tab.id}
        data-conversation-id={tab.kind === "chat" ? (tab.conversationId ?? undefined) : undefined}
        hidden={!active}
        className={active ? "flex min-h-0 flex-1 flex-col" : "hidden"}
      >
        {active || mounted.has(tab.id) ? (
          <TabPanel workspace={workspace} tab={tab} active={active} />
        ) : null}
      </div>
    );
  });
}

function useTabPresentation({ host, snapshot, tabs: { scope, group } }: MainWorkspaceProps) {
  const { projectDirectories, conversationStatuses, onlyDiff } = useChatProject();
  React.useSyncExternalStore(
    executionSessionManager.subscribe,
    executionSessionManager.getSnapshot,
    executionSessionManager.getSnapshot,
  );
  const registry = React.useSyncExternalStore(
    host.registry.subscribe,
    host.registry.getSnapshot,
    host.registry.getSnapshot,
  );
  if (!group) return { registry, tabViews: [] };
  const statuses = new Map<string, ConversationStatus | ExecutionStatus>(conversationStatuses);
  const unsaved = new Set(snapshot.unsavedSourceTabIds);
  for (const key of snapshot.unsavedInstanceKeys) {
    const tabId = tabIdOfInstance(key);
    if (tabId !== null) unsaved.add(tabId);
  }
  for (const tab of group.tabs) {
    if (tab.kind === "chat" && scope) {
      const conversationId = tab.conversationId ?? tab.draftId;
      const status = executionSessionManager.getConversationActivity({ ...scope, conversationId });
      if (status !== "idle") statuses.set(conversationId, status);
    }
  }
  return {
    registry,
    tabViews: describeTabs(group, {
      directories: projectDirectories,
      onlyDiff,
      conversationStatuses: statuses,
      unsavedTabIds: unsaved,
      view: (id) => registry.views.get(id),
    }),
  };
}

function FullWorkspaceView({ workspace }: { workspace: MainWorkspaceProps }) {
  const {
    host,
    tabs: { scope },
    workspaceLayout,
  } = workspace;
  switch (workspaceLayout.fullWorkspaceViewId) {
    case BUILT_IN_VIEWS.events:
      return (
        <div className="min-h-0 flex-1 overflow-y-auto agw-scrollbar">
          <EventsPage />
        </div>
      );
    case BUILT_IN_VIEWS.notifications:
      return (
        <NotificationsPage
          desktopBridge={typeof window === "undefined" ? null : window.agwDesktop}
          serverId={scope?.serverId ?? host.context.snapshot().server.id}
          initialNotificationId={notificationIdFromRequest(
            workspaceLayout.openRequests[BUILT_IN_VIEWS.notifications]?.params,
          )}
          liveUpdatesEnabled={false}
        />
      );
    default:
      return null;
  }
}

function WorkspacePanels({
  workspace,
  group,
  floatingVisible,
  tabStripVisible,
}: {
  workspace: MainWorkspaceProps;
  group: TabGroup;
  floatingVisible: boolean;
  tabStripVisible: boolean;
}) {
  const { host, snapshot, workspaceLayout, onChatSelection, getChatActions } = workspace;
  const activeTabId = workspaceLayout.fullWorkspaceViewId === null ? group.activeTabId : null;
  const { floatingChat } = group;
  return (
    <>
      <TabPanels
        workspace={workspace}
        group={group}
        activeTabId={activeTabId}
        tabStripVisible={tabStripVisible}
      />
      <DesktopFloatingChat
        host={host}
        snapshot={snapshot}
        floatingChat={floatingChat}
        visible={floatingVisible}
        {...getChatActions(floatingChat.conversationId)}
        onSelectionChange={(selection) => onChatSelection(floatingChat.draftId, selection)}
      />
      {activeTabId === null && workspaceLayout.fullWorkspaceViewId === null ? (
        <EmptyWorkspace onNewConversation={() => host.tabs.openNewConversation()} />
      ) : null}
    </>
  );
}

function WorkspaceTabStrip({
  workspace,
  tabViews,
  group,
}: {
  workspace: MainWorkspaceProps;
  tabViews: ReturnType<typeof describeTabs>;
  group: TabGroup;
}) {
  const { tabs } = workspace.host;
  return (
    <WorkspaceTabBar
      tabs={tabViews}
      activeTabId={group.activeTabId}
      onActivate={(id) => tabs.activate(id)}
      onPin={(id) => tabs.pin(id)}
      onClose={(ids) => void tabs.close(ids)}
      onMove={(id, index) => tabs.move(id, index)}
    />
  );
}

export function MainWorkspace(props: MainWorkspaceProps) {
  const {
    host,
    snapshot,
    tabs: { scope, group, error },
    workspaceLayout,
  } = props;
  const { registry, tabViews } = useTabPresentation(props);
  const fullView = workspaceLayout.fullWorkspaceViewId;
  const activeTab = activeTabOf(group);
  const floatingOpen = snapshot.floatingChat?.open === true;
  const { floatingAllowed } = workspaceChatState(group, fullView, floatingOpen);
  React.useEffect(() => {
    if (!floatingAllowed && floatingOpen) host.closeFloatingChat();
  }, [host, floatingAllowed, floatingOpen]);

  if (error) throw error;
  const tabStripVisible = fullView === null && tabViews.length > 0;
  const activeViewId = fullView ?? (activeTab ? activeViewIdOf(activeTab) : "agw.empty");
  const activeTitle = fullView
    ? registry.views.get(fullView)?.title
    : (tabViews.find((view) => view.tab.id === activeTab?.id)?.title ??
      (activeTab?.kind === "chat" ? "New conversation" : undefined));
  const groupKey = scope ? JSON.stringify([scope.serverId, scope.projectId]) : null;

  return (
    <div className="flex h-full min-h-0 min-w-0 flex-1 flex-col">
      {group && tabStripVisible ? (
        <WorkspaceTabStrip key={groupKey} workspace={props} group={group} tabViews={tabViews} />
      ) : null}
      <main
        className="agw-main-workspace relative"
        aria-label={activeTitle ?? "Workspace"}
        data-view-id={activeViewId}
        data-tab-id={fullView === null ? activeTab?.id : undefined}
      >
        <WindowFloatingChatButton host={host} snapshot={snapshot} hidden={!floatingAllowed} />
        <FullWorkspaceView workspace={props} />
        {group && scope ? (
          <WorkspacePanels
            key={groupKey}
            workspace={props}
            group={group}
            floatingVisible={floatingAllowed && floatingOpen}
            tabStripVisible={tabStripVisible}
          />
        ) : null}
      </main>
    </div>
  );
}
