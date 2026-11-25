"use client";

import * as React from "react";
import {
  ChatConversationPane,
  ChatSessionProvider,
  useChatSession,
  type ChatConversationPaneProps,
  type ChatSessionProviderProps,
} from "@agw/chat";
import { Button } from "@agw/components";
import { createExtensionError } from "@agw/extension-sdk";

import type { ExtensionHost } from "../../extensions/extension-host";
import type { ChatTab } from "../../extensions/workspace-tab";

export type ChatSelection = Parameters<
  NonNullable<ChatSessionProviderProps["onSelectionChange"]>
>[0];
export type DesktopChatActions = Pick<
  ChatConversationPaneProps,
  "composerActions" | "messageActions"
>;

export function DesktopChatPane({
  host,
  visible,
  onClose,
  closeLabel,
  ...pane
}: ChatConversationPaneProps & {
  host: ExtensionHost;
  visible: boolean;
  onClose(): void;
  closeLabel: string;
}) {
  const { composerRef, loadError, retry } = useChatSession();
  React.useLayoutEffect(() => {
    if (!visible || loadError !== null) return;
    return host.registerComposer({
      insertText(text) {
        const composer = composerRef.current;
        if (!composer)
          throw createExtensionError("ERR_UNAVAILABLE", "No chat composer is visible.");
        composer.insertText(text);
      },
    });
  }, [host, visible, loadError, composerRef]);

  if (loadError !== null) {
    return (
      <div
        role="alert"
        className="flex min-h-0 flex-1 flex-col items-center justify-center gap-3 p-6 text-center text-sm"
      >
        <p>Failed to load the conversation: {loadError}</p>
        <div className="flex gap-2">
          <Button type="button" variant="outline" size="sm" onClick={retry}>
            Retry
          </Button>
          <Button type="button" variant="outline" size="sm" onClick={onClose}>
            {closeLabel}
          </Button>
        </div>
      </div>
    );
  }
  return <ChatConversationPane {...pane} showUserInputNavigation />;
}

export function ChatTabPanel({
  host,
  tab,
  active,
  onSelectionChange,
  ...actions
}: DesktopChatActions & {
  host: ExtensionHost;
  tab: ChatTab;
  active: boolean;
  onSelectionChange(selection: ChatSelection): void;
}) {
  const tabs = host.tabs;
  const isOpen = () => tabs.getSnapshot().group?.tabs.some((item) => item.id === tab.id);
  const close = () => {
    void tabs.close([tab.id]);
  };
  return (
    <ChatSessionProvider
      conversationId={tab.conversationId}
      draftId={tab.draftId}
      newConversationId={tab.draftId}
      draftSubmitted={tab.draftSubmitted}
      visible={active}
      onConversationAccepted={(id) => {
        if (isOpen()) tabs.acceptConversation(tab.id, id);
      }}
      onDraftLost={() => {
        if (isOpen()) tabs.setDraftSubmitted(tab.id, false);
      }}
      onTitleChange={(title) => {
        if (isOpen()) tabs.setChatTitle(tab.id, title);
      }}
      onSubmitted={() => {
        if (!isOpen()) return;
        tabs.pin(tab.id);
        if (tab.conversationId === null) tabs.setDraftSubmitted(tab.id, true);
      }}
      onSelectionChange={onSelectionChange}
    >
      <div className="relative flex min-h-0 flex-1 flex-col">
        <DesktopChatPane
          host={host}
          visible={active}
          onClose={close}
          closeLabel="Close tab"
          {...actions}
        />
      </div>
    </ChatSessionProvider>
  );
}
