"use client";

import * as React from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { toast } from "sonner";
import { apiGet, getApiErrorMessage } from "@agw/api";
import {
  buildChatHref,
  ChatProjectProvider,
  ColResizeSplit,
  useChatProject,
  useExecutionPlatform,
  type ChatConversationSidebarProps,
  type ChatFilesSidebarProps,
  type ChatMessageActionArgs,
  type ExtensionMenuItem,
} from "@agw/chat";
import { useQuery } from "@agw/components/query";
import { NotificationsLiveSync, useNotificationsUnreadCount } from "@agw/notifications";
import {
  fileViewerFileType,
  type ConversationItemActionArgs,
  type ConversationSummary,
  type ExplorerContextMenuArgs,
} from "@agw/projects";
import type { AgwDesktopBridge } from "@desktop/shared/contracts";
import {
  sameWorkspaceTabScope,
  type WorkspaceTabScope,
} from "@desktop/shared/contracts/workspace-tabs";

import { formatCloudUri } from "../../extensions/cloud-file-system";
import { toContextProject, toContextTarget } from "../../extensions/extension-bridge-broker";
import type { ExtensionHost } from "../../extensions/extension-host";
import { useExtensionRuntime } from "../../extensions/extension-host-provider";
import type { ExtensionLayoutSampler } from "../../extensions/extension-layout-sampler";
import type { HostOverlayTracker } from "../../extensions/host-overlay-tracker";
import { BUILT_IN_VIEWS } from "../../extensions/workspace-ids";
import type {
  WorkspaceEntry,
  WorkspaceRegistrySnapshot,
} from "../../extensions/workspace-registry";
import {
  activeViewIdOf,
  hasVisibleWorkspaceTabs,
  type FileTab,
  type TabGroup,
} from "../../extensions/workspace-tab";
import {
  activeTabOf,
  workspaceChatState,
  type WorkspaceTabsSnapshot,
} from "../../extensions/workspace-tabs-store";
import type { ChatSelection, DesktopChatActions } from "./chat-tab-panel";
import { LeftSidebar } from "./left-sidebar";
import { MainWorkspace, type MainWorkspaceProps } from "./main-workspace";
import { Ribbon } from "./ribbon";
import { WorkspaceIconView } from "./workspace-icon";

type AgentflowDto = { id: string; name: string; description?: string | null };
type ChatProjectModel = ReturnType<typeof useChatProject>;
type WorkspaceRuntime = {
  host: ExtensionHost;
  tracker: HostOverlayTracker;
  viewLayout: ExtensionLayoutSampler;
};

const EMPTY_TABS: WorkspaceTabsSnapshot = { scope: null, group: null, error: null };

function useStore<T>(
  subscribe: (listener: () => void) => () => void,
  getSnapshot: () => T,
  getServerSnapshot = getSnapshot,
) {
  return React.useSyncExternalStore(subscribe, getSnapshot, getServerSnapshot);
}

function useWorkspaceHostState(host: ExtensionHost) {
  const layout = useStore(
    host.layout.subscribe,
    host.layout.getSnapshot,
    host.layout.getServerSnapshot,
  );
  const snapshot = useStore(host.subscribe, host.getSnapshot);
  const tabs = useStore(host.tabs.subscribe, host.tabs.getSnapshot);
  return { layout, snapshot, tabs };
}

function useWorkspaceTabGroup(
  host: ExtensionHost,
  tabs: WorkspaceTabsSnapshot,
  serverId: string,
  projectId: string | null,
) {
  const scope = React.useMemo<WorkspaceTabScope | null>(
    () => (projectId ? { serverId, projectId } : null),
    [serverId, projectId],
  );
  React.useEffect(() => host.attachWorkspace(), [host]);
  React.useEffect(
    () => () => {
      void host.tabs.setScope(null);
    },
    [host],
  );
  React.useEffect(() => {
    void host.tabs.setScope(scope);
  }, [host, scope]);
  const getGroup = React.useCallback(() => {
    const current = host.tabs.getSnapshot();
    return sameWorkspaceTabScope(current.scope, scope) ? current.group : null;
  }, [host, scope]);
  return { currentTabs: sameWorkspaceTabScope(tabs.scope, scope) ? tabs : EMPTY_TABS, getGroup };
}

