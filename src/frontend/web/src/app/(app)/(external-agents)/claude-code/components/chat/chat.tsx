"use client";

import * as React from "react";
import dynamic from "next/dynamic";
import { PanelLeft, PanelLeftClose, PanelLeftOpen } from "lucide-react";
import { ChatSession, ChatSessionProps } from "@/components/message/chat-session";
import type { AiMessage } from "@/types";
import ColResizeSplit from "../split-layout";
import {
  CLAUDE_CODE_PROJECT_ID,
  getSessionBySessionId,
  type ChatSessionRecordDetails,
} from "../../lib/chat-history-service";
import {
  Drawer,
  DrawerContent,
  DrawerHeader,
  DrawerTitle,
  DrawerTrigger,
} from "@/components/ui/drawer";
import { Button } from "@/components/ui/button";

// Dynamically import ChatHistoryList to keep the chat shell lightweight
const ChatHistoryList = dynamic(
  () => import("./chat-history-list").then((mod) => ({ default: mod.ChatHistoryList })),
  { ssr: false },
);
export interface ChatProps extends ChatSessionProps {
  currentSessionId: string | null;
  onSessionSelect: (messages: AiMessage[], sessionId: string) => void;
  onNewChat: () => void;
  onSessionDeleted: (sessionId: string) => void;
  onAllSessionsCleared: () => void;
}

export function Chat({
  currentSessionId,
  onSessionSelect,
  onNewChat,
  onSessionDeleted,
  onAllSessionsCleared,
  ...messageAreaProps
}: ChatProps) {
  const [isMobile, setIsMobile] = React.useState(false);
  const [isDrawerOpen, setIsDrawerOpen] = React.useState(false);
  const [showChatHistory, setShowChatHistory] = React.useState(true);

  React.useEffect(() => {
    const mediaQuery = window.matchMedia("(max-width: 768px)");
    const handleMediaChange = (event: MediaQueryListEvent) => {
      setIsMobile(event.matches);
    };

    setIsMobile(mediaQuery.matches);
    mediaQuery.addEventListener("change", handleMediaChange);
    return () => mediaQuery.removeEventListener("change", handleMediaChange);
  }, []);

  const handleSessionSelect = async (sessionId: string) => {
    try {
      const details: ChatSessionRecordDetails | null = await getSessionBySessionId(
        sessionId,
        CLAUDE_CODE_PROJECT_ID,
      );
      if (!details) {
        return;
      }
      onSessionSelect(details.messages ?? [], details.sessionId);
    } catch (error) {
      console.error("Failed to load session:", error);
    } finally {
      setIsDrawerOpen(false);
    }
  };

  return (
    <>
      <div className="flex flex-1 flex-col">
        {!isMobile && (
          <div className="flex items-center gap-4 pb-2">
            <Button
              variant="ghost"
              className="cursor-pointer"
              size="sm"
              onClick={() => setShowChatHistory(!showChatHistory)}
              title={showChatHistory ? "Hide chat history" : "Show chat history"}
            >
              {showChatHistory ? (
                <PanelLeftClose className="h-4 w-4" />
              ) : (
                <PanelLeftOpen className="h-4 w-4" />
              )}
            </Button>
          </div>
        )}
        {isMobile && (
          <Drawer direction="left" open={isDrawerOpen} onOpenChange={setIsDrawerOpen}>
            <DrawerTrigger asChild>
              <Button variant="outline" size="sm" className="gap-2">
                <PanelLeft className="h-4 w-4" />
                Chat History
              </Button>
            </DrawerTrigger>
            <DrawerContent className="h-screen max-h-screen">
              <DrawerHeader>
                <DrawerTitle>Chat History</DrawerTitle>
              </DrawerHeader>
              <div className="px-4 pb-6">
                <ChatHistoryList
                  currentSessionId={currentSessionId}
                  onSessionSelect={handleSessionSelect}
                  onNewChat={onNewChat}
                  onSessionDeleted={onSessionDeleted}
                  onAllSessionsCleared={onAllSessionsCleared}
                />
              </div>
            </DrawerContent>
          </Drawer>
        )}

        <ColResizeSplit>
          {!isMobile && showChatHistory && (
            <ColResizeSplit.Left>
              <ChatHistoryList
                currentSessionId={currentSessionId}
                onSessionSelect={handleSessionSelect}
                onNewChat={onNewChat}
                onSessionDeleted={onSessionDeleted}
                onAllSessionsCleared={onAllSessionsCleared}
              />
            </ColResizeSplit.Left>
          )}

          <ColResizeSplit.Right>
            <div className="flex flex-col flex-1 px-2">
              <ChatSession {...messageAreaProps} />
            </div>
          </ColResizeSplit.Right>
        </ColResizeSplit>
      </div>
    </>
  );
}
