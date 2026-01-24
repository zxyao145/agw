"use client";

import * as React from "react";
import { ChatMessageArea } from "./chat-message-area";
import { ChatHistoryList } from "./chat-history-list";
import type { ChatMessageAreaProps } from "../../types";
import type { ChatSessionDocument } from "../../lib/chat-history-db";
import type { AiMessage } from "@/types";
import ColResizeSplit from "../split-layout";

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
    <div className="flex flex-col h-full">
      <ColResizeSplit>
        <ColResizeSplit.Left>
          <ChatHistoryList
            currentThreadId={currentThreadId}
            onSessionSelect={handleSessionSelect}
            onNewChat={onNewChat}
          />
        </ColResizeSplit.Left>

        <ColResizeSplit.Right>
          <div className="flex flex-col h-full overflow-hidden">
            <ChatMessageArea {...messageAreaProps} />
          </div>
        </ColResizeSplit.Right>
      </ColResizeSplit>
    </div>
  );
}
