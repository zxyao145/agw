"use client";

import * as React from "react";
import dynamic from "next/dynamic";
import { ChatSession,ChatSessionProps } from "../../../../../components/message/chat-session";
import type { AiMessage } from "@/types";
import ColResizeSplit from "../split-layout";
import { getSessionByThreadId, type ChatSessionRecordDetails } from "../../lib/chat-history-service";

// Dynamically import ChatHistoryList to keep the chat shell lightweight
const ChatHistoryList = dynamic(
  () => import("./chat-history-list").then(mod => ({ default: mod.ChatHistoryList })),
  { ssr: false }
);
export interface ChatProps extends ChatSessionProps {
  currentThreadId: string | null;
  onSessionSelect: (messages: AiMessage[], threadId: string) => void;
  onNewChat: () => void;
  onSessionDeleted: (threadId: string) => void;
  onAllSessionsCleared: () => void;
}

export function Chat({
  currentThreadId,
  onSessionSelect,
  onNewChat,
  onSessionDeleted,
  onAllSessionsCleared,
  ...messageAreaProps
}: ChatProps) {
  const handleSessionSelect = async (sessionId: string) => {
    try {
      const details: ChatSessionRecordDetails | null =
        await getSessionByThreadId(sessionId);
      if (!details) {
        return;
      }
      onSessionSelect(details.messages ?? [], details.sessionId);
    } catch (error) {
      console.error("Failed to load session:", error);
    }
  };

  return (
    <>
      <ColResizeSplit>
        <ColResizeSplit.Left>
          <ChatHistoryList
            currentThreadId={currentThreadId}
            onSessionSelect={handleSessionSelect}
            onNewChat={onNewChat}
            onSessionDeleted={onSessionDeleted}
            onAllSessionsCleared={onAllSessionsCleared}
          />
        </ColResizeSplit.Left>

        <ColResizeSplit.Right>
          <div className="flex flex-col flex-1 px-2">
            <ChatSession {...messageAreaProps} />
          </div>
        </ColResizeSplit.Right>
      </ColResizeSplit>
    </>
  );
}
