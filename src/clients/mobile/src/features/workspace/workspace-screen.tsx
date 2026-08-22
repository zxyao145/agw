import type { NativeConversationHistoryHandle } from "@agw/chat-native/conversation";
import React from "react";
import { StyleSheet, View } from "react-native";

import { ChatScreen } from "@/features/chat/chat-screen";
import { FilesScreen } from "@/features/files/files-screen";
import { WorkspaceShell } from "./workspace-shell";
import type { WorkspacePaneHandle, WorkspaceTab } from "./workspace-types";

export function WorkspaceScreen({ initialTab }: { initialTab: WorkspaceTab }): React.JSX.Element {
  const [activeTab, setActiveTab] = React.useState<WorkspaceTab>(initialTab);
  const [visitedTabs, setVisitedTabs] = React.useState<Record<WorkspaceTab, boolean>>({
    chat: initialTab === "chat",
    files: initialTab === "files",
  });
  const chatRef = React.useRef<NativeConversationHistoryHandle>(null);
  const filesRef = React.useRef<WorkspacePaneHandle>(null);

  const selectTab = React.useCallback((tab: WorkspaceTab) => {
    setVisitedTabs((current) => (current[tab] ? current : { ...current, [tab]: true }));
    setActiveTab(tab);
  }, []);

  const scrollToTop = React.useCallback(() => {
    const activeRef = activeTab === "chat" ? chatRef : filesRef;
    activeRef.current?.scrollToTop();
  }, [activeTab]);

  const scrollToBottom = React.useCallback(() => {
    chatRef.current?.scrollToBottom();
  }, []);

  return (
    <WorkspaceShell
      active={activeTab}
      onScrollToBottom={activeTab === "chat" ? scrollToBottom : undefined}
      onScrollToTop={scrollToTop}
      onTabChange={selectTab}
    >
      {visitedTabs.chat ? (
        <WorkspacePane active={activeTab === "chat"}>
          <ChatScreen ref={chatRef} />
        </WorkspacePane>
      ) : null}
      {visitedTabs.files ? (
        <WorkspacePane active={activeTab === "files"}>
          <FilesScreen ref={filesRef} />
        </WorkspacePane>
      ) : null}
    </WorkspaceShell>
  );
}

function WorkspacePane({
  active,
  children,
}: {
  active: boolean;
  children: React.ReactNode;
}): React.JSX.Element {
  return (
    <View
      accessibilityElementsHidden={!active}
      importantForAccessibility={active ? "auto" : "no-hide-descendants"}
      style={[styles.pane, !active && styles.hidden]}
    >
      {children}
    </View>
  );
}

const styles = StyleSheet.create({
  pane: { flex: 1, minHeight: 0 },
  hidden: { display: "none" },
});
