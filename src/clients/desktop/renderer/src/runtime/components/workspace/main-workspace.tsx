"use client";

import * as React from "react";
import {
  ChatConversationPane,
  ChatFilePane,
  FloatingChatPanel,
  useChatWorkspace,
  type ChatMessageActionArgs,
  type ExtensionMenuItem,
} from "@agw/chat";
import type { FileViewerFile } from "@agw/extension-sdk";
import type { EditableSource } from "@agw/projects";

import type { ExtensionHost, ExtensionHostSnapshot } from "../../extensions/extension-host";
import type { ExtensionLayoutSampler } from "../../extensions/extension-layout-sampler";
import { BUILT_IN_VIEWS } from "../../extensions/workspace-ids";
import type { ViewOpenRequest } from "../../extensions/workspace-layout-store";
import type { WorkspaceView } from "../../extensions/workspace-registry";
import { ExtensionView, ExtensionViewInfo, reloadExtensionView } from "./extension-view";
import { WindowFloatingChatButton } from "./window-floating-chat-button";
import { JobsWorkspace } from "./jobs-workspace";
import { EventsPage } from "@agw/events";
import { NotificationsPage } from "@agw/notifications";

export type MainWorkspaceProps = {
  host: ExtensionHost;
  layout: ExtensionLayoutSampler;
  snapshot: ExtensionHostSnapshot;
  activeView: WorkspaceView;
  /** 打开过且需要保留页面状态的 extension 主区视图。Opened extension main views that keep their page state. */
  retainedViews: readonly WorkspaceView[];
  openRequests: Readonly<Record<string, ViewOpenRequest>>;
  composerActions: readonly ExtensionMenuItem<Record<string, never>>[];
  messageActions: readonly ExtensionMenuItem<ChatMessageActionArgs>[];
  /** 匹配当前文件的文件查看器及其文件。File viewers matching the current file, and that file. */
  fileViewers: readonly WorkspaceView[];
  fileViewerFile: FileViewerFile | null;
  /** 可编辑的 Source。The editable Source. */
  editableSource: EditableSource;
  /** 用户为当前文件类型设置的默认查看器 id 或 "source"。The user's default viewer id for the file type, or "source". */
  defaultFileViewerId: string | null;
  /** 把查看器设为当前文件类型的默认，null 删除设置。Sets the file type's default viewer; null removes it. */
  onSetDefaultFileViewer: ((viewerId: string | null) => void) | undefined;
  sourceChatContext: Record<string, string> | null;
};

function SourceFloatingChatContext({
  host,
  context,
}: {
  host: ExtensionHost;
  context: Record<string, string>;
}) {
  React.useEffect(() => {
    host.setSourceFloatingChatContext(context);
  }, [host, context]);

  React.useEffect(() => {
    return () => host.closeSourceFloatingChat();
  }, [host]);

  return null;
}

function notificationIdFromRequest(value: unknown): string | null {
  if (typeof value !== "object" || value === null || !("notificationId" in value)) return null;
  return typeof value.notificationId === "string" ? value.notificationId : null;
}

/**
 * 渲染 `activeMainViewId` 对应的视图。内置对话视图常驻挂载、切走时隐藏；内置文件与 Jobs 视图只在激活时挂载；
 * extension 主区视图按 `retainContextWhenHidden` 决定切走时隐藏或卸载。
 * Renders the `activeMainViewId` view. Chat stays mounted and hidden; File and Jobs mount only when active; extension
 * main views hide or unload according to `retainContextWhenHidden`.
 */