function usePruneWorkspaceTabs(
  desktopBridge: AgwDesktopBridge | null,
  serverId: string,
  project: ChatProjectModel,
) {
  const { projectsLoaded, projects } = project;
  React.useEffect(() => {
    if (!desktopBridge || !projectsLoaded) return;
    void desktopBridge
      .pruneWorkspaceTabs(
        serverId,
        projects.map(({ id }) => id),
      )
      .catch((error: unknown) => {
        console.error("Unable to prune open tabs.", error);
      });
  }, [desktopBridge, serverId, projectsLoaded, projects]);
}

function useWorkspaceLinks(
  host: ExtensionHost,
  serverId: string,
  selectedProjectId: string | null,
  groupReady: boolean,
) {
  const router = useRouter();
  const searchParams = useSearchParams();
  const requestedProjectId = searchParams.get("projectId");
  const requestedConversationId = searchParams.get("conversationId");
  const notificationId = searchParams.get("notificationId");
  const notificationServerId = searchParams.get("serverId");

  React.useEffect(() => {
    if (!groupReady || !selectedProjectId || !requestedConversationId) return;
    if (requestedProjectId && requestedProjectId !== selectedProjectId) return;
    host.tabs.openConversation(requestedConversationId, { preview: false });
    router.replace(
      buildChatHref("/desktop/chat", { projectId: selectedProjectId, conversationId: null }),
      { scroll: false },
    );
  }, [host, router, groupReady, requestedProjectId, requestedConversationId, selectedProjectId]);

  React.useEffect(() => {
    if (notificationId && notificationServerId === serverId) {
      host.layout.openMainView(BUILT_IN_VIEWS.notifications, { notificationId });
    }
  }, [host, notificationId, notificationServerId, serverId]);
}

function useWorkspaceCommands(host: ExtensionHost, desktopBridge: AgwDesktopBridge | null) {
  React.useEffect(
    () =>
      desktopBridge?.onWorkspaceCommand((command) => {
        if (host.layout.getSnapshot().fullWorkspaceViewId !== null) return;
        const current = host.tabs.getSnapshot().group;
        if (!current || !hasVisibleWorkspaceTabs(current.tabs)) return;
        if (command === "closeTab") {
          if (current.activeTabId) void host.tabs.close([current.activeTabId]);
        } else {
          host.tabs.activateAdjacent(command === "nextTab" ? 1 : -1);
        }
      }),
    [host, desktopBridge],
  );
}

function useFollowActiveFile(project: ChatProjectModel, activeTab: ReturnType<typeof activeTabOf>) {
  const activeFile = activeTab?.kind === "file" ? activeTab : null;
  const followActiveFile = React.useEffectEvent(() => {
    if (activeFile && activeFile.directoryId !== (project.selectedDirectory?.id ?? null)) {
      project.selectDirectory(activeFile.directoryId);
    }
  });
  React.useEffect(() => {
    followActiveFile();
  }, [activeFile?.id, project.selectedProjectId]);
}

function useWorkspaceSelection(
  host: ExtensionHost,
  project: ChatProjectModel,
  serverId: string,
  visibleSessionId: string | null,
) {
  const [chatSelection, setChatSelection] = React.useState<{
    id: string;
    selection: ChatSelection;
  } | null>(null);
  const visibleSelection =
    chatSelection && chatSelection.id === visibleSessionId ? chatSelection.selection : null;
  const connectionKey = useStore(
    React.useCallback((listener) => host.context.onConnectionChanged(listener), [host]),
    () => host.context.connectionKey,
    () => null,
  );
  const agentflows = useQuery({
    queryKey: ["agentflows"],
    queryFn: async () => (await apiGet("/api/agentflows")) as AgentflowDto[],
  });
  const target = visibleSelection?.target;
  const conversationId = visibleSelection?.conversationId ?? null;
  const { selectedProject, agents } = project;
  // 当前 Project 与可见会话共同提供 extension context。The current Project and visible session supply the extension context.
  React.useEffect(() => {
    if (!connectionKey) return;
    const agent = agents?.find((item) => item.id === target?.id);
    const agentflow = agentflows.data?.find((item) => item.id === target?.id);
    host.context.setSelection(connectionKey, {
      project: selectedProject ? toContextProject(serverId, selectedProject) : null,
      conversation: conversationId ? { id: conversationId } : null,
      target:
        target?.type === "agent" && agent
          ? toContextTarget("agent", agent)
          : target?.type === "agentflow" && agentflow
            ? toContextTarget("agentflow", agentflow)
            : null,
    });
  }, [
    host,
    connectionKey,
    serverId,
    selectedProject,
    conversationId,
    target,
    agents,
    agentflows.data,
  ]);
  return (id: string, selection: ChatSelection) => setChatSelection({ id, selection });
}

