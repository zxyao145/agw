"use client";

import * as React from "react";
import { apiGet } from "@agw/api";
import {
  ChatWorkspaceProvider,
  ColResizeSplit,
  useChatWorkspace,
  type ChatMessageActionArgs,
  type ExtensionMenuItem,
} from "@agw/chat";
import { useQuery } from "@agw/components/query";
import type { FileViewerFile } from "@agw/extension-sdk";
import {
  fileViewerFileType,
  type ConversationItemActionArgs,
  type ExplorerContextMenuArgs,
} from "@agw/projects";

import { formatCloudUri } from "../../extensions/cloud-file-system";
import { fileBaseName } from "../../extensions/file-viewer-patterns";
import { toContextProject, toContextTarget } from "../../extensions/extension-bridge-broker";
import type { ExtensionHost } from "../../extensions/extension-host";
import { useExtensionRuntime } from "../../extensions/extension-host-provider";
import type { ExtensionLayoutSampler } from "../../extensions/extension-layout-sampler";
import type { HostOverlayTracker } from "../../extensions/host-overlay-tracker";
import { BUILT_IN_VIEWS } from "../../extensions/workspace-ids";
import {
  fileViewerDefault,
  type WorkspaceEntry,
  type WorkspaceView,
} from "../../extensions/workspace-registry";
import { LeftSidebar } from "./left-sidebar";
import { MainWorkspace } from "./main-workspace";
import { Ribbon } from "./ribbon";
import { WorkspaceIconView } from "./workspace-icon";

type AgentflowDto = { id: string; name: string; description?: string | null };

function menuTitle(entry: WorkspaceEntry): string {
  return entry.extensionName ? `${entry.title} · ${entry.extensionName}` : entry.title;
}

function useStore<T>(
  subscribe: (listener: () => void) => () => void,
  getSnapshot: () => T,
  getServerSnapshot = getSnapshot,
) {
  return React.useSyncExternalStore(subscribe, getSnapshot, getServerSnapshot);
}