export function MainWorkspace(props: MainWorkspaceProps) {
  const workspace = useChatWorkspace();
  const { activeView, fileViewerFile } = props;
  const floatingChat = props.snapshot.floatingChat;
  const isChat = activeView.id === BUILT_IN_VIEWS.chat;
  const floatingChatMode = isChat ? "page" : floatingChat?.open ? "floating" : "hidden";
  React.useEffect(() => {
    if (isChat && floatingChat?.open) props.host.closeFloatingChat();
  }, [props.host, isChat, floatingChat?.open]);
  const retained = new Set(props.retainedViews.map((view) => view.id));
  const extensionView = (view: WorkspaceView, hidden: boolean) => (
    <div key={view.id} className="flex min-h-0 flex-1 flex-col" hidden={hidden}>
      <ExtensionView
        host={props.host}
        layout={props.layout}
        snapshot={props.snapshot}
        view={view}
        openRequest={props.openRequests[view.id]}
        active={!hidden}
      />
    </div>
  );
  const fileViewers = fileViewerFile ? props.fileViewers : [];
  const fileViewer = (viewerId: string) => {
    const view = fileViewers.find((item) => item.id === viewerId);
    if (!view) throw new Error(`File viewer ${viewerId} is not available.`);
    return view;
  };
  /** 查看器在 "More" 菜单某一组中的条目。A viewer's file header items in one group of the "More" menu. */
  const headerItems = (viewerId: string, placement: "top" | "bottom") =>
    (props.snapshot.fileHeaderItems[viewerId] ?? []).filter((item) => item.placement === placement);

  return (
    <main
      className="agw-main-workspace relative"
      aria-label={activeView.title}
      data-view-id={activeView.id}
    >
      <WindowFloatingChatButton host={props.host} snapshot={props.snapshot} hidden={isChat} />
      <FloatingChatPanel
        mode={floatingChatMode}
        onClose={() => props.host.closeFloatingChat()}
        onNewConversation={workspace.startNewConversation}
      >
        <ChatConversationPane
          compact
          showUserInputNavigation
          composerActions={props.composerActions}
          messageActions={props.messageActions}
          context={floatingChatMode === "floating" ? floatingChat?.context : undefined}
        />
      </FloatingChatPanel>
      {activeView.id === BUILT_IN_VIEWS.jobs ? (
        <JobsWorkspace
          key={props.openRequests[BUILT_IN_VIEWS.jobs]?.sequence}
          onOpenChat={() => props.host.layout.openMainView(BUILT_IN_VIEWS.chat)}
        />
      ) : null}
      {activeView.id === BUILT_IN_VIEWS.events ? (
        <div className="min-h-0 flex-1 overflow-y-auto agw-scrollbar">
          <EventsPage />
        </div>
      ) : null}
      {activeView.id === BUILT_IN_VIEWS.notifications ? (
        <NotificationsPage
          desktopBridge={typeof window === "undefined" ? null : window.agwDesktop}
          serverId={props.host.context.snapshot().server.id}
          initialNotificationId={notificationIdFromRequest(
            props.openRequests[BUILT_IN_VIEWS.notifications]?.params,
          )}
          liveUpdatesEnabled={false}
        />
      ) : null}
      {activeView.id === BUILT_IN_VIEWS.file ? (
        <ChatFilePane
          fileViewers={fileViewers.map((view) => ({
            id: view.id,
            title: view.title,
            unsaved: props.snapshot.unsavedViewIds.includes(view.id),
            saveStatus: props.snapshot.saveStatuses[view.id] ?? null,
            topItems: headerItems(view.id, "top"),
            bottomItems: headerItems(view.id, "bottom"),
          }))}
          renderFileViewer={(viewerId) => {
            const view = fileViewers.find((item) => item.id === viewerId);
            return view && fileViewerFile ? (
              <ExtensionView
                key={view.id}
                host={props.host}
                layout={props.layout}
                snapshot={props.snapshot}
                view={view}
                openRequest={undefined}
                file={fileViewerFile}
                showHeader={false}
              />
            ) : null;
          }}
          onReloadFileViewer={(viewerId) =>
            void reloadExtensionView(props.host, fileViewer(viewerId))
          }
          onReloadFileViewerDocument={(viewerId) => props.host.reloadFileViewerDocument(viewerId)}
          renderFileViewerInfo={(viewerId) => <ExtensionViewInfo view={fileViewer(viewerId)} />}
          onSelectFileViewerItem={(viewerId, itemId, entryId) =>
            props.host.selectFileHeaderItem(viewerId, itemId, entryId)
          }
          defaultFileViewerId={props.defaultFileViewerId}
          onSetDefaultFileViewer={props.onSetDefaultFileViewer}
          editableSource={props.editableSource}
          renderSourceAccessory={() =>
            props.sourceChatContext ? (
              <SourceFloatingChatContext host={props.host} context={props.sourceChatContext} />
            ) : null
          }
        />
      ) : null}
      {props.retainedViews.map((view) => extensionView(view, view.id !== activeView.id))}
      {activeView.extensionId !== null && !retained.has(activeView.id)
        ? extensionView(activeView, false)
        : null}
    </main>
  );
}
