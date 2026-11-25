"use client";

import {
  ChatConversationSidebar,
  ChatFilesSidebar,
  type ChatConversationSidebarProps,
  type ChatFilesSidebarProps,
} from "@agw/chat";

import type { ExtensionHost, ExtensionHostSnapshot } from "../../extensions/extension-host";
import type { ExtensionLayoutSampler } from "../../extensions/extension-layout-sampler";
import { BUILT_IN_VIEWS, sidebarInstanceKey } from "../../extensions/workspace-ids";
import type { ViewOpenRequest } from "../../extensions/workspace-layout-store";
import type { WorkspaceView } from "../../extensions/workspace-registry";
import { ExtensionView } from "./extension-view";

export type LeftSidebarProps = {
  host: ExtensionHost;
  layout: ExtensionLayoutSampler;
  snapshot: ExtensionHostSnapshot;
  view: WorkspaceView;
  openRequest: ViewOpenRequest | undefined;
  conversation: ChatConversationSidebarProps;
  files: ChatFilesSidebarProps;
};

/** 渲染 `activeSidebarViewId` 对应的视图。Renders the view of `activeSidebarViewId`. */
export function LeftSidebar(props: LeftSidebarProps) {
  const { view } = props;
  return (
    <aside className="agw-left-sidebar" aria-label={view.title} data-view-id={view.id}>
      {view.id === BUILT_IN_VIEWS.conversations ? (
        <ChatConversationSidebar {...props.conversation} />
      ) : view.id === BUILT_IN_VIEWS.files ? (
        <ChatFilesSidebar {...props.files} />
      ) : (
        <ExtensionView
          key={view.id}
          host={props.host}
          layout={props.layout}
          snapshot={props.snapshot}
          instanceKey={sidebarInstanceKey(view.id)}
          view={view}
          openRequest={props.openRequest}
        />
      )}
    </aside>
  );
}