function useWorkspaceProjectBindings(
  host: ExtensionHost,
  tracker: HostOverlayTracker,
  project: ChatProjectModel,
) {
  const { selectedProjectId, refreshConversations, notifyFileWritten, isReconnecting } = project;
  React.useEffect(() => {
    host.setConversationRefresher((projectId) => {
      if (projectId === selectedProjectId) refreshConversations();
    });
    host.setFileWriteListener(({ projectId, directoryId, path }) => {
      if (projectId === selectedProjectId) notifyFileWritten(directoryId, path);
    });
    return () => {
      host.setConversationRefresher(null);
      host.setFileWriteListener(null);
    };
  }, [host, selectedProjectId, refreshConversations, notifyFileWritten]);
  React.useEffect(() => tracker.setObscureAll(isReconnecting), [tracker, isReconnecting]);
}

function useFileViewerDefaults(desktopBridge: AgwDesktopBridge | null) {
  const [fileViewerDefaults, setFileViewerDefaults] = React.useState<
    Readonly<Record<string, string>>
  >({});
  React.useEffect(() => {
    if (!desktopBridge) return;
    let active = true;
    void desktopBridge.getFileViewerDefaults().then((defaults) => {
      if (active) setFileViewerDefaults(defaults);
    });
    return () => {
      active = false;
    };
  }, [desktopBridge]);
  return {
    fileViewerDefaults,
    onSetDefaultFileViewer: desktopBridge
      ? (name: string, id: string | null) => {
          void desktopBridge
            .setFileViewerDefault(fileViewerFileType(name), id)
            .then(setFileViewerDefaults);
        }
      : undefined,
  };
}

function runWorkspaceAction(host: ExtensionHost, entry: WorkspaceEntry, args: unknown): void {
  void host.runAction(entry.extensionId, entry.action, args).catch((error: unknown) => {
    console.error(`Unable to run workspace action ${entry.id}.`, error);
    toast.error(`Unable to run action: ${getApiErrorMessage(error)}`);
  });
}

function toMenuItems<T>(
  host: ExtensionHost,
  entries: readonly WorkspaceEntry[],
  args: (input: T) => unknown,
  beforeAction?: (entry: WorkspaceEntry, input: T) => void,
): ExtensionMenuItem<T>[] {
  return entries.map((entry) => ({
    id: entry.id,
    title: entry.extensionName ? `${entry.title} · ${entry.extensionName}` : entry.title,
    icon: <WorkspaceIconView icon={entry.icon} />,
    onSelect: (input) => {
      beforeAction?.(entry, input);
      runWorkspaceAction(host, entry, args(input));
    },
  }));
}

function createChatActions(
  host: ExtensionHost,
  registry: WorkspaceRegistrySnapshot,
  selectedProjectId: string | null,
) {
  return (id: string | null): DesktopChatActions => ({
    composerActions: toMenuItems<Record<string, never>>(host, registry.composerActions, () => ({
      projectId: selectedProjectId,
      conversationId: id,
    })),
    messageActions: toMenuItems<ChatMessageActionArgs>(
      host,
      registry.messageActions,
      (message) => ({
        projectId: selectedProjectId,
        conversationId: id,
        message: {
          id: message.messageId,
          role: message.role,
          text: message.text,
          turnId: message.turnId,
        },
      }),
    ),
  });
}

type SidebarTabs = {
  host: ExtensionHost;
  entries: readonly WorkspaceEntry[];
  getGroup(): TabGroup | null;
};

function useConversationSidebar({
  host,
  entries,
  getGroup,
  projectId,
  currentConversationId,
}: SidebarTabs & {
  projectId: string | null;
  currentConversationId: string | null;
}): ChatConversationSidebarProps {
  const onConversationsLoaded = React.useCallback(
    (items: readonly ConversationSummary[]) => {
      const current = getGroup();
      if (!current) return;
      const titles = new Map(items.map(({ conversationId: id, title }) => [id, title]));
      for (const tab of current.tabs) {
        if (tab.kind !== "chat" || !tab.conversationId) continue;
        const title = titles.get(tab.conversationId);
        if (title != null && tab.title !== title) host.tabs.setChatTitle(tab.id, title);
      }
    },
    [getGroup, host],
  );
  return {
    currentConversationId,
    onConversationSelect: (item, { pinned }) => {
      if (getGroup()) host.tabs.openConversation(item.conversationId, { preview: !pinned });
    },
    onNewConversation: () => {
      if (getGroup()) host.tabs.openNewConversation();
    },
    onAllConversationsDeleted: () => {
      if (getGroup()) host.tabs.removeConversations("all");
    },
    onConversationDeleted: (id) => {
      if (getGroup()) host.tabs.removeConversations([id]);
    },
    onConversationsLoaded,
    itemActions: toMenuItems<ConversationItemActionArgs>(host, entries, (item) => ({
      projectId,
      conversationId: item.conversationId,
      title: item.title,
    })),
  };
}

