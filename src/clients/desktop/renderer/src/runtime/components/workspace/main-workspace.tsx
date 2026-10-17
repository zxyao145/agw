"use client";

import {
  ChatConversationPane,
  ChatFilePane,
  type ChatMessageActionArgs,
  type ExtensionMenuItem,
} from "@agw/chat";

import type { ExtensionHost, ExtensionHostSnapshot } from "../../extensions/extension-host";
import type { HostOverlayTracker } from "../../extensions/host-overlay-tracker";
import { BUILT_IN_VIEWS } from "../../extensions/workspace-ids";
import type { ViewOpenRequest } from "../../extensions/workspace-layout-store";
import type { WorkspaceView } from "../../extensions/workspace-registry";
import { ExtensionView } from "./extension-view";

export type MainWorkspaceProps = {
  host: ExtensionHost;
  tracker: HostOverlayTracker;
  snapshot: ExtensionHostSnapshot;
  activeView: WorkspaceView;
  /** 打开过且需要保留页面状态的 extension 主区视图。Opened extension main views that keep their page state. */
  retainedViews: readonly WorkspaceView[];
  openRequests: Readonly<Record<string, ViewOpenRequest>>;
  composerActions: readonly ExtensionMenuItem<Record<string, never>>[];
  messageActions: readonly ExtensionMenuItem<ChatMessageActionArgs>[];
};

/**
 * 渲染 `activeMainViewId` 对应的视图。内置对话视图常驻挂载、切走时隐藏；内置文件视图只在激活时挂载；
 * extension 主区视图按 `retainContextWhenHidden` 决定切走时隐藏或卸载。
 * Renders the `activeMainViewId` view. Chat stays mounted and hidden; File mounts only when active; extension
 * main views hide or unload according to `retainContextWhenHidden`.
 */
export function MainWorkspace(props: MainWorkspaceProps) {
  const { activeView } = props;
  const isChat = activeView.id === BUILT_IN_VIEWS.chat;
  const retained = new Set(props.retainedViews.map((view) => view.id));
  const extensionView = (view: WorkspaceView, hidden: boolean) => (
    <div key={view.id} className="flex min-h-0 flex-1 flex-col" hidden={hidden}>
      <ExtensionView
        host={props.host}
        tracker={props.tracker}
        snapshot={props.snapshot}
        view={view}
        openRequest={props.openRequests[view.id]}
        active={!hidden}
      />
    </div>
  );

  return (
    <main className="agw-main-workspace" aria-label={activeView.title} data-view-id={activeView.id}>
      <div className="flex min-h-0 flex-1 flex-col" hidden={!isChat}>
        <ChatConversationPane
          compact
          showUserInputNavigation
          composerActions={props.composerActions}
          messageActions={props.messageActions}
        />
      </div>
      {activeView.id === BUILT_IN_VIEWS.file ? <ChatFilePane /> : null}
      {props.retainedViews.map((view) => extensionView(view, view.id !== activeView.id))}
      {activeView.extensionId !== null && !retained.has(activeView.id)
        ? extensionView(activeView, false)
        : null}
    </main>
  );
}
