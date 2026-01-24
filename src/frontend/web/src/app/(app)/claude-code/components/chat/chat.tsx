"use client";

import * as React from "react";
import { Panel, Group, Separator } from "react-resizable-panels";
import { ChatMessageArea } from "./chat-message-area";
import { ChatHistoryList } from "../chat-history-list";
import type { ChatMessageAreaProps } from "../../types";
import type { ChatSessionDocument } from "../../lib/chat-history-db";
import type { AiMessage } from "@/types";

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
      <Group orientation="horizontal">
        {/* Chat History Panel */}
        <Panel defaultSize={25} minSize={15} maxSize={40}>
          <ChatHistoryList
            currentThreadId={currentThreadId}
            onSessionSelect={handleSessionSelect}
            onNewChat={onNewChat}
          />
        </Panel>

        {/* Resize Handle */}
        <Separator className="w-1 bg-border hover:bg-primary/20 transition-colors" />

        {/* Chat Messages Panel */}
        <Panel defaultSize={75} minSize={60}>
          <div className="flex flex-col h-full overflow-hidden">
            <ChatMessageArea {...messageAreaProps} />
          </div>
        </Panel>
      </Group>
    </div>
  );
}