function explorerMenuItems(
  host: ExtensionHost,
  entries: readonly WorkspaceEntry[],
  serverId: string,
  projectId: string | null,
) {
  const uri = (item: ExplorerContextMenuArgs) =>
    formatCloudUri({
      serverId,
      projectId: projectId ?? "",
      directoryId: item.directoryId,
      path: item.path,
    });
  return toMenuItems<ExplorerContextMenuArgs>(
    host,
    entries,
    (item) => ({ projectId, uri: uri(item), name: item.name, type: item.type }),
    (entry, item) => {
      if (entry.extensionId)
        host.grantCloudRead(entry.extensionId, uri(item), item.type === "directory");
    },
  );
}

function filesSidebar({
  host,
  entries,
  getGroup,
  serverId,
  project,
  activeFile,
}: SidebarTabs & {
  serverId: string;
  project: ChatProjectModel;
  activeFile: FileTab | null;
}): ChatFilesSidebarProps {
  const { selectedDirectory, selectedProjectId, onlyDiff } = project;
  const directoryId = selectedDirectory?.id ?? null;
  return {
    selectedFile: activeFile,
    onFileSelected: (path, scope, { pinned }) => {
      if (!getGroup()) return;
      host.tabs.openFile(
        { directoryId, path, diffScope: scope ?? null },
        { preview: !pinned, replaceDiffView: onlyDiff },
      );
    },
    onFileDeleted: (path) => {
      if (getGroup()) host.tabs.removeFiles(directoryId, path);
    },
    onFileGitScopeChanged: (path, scope) => {
      const group = getGroup();
      if (!group) return;
      const prefix = `${path.replace(/\/+$/u, "")}/`;
      for (const tab of group.tabs) {
        const matches =
          tab.kind === "file" &&
          tab.directoryId === directoryId &&
          (tab.path === path || tab.path.startsWith(prefix));
        if (matches)
          host.tabs.setFileView(tab.id, { diffScope: scope, diffFileView: tab.diffFileView });
      }
    },
    contextMenuItems: explorerMenuItems(host, entries, serverId, selectedProjectId),
  };
}

type WorkspaceHostState = ReturnType<typeof useWorkspaceHostState>;

function DesktopWorkspaceSidebar({
  main,
  getGroup,
  serverId,
  registry,
}: {
  main: MainWorkspaceProps;
  getGroup(): TabGroup | null;
  serverId: string;
  registry: WorkspaceRegistrySnapshot;
}) {
  const { host, layout, snapshot, tabs, workspaceLayout } = main;
  const project = useChatProject();
  const activeTab = activeTabOf(tabs.group);
  const sidebarView =
    registry.views.get(workspaceLayout.activeSidebarViewId) ??
    registry.views.get(BUILT_IN_VIEWS.conversations)!;
  const conversation = useConversationSidebar({
    host,
    getGroup,
    entries: registry.conversationActions,
    projectId: project.selectedProjectId,
    currentConversationId: activeTab?.kind === "chat" ? activeTab.conversationId : null,
  });
  const files = filesSidebar({
    host,
    getGroup,
    entries: registry.explorerActions,
    serverId,
    project,
    activeFile: activeTab?.kind === "file" ? activeTab : null,
  });
  return (
    <LeftSidebar
      host={host}
      layout={layout}
      snapshot={snapshot}
      view={sidebarView}
      openRequest={workspaceLayout.openRequests[sidebarView.id]}
      conversation={conversation}
      files={files}
    />
  );
}

