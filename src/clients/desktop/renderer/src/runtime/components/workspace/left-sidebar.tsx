"use client";

import { ChatConversationSidebar, ChatFilesSidebar, type ExtensionMenuItem } from "@agw/chat";
import type { ConversationItemActionArgs, ExplorerContextMenuArgs } from "@agw/projects";

import type { ExtensionHost, ExtensionHostSnapshot } from "../../extensions/extension-host";
import type { ExtensionLayoutSampler } from "../../extensions/extension-layout-sampler";
import { BUILT_IN_VIEWS } from "../../extensions/workspace-ids";
import type { ViewOpenRequest } from "../../extensions/workspace-layout-store";
import type { WorkspaceView } from "../../extensions/workspace-registry";
import { ExtensionView } from "./extension-view";

export type LeftSidebarProps = {
  host: ExtensionHost;
  layout: ExtensionLayoutSampler;
  snapshot: ExtensionHostSnapshot;
  view: WorkspaceView;
  openRequest: ViewOpenRequest | undefined;
  conversationActions: readonly ExtensionMenuItem<ConversationItemActionArgs>[];
  explorerActions: readonly ExtensionMenuItem<ExplorerContextMenuArgs>[];
  onConversationOpened(): void;
  onFileOpened(): void;
};

/** 渲染 `activeSidebarViewId` 对应的视图。Renders the view of `activeSidebarViewId`. */
export function LeftSidebar(props: LeftSidebarProps) {
  const { view } = props;
  return (
    <aside className="agw-left-sidebar" aria-label={view.title} data-view-id={view.id}>
      {view.id === BUILT_IN_VIEWS.conversations ? (
        <ChatConversationSidebar
          onConversationOpened={props.onConversationOpened}
          itemActions={props.conversationActions}
        />
      ) : view.id === BUILT_IN_VIEWS.files ? (
        <ChatFilesSidebar
          onFileOpened={props.onFileOpened}
          contextMenuItems={props.explorerActions}
        />
      ) : (
        <ExtensionView
          key={view.id}
          host={props.host}
          layout={props.layout}
          snapshot={props.snapshot}
          view={view}
          openRequest={props.openRequest}
        />
      )}
    </aside>
  );
}