function DesktopWorkspaceLayout({
  host,
  tracker,
  viewLayout,
}: {
  host: ExtensionHost;
  tracker: HostOverlayTracker;
  viewLayout: ExtensionLayoutSampler;
}) {
  const workspace = useChatWorkspace();
  const layout = useStore(
    host.layout.subscribe,
    host.layout.getSnapshot,
    host.layout.getServerSnapshot,
  );
  const registry = useStore(host.registry.subscribe, host.registry.getSnapshot);
  const snapshot = useStore(host.subscribe, host.getSnapshot);
  const agentflows = useQuery({
    queryKey: ["agentflows"],
    queryFn: async () => (await apiGet("/api/agentflows")) as AgentflowDto[],
  });

  React.useEffect(() => host.attachWorkspace(), [host]);

  const {
    selectedProject,
    selectedProjectId,
    conversationId,
    selectedTarget,
    composerRef,
    refreshConversations,
  } = workspace;
  const serverId = host.context.snapshot().server.id;

  // 当前 Project、对话与 Agent 连同连接标识写入 context。Writes the current selection with the connection key.
  React.useEffect(() => {
    const key = host.context.connectionKey;
    if (!key) return;
    const agent = workspace.agents?.find((item) => item.id === selectedTarget?.id);
    const agentflow = agentflows.data?.find((item) => item.id === selectedTarget?.id);
    host.context.setSelection(key, {
      project: selectedProject ? toContextProject(serverId, selectedProject) : null,
      conversation: conversationId ? { id: conversationId } : null,
      target:
        selectedTarget?.type === "agent" && agent
          ? toContextTarget("agent", agent)
          : selectedTarget?.type === "agentflow" && agentflow
            ? toContextTarget("agentflow", agentflow)
            : null,
    });
  }, [
    host,
    serverId,
    selectedProject,
    conversationId,
    selectedTarget,
    workspace.agents,
    agentflows.data,
  ]);

  React.useEffect(() => {
    host.setComposer({
      insertText: (text) => {
        const composer = composerRef.current;
        if (!composer) throw new Error("The chat composer is not mounted.");
        composer.insertText(text);
      },
    });
    host.setConversationRefresher((projectId) => {
      if (projectId === selectedProjectId) refreshConversations();
    });
    return () => {
      host.setComposer(null);
      host.setConversationRefresher(null);
    };
  }, [host, composerRef, refreshConversations, selectedProjectId]);

  // extension 写入当前 Project 的文件后刷新 Source 与文件树。An extension write to the current Project refreshes Source and the tree.
  const { handleFileWritten } = workspace;
  React.useEffect(() => {
    host.setFileWriteListener(({ projectId, directoryId, path }) => {
      if (projectId === selectedProjectId) handleFileWritten(directoryId, path);
    });
    return () => host.setFileWriteListener(null);
  }, [host, handleFileWritten, selectedProjectId]);

  React.useEffect(
    () => tracker.setObscureAll(workspace.isReconnecting),
    [tracker, workspace.isReconnecting],
  );

  // 用户为每类文件选择的默认查看器，经 bridge 保存在 settings.json 中。
  // The user's default viewer per file type, kept in settings.json through the bridge.
  const desktopBridge = window.agwDesktop ?? null;
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

  // 文件查看器：按当前文件名匹配；不再匹配的查看器 frame 关闭。File viewers match the current file; others close.
  const { selectedFile, selectedDirectory, openFile } = workspace;
  const selectedDirectoryId = selectedDirectory?.id ?? null;
  const selectedFileName = selectedFile ? fileBaseName(selectedFile) : null;
  // 每次渲染按 registry 快照匹配。Matched against the registry snapshot on every render.
  const fileViewers = selectedFileName
    ? host.registry.fileViewersFor(selectedFileName, fileViewerDefaults)
    : [];
  const defaultFileViewerId = selectedFileName
    ? fileViewerDefault(fileViewerDefaults, selectedFileName)
    : null;
  const setDefaultFileViewer =
    desktopBridge && selectedFileName
      ? (viewerId: string | null) =>
          void desktopBridge
            .setFileViewerDefault(fileViewerFileType(selectedFileName), viewerId)
            .then(setFileViewerDefaults)
      : undefined;
  const fileViewerFile = React.useMemo<FileViewerFile | null>(
    () =>
      selectedFile && selectedProjectId
        ? {
            uri: formatCloudUri({
              serverId,
              projectId: selectedProjectId,
              directoryId: selectedDirectoryId,
              path: selectedFile,
            }),
            name: fileBaseName(selectedFile),
            projectId: selectedProjectId,
          }
        : null,
    [serverId, selectedProjectId, selectedDirectoryId, selectedFile],
  );
  const fileViewerIds = fileViewers.map((view) => view.id).join("\n");
  React.useEffect(() => {
    host.retainFileViewers(fileViewerIds ? fileViewerIds.split("\n") : []);
  }, [host, fileViewerIds]);
  React.useEffect(() => {
    host.setFileOpener(async ({ directoryId, path }) => {
      const opened = await openFile(directoryId, path);
      if (opened) host.layout.openMainView(BUILT_IN_VIEWS.file);
      return opened;
    });
    return () => host.setFileOpener(null);
  }, [host, openFile]);

  const toMenuItems = <T,>(
    entries: readonly WorkspaceEntry[],
    args: (input: T) => unknown,
    onSelect?: (entry: WorkspaceEntry, input: T) => void,
  ) =>
    entries.map((entry): ExtensionMenuItem<T> => ({
      id: entry.id,
      title: menuTitle(entry),
      icon: <WorkspaceIconView icon={entry.icon} />,
      onSelect: (input) => {
        onSelect?.(entry, input);
        host.runAction(entry.extensionId, entry.action, args(input));
      },
    }));

  const composerActions = toMenuItems<Record<string, never>>(registry.composerActions, () => ({
    projectId: selectedProjectId,
    conversationId,
  }));
  const messageActions = toMenuItems<ChatMessageActionArgs>(registry.messageActions, (message) => ({
    projectId: selectedProjectId,
    conversationId,
    message: {
      id: message.messageId,
      role: message.role,
      text: message.text,
      turnId: message.turnId,
    },
  }));
  const conversationActions = toMenuItems<ConversationItemActionArgs>(
    registry.conversationActions,
    (item) => ({
      projectId: selectedProjectId,
      conversationId: item.conversationId,
      title: item.title,
    }),
  );
  const explorerUri = (item: ExplorerContextMenuArgs) =>
    formatCloudUri({
      serverId,
      projectId: selectedProjectId ?? "",
      directoryId: item.directoryId,
      path: item.path,
    });
  const explorerActions = toMenuItems<ExplorerContextMenuArgs>(
    registry.explorerActions,
    (item) => ({
      projectId: selectedProjectId,
      uri: explorerUri(item),
      name: item.name,
      type: item.type,
    }),
    (entry, item) => {
      if (entry.extensionId)
        host.grantCloudRead(entry.extensionId, explorerUri(item), item.type === "directory");
    },
  );

  const sidebarView =
    registry.views.get(layout.activeSidebarViewId) ??
    registry.views.get(BUILT_IN_VIEWS.conversations)!;
  const mainView =
    registry.views.get(layout.activeMainViewId) ?? registry.views.get(BUILT_IN_VIEWS.chat)!;

  // 打开过的 retainContextWhenHidden 视图切走后保持挂载。Opened retained views stay mounted after switching away.
  const [openedRetained, setOpenedRetained] = React.useState<readonly string[]>([]);
  React.useEffect(() => {
    if (mainView.retainContextWhenHidden && !openedRetained.includes(mainView.id)) {
      setOpenedRetained((current) => [...current, mainView.id]);
    }
  }, [mainView, openedRetained]);
  const retainedViews = openedRetained
    .map((id) => registry.views.get(id))
    .filter((view): view is WorkspaceView => view !== undefined && view.retainContextWhenHidden);

  return (
    <div
      className="flex h-full min-h-0 w-full"
      inert={workspace.isReconnecting}
      aria-hidden={workspace.isReconnecting}
    >
      <Ribbon
        entries={registry.ribbon}
        sidebarVisible={layout.sidebarVisible}
        activeSidebarViewId={layout.activeSidebarViewId}
        onSelect={(entry) => host.runAction(entry.extensionId, entry.action, {})}
      />
      <ColResizeSplit>
        {layout.sidebarVisible ? (
          <ColResizeSplit.Left defaultPanelWidth={320} minWidth={268} maxWidth={420}>
            <LeftSidebar
              host={host}
              layout={viewLayout}
              snapshot={snapshot}
              view={sidebarView}
              openRequest={layout.openRequests[sidebarView.id]}
              conversationActions={conversationActions}
              explorerActions={explorerActions}
              onConversationOpened={() => host.layout.openMainView(BUILT_IN_VIEWS.chat)}
              onFileOpened={() => host.layout.openMainView(BUILT_IN_VIEWS.file)}
            />
          </ColResizeSplit.Left>
        ) : null}
        <ColResizeSplit.Right>
          <MainWorkspace
            host={host}
            layout={viewLayout}
            snapshot={snapshot}
            activeView={mainView}
            retainedViews={retainedViews}
            openRequests={layout.openRequests}
            composerActions={composerActions}
            messageActions={messageActions}
            fileViewers={fileViewers}
            fileViewerFile={fileViewerFile}
            defaultFileViewerId={defaultFileViewerId}
            onSetDefaultFileViewer={setDefaultFileViewer}
          />
        </ColResizeSplit.Right>
      </ColResizeSplit>
    </div>
  );
}

/** Desktop 的三区布局：Ribbon、Left Sidebar、Main Workspace。The Desktop three-region layout. */
export function DesktopWorkspace() {
  const runtime = useExtensionRuntime();
  if (!runtime) throw new Error("DesktopWorkspace requires the extension host.");
  return (
    <div className="relative flex h-full min-h-0 w-full">
      <ChatWorkspaceProvider
        routeBasePath="/desktop/chat"
        confirmFileChange={() => runtime.host.confirmDiscardUnsaved()}
      >
        <DesktopWorkspaceLayout
          host={runtime.host}
          tracker={runtime.tracker}
          viewLayout={runtime.layout}
        />
      </ChatWorkspaceProvider>
    </div>
  );
}