function useWorkspaceLayout({
  host,
  tracker,
  viewLayout,
  state,
}: WorkspaceRuntime & { state: WorkspaceHostState }) {
  const { layout, snapshot, tabs } = state;
  const project = useChatProject();
  const { selectedProjectId, isReconnecting } = project;
  const { serverId } = useExecutionPlatform();
  const desktopBridge = typeof window === "undefined" ? null : (window.agwDesktop ?? null);
  const { currentTabs, getGroup } = useWorkspaceTabGroup(host, tabs, serverId, selectedProjectId);
  const { group } = currentTabs;
  const activeTab = activeTabOf(group);
  const { session } = workspaceChatState(
    group,
    layout.fullWorkspaceViewId,
    snapshot.floatingChat?.open === true,
  );
  const onChatSelection = useWorkspaceSelection(host, project, serverId, session?.id ?? null);
  useWorkspaceLinks(host, serverId, selectedProjectId, group !== null);
  useWorkspaceCommands(host, desktopBridge);
  usePruneWorkspaceTabs(desktopBridge, serverId, project);
  useFollowActiveFile(project, activeTab);
  useWorkspaceProjectBindings(host, tracker, project);
  const fileViewerDefaults = useFileViewerDefaults(desktopBridge);
  const registry = useStore(host.registry.subscribe, host.registry.getSnapshot);
  const main: MainWorkspaceProps = {
    host,
    layout: viewLayout,
    snapshot,
    workspaceLayout: layout,
    tabs: currentTabs,
    ...fileViewerDefaults,
    getChatActions: createChatActions(host, registry, selectedProjectId),
    onChatSelection,
  };
  const activeMainViewId =
    layout.fullWorkspaceViewId ?? (activeTab ? activeViewIdOf(activeTab) : null);
  return { main, getGroup, registry, serverId, desktopBridge, isReconnecting, activeMainViewId };
}

function DesktopWorkspaceLayout(props: WorkspaceRuntime & { state: WorkspaceHostState }) {
  const { main, getGroup, registry, serverId, desktopBridge, isReconnecting, activeMainViewId } =
    useWorkspaceLayout(props);
  const { sidebarVisible, activeSidebarViewId } = props.state.layout;
  const unreadNotificationCount = useNotificationsUnreadCount();
  return (
    <div className="flex h-full min-h-0 w-full" inert={isReconnecting} aria-hidden={isReconnecting}>
      <NotificationsLiveSync desktopBridge={desktopBridge} serverId={serverId} />
      <Ribbon
        entries={registry.ribbon}
        sidebarVisible={sidebarVisible}
        activeSidebarViewId={activeSidebarViewId}
        activeMainViewId={activeMainViewId}
        unreadNotificationCount={unreadNotificationCount}
        onSelect={(entry) => runWorkspaceAction(props.host, entry, {})}
      />
      <ColResizeSplit>
        {sidebarVisible ? (
          <ColResizeSplit.Left defaultPanelWidth={320} minWidth={268} maxWidth={420}>
            <DesktopWorkspaceSidebar
              main={main}
              getGroup={getGroup}
              serverId={serverId}
              registry={registry}
            />
          </ColResizeSplit.Left>
        ) : null}
        <ColResizeSplit.Right>
          <MainWorkspace {...main} />
        </ColResizeSplit.Right>
      </ColResizeSplit>
    </div>
  );
}

function DesktopWorkspaceProvider(props: WorkspaceRuntime) {
  const state = useWorkspaceHostState(props.host);
  const { tabs, layout, snapshot } = state;
  const { serverId } = useExecutionPlatform();
  const projectId = useSearchParams().get("projectId") ?? tabs.scope?.projectId;
  const group =
    projectId && sameWorkspaceTabScope(tabs.scope, { serverId, projectId }) ? tabs.group : null;
  const { session } = workspaceChatState(
    group,
    layout.fullWorkspaceViewId,
    snapshot.floatingChat?.open === true,
  );
  return (
    <ChatProjectProvider
      routeBasePath="/desktop/chat"
      activeConversationId={session?.conversationId ?? null}
    >
      <DesktopWorkspaceLayout {...props} state={state} />
    </ChatProjectProvider>
  );
}

/** Desktop 的三区布局。The Desktop three-region workspace. */
export function DesktopWorkspace() {
  const runtime = useExtensionRuntime();
  if (!runtime) throw new Error("DesktopWorkspace requires the extension host.");
  return (
    <div className="relative flex h-full min-h-0 w-full">
      <DesktopWorkspaceProvider
        host={runtime.host}
        tracker={runtime.tracker}
        viewLayout={runtime.layout}
      />
    </div>
  );
}
