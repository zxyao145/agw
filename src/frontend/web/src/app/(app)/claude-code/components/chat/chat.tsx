"use client";

import * as React from "react";
import dynamic from "next/dynamic";
import { ChatSession } from "./chat-session";
import type { ChatMessageAreaProps } from "../../types";
import type { ChatSessionDocument } from "../../lib/chat-history-db";
import type { AiMessage } from "@/types";
import ColResizeSplit from "../split-layout";

// Dynamically import ChatHistoryList to avoid SSR issues with PouchDB
const ChatHistoryList = dynamic(
  () => import("./chat-history-list").then(mod => ({ default: mod.ChatHistoryList })),
  { ssr: false }
);
export interface ChatProps extends ChatMessageAreaProps {
  currentThreadId: string | null;
  onSessionSelect: (messages: AiMessage[], threadId: string) => void;
  onNewChat: () => void;
}

export function Chat({
  currentThreadId,
  onSessionSelect,
  onNewChat,
  ...messageAreaProps
}: ChatProps) {
  const handleSessionSelect = (session: ChatSessionDocument) => {
    onSessionSelect(session.messages, session.threadId);
  };

  return (
    <>
      <ColResizeSplit>
        <ColResizeSplit.Left>
          <ChatHistoryList
            currentThreadId={currentThreadId}
            onSessionSelect={handleSessionSelect}
            onNewChat={onNewChat}
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
