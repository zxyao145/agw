"use client";

import { ChatSessionProvider, FloatingChatPanel } from "@agw/chat";

import type { ExtensionHost, ExtensionHostSnapshot } from "../../extensions/extension-host";
import type { TabGroup } from "../../extensions/workspace-tab";
import { DesktopChatPane, type ChatSelection, type DesktopChatActions } from "./chat-tab-panel";

export function DesktopFloatingChat({
  host,
  snapshot,
  floatingChat,
  visible,
  onSelectionChange,
  ...actions
}: DesktopChatActions & {
  host: ExtensionHost;
  snapshot: ExtensionHostSnapshot;
  floatingChat: TabGroup["floatingChat"];
  visible: boolean;
  onSelectionChange(selection: ChatSelection): void;
}) {
  const { conversationId, draftId, draftSubmitted } = floatingChat;
  const tabs = host.tabs;
  const isCurrent = () => tabs.getSnapshot().group?.floatingChat.draftId === draftId;
  const close = () => host.closeFloatingChat();
  return (
    <FloatingChatPanel
      mode={visible ? "floating" : "hidden"}
      onClose={close}
      onNewConversation={tabs.floatingChat.newConversation}
    >
      <ChatSessionProvider
        key={draftId}
        conversationId={conversationId}
        draftId={draftId}
        newConversationId={draftId}
        draftSubmitted={draftSubmitted}
        visible={visible}
        onConversationAccepted={(id) => {
          if (isCurrent()) tabs.floatingChat.acceptConversation(id);
        }}
        onDraftLost={() => {
          if (isCurrent()) tabs.floatingChat.setDraftSubmitted(false);
        }}
        onSubmitted={() => {
          if (isCurrent() && conversationId === null) tabs.floatingChat.setDraftSubmitted(true);
        }}
        onSelectionChange={onSelectionChange}
      >
        <DesktopChatPane
          host={host}
          visible={visible}
          closeLabel="Close chat"
          onClose={close}
          compact
          context={visible ? snapshot.floatingChat?.context : undefined}
          {...actions}
        />
      </ChatSessionProvider>
    </FloatingChatPanel>
  );
}
