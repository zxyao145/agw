"use client";

import * as React from "react";
import dynamic from "next/dynamic";
import { PanelLeft } from "lucide-react";
import { ChatSession, ChatSessionProps } from "../../../../../components/message/chat-session";
import type { AiMessage } from "@/types";
import ColResizeSplit from "../split-layout";
import { getSessionByThreadId, type ChatSessionRecordDetails } from "../../lib/chat-history-service";
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
  const [isMobile, setIsMobile] = React.useState(false);
  const [isDrawerOpen, setIsDrawerOpen] = React.useState(false);

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
      const details: ChatSessionRecordDetails | null =
        await getSessionByThreadId(sessionId);
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
        {isMobile && (
          <div className="flex items-center justify-between border-b px-2 py-2">
            <Drawer open={isDrawerOpen} onOpenChange={setIsDrawerOpen}>
              <DrawerTrigger asChild>
                <Button variant="outline" size="sm" className="gap-2">
                  <PanelLeft className="h-4 w-4" />
                  Chat History
                </Button>
              </DrawerTrigger>
              <DrawerContent className="max-h-[80vh]">
                <DrawerHeader>
                  <DrawerTitle>Chat History</DrawerTitle>
                </DrawerHeader>
                <div className="px-4 pb-6">
                  <ChatHistoryList
                    currentThreadId={currentThreadId}
                    onSessionSelect={handleSessionSelect}
                    onNewChat={onNewChat}
                    onSessionDeleted={onSessionDeleted}
                    onAllSessionsCleared={onAllSessionsCleared}
                  />
                </div>
              </DrawerContent>
            </Drawer>
          </div>
        )}

        <ColResizeSplit>
          {!isMobile && (
            <ColResizeSplit.Left>
              <ChatHistoryList
                currentThreadId={currentThreadId}
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
