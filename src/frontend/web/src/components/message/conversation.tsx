"use client";

import * as React from "react";
import { Uuid4 } from "id128";
import { toast } from "sonner";

import { Button } from "@/components/ui/button";
import { ChatSession } from "@/components/message/chat-session";
import { cn } from "@/lib/utils";
import type { AiMessage, ProcessedMessageItem } from "@/types";
import {
  createUserTextMessage,
  executeWithWebSocketStream,
  mergeStreamingMessage,
} from "@/lib/execution-stream";
import { UserInput } from "./user-input";
import { ArrowUp, Eraser, Square } from "lucide-react";
import { Separator } from "../ui/separator";
import { useQuery } from "@tanstack/react-query";
import { deleteSessionBySessionId, getSessionBySessionId } from "@/app/(app)/(external-agents)/claude-code/lib/chat-history-service";

export interface ConversationProps {
  executionId: string | null | undefined;
  agentType: number;
  projectId: string;
  sessionId?: string | null;
  resetSignal?: string | number | boolean;
  placeholder?: string;
  className?: string;
  onExecutionComplete?: () => void | Promise<void>;
  onExecutionError?: (error: unknown) => void;
}

function nextSessionId(): string {
  return Uuid4.generate().toCanonical();
}

export function Conversation({
  executionId,
  agentType,
  projectId,
  sessionId,
  resetSignal,
  placeholder = "Type your message...",
  className,
  onExecutionComplete,
  onExecutionError,
}: ConversationProps) {
  const [isExecuting, setIsExecuting] = React.useState(false);
  const [messages, setMessages] = React.useState<AiMessage[]>([]);
  const [curSessionId, setSessionId] = React.useState<string>(
    sessionId ?? nextSessionId(),
  );
  const messagesEndRef = React.useRef<HTMLDivElement>(null!);
  const messagesStartRef = React.useRef<HTMLDivElement>(null!);

  const processMessages = React.useCallback(
    (items: AiMessage[]): ProcessedMessageItem[] =>
      items.map((message) => ({ type: "normal", message })),
    [],
  );

  const updateSessionId = React.useCallback((value: string | null) => {
    setSessionId(value);
  }, []);

  const sessionQuery = useQuery({
    queryKey: ["projects", projectId, "tasks", curSessionId, "session-record"],
    queryFn: async () => {
      return await getSessionBySessionId(curSessionId, projectId);
    },
    enabled: Boolean(curSessionId),
    refetchInterval: false,
  });

  React.useEffect(() => {
    const sessionMessages = sessionQuery.data?.messages ?? [];
    if(sessionMessages.length > 0){ 
      setMessages(sessionMessages);
    }
    messagesEndRef.current?.scrollIntoView({ behavior: "smooth" });
  }, []);

  React.useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages]);

  const handleSend = React.useCallback(
    async (value: string) => {
      if (!executionId) {
        toast.error("Please enter executionId");
        return;
      }
      if (!value.trim()) {
        toast.error("Please enter a prompt");
        return;
      }

      setMessages((prev) => [...prev, createUserTextMessage(value)]);
      setIsExecuting(true);

      try {
        await executeWithWebSocketStream({
          id: executionId,
          request: {
            agentType,
            sessionId: curSessionId,
            projectId,
            input: value,
          },
          onMessage: (message) => {
            setMessages((prev) => mergeStreamingMessage(prev, message));
          },
        });
        // await sessionQuery.refetch();
        await onExecutionComplete?.();
      } catch (error) {
        console.error("Execute failed:", error);
        if (onExecutionError) {
          onExecutionError(error);
        } else {
          toast.error(
            `Execute failed: ${error instanceof Error ? error.message : "Unknown error"}`,
          );
        }
      } finally {
        setIsExecuting(false);
      }
    },
    [
      agentType,
      executionId,
      onExecutionComplete,
      onExecutionError,
      projectId,
      curSessionId,
      updateSessionId,
    ],
  );

  const handleClear = React.useCallback(async () => {
    const success = await deleteSessionBySessionId(curSessionId, projectId);
    if(success){
      setMessages([]);
    }
  }, [curSessionId, projectId]);

  const handleScrollToTop = () => {
    messagesStartRef.current?.scrollIntoView({
      behavior: "smooth",
      block: "start",
    });
  };

  return (
    <div className={cn("flex h-full relative", className)}>

      <ChatSession
        messages={messages}
        messagesStartRef={messagesStartRef}
        messagesEndRef={messagesEndRef}
        processMessages={processMessages}
      />

      <div className="absolute bottom-0 z-10 left-0 right-0 h-30 px-2 bg-linear-to-t from-bg-000 from-50% via-bg-000/80 via-70% to-transparent pointer-events-none">
        <UserInput
          isExecuting={isExecuting}
          onExecute={handleSend}
          placeholder={placeholder}
        >
          {/* <UserInput.TopLeft></UserInput.TopLeft> */}

          <UserInput.TopRight>
            <Button
              onClick={handleClear}
              disabled={isExecuting}
              variant="ghost"
              size="sm"
            >
              <Eraser width={16} />
            </Button>

            <Separator orientation="vertical" />
            <Button onClick={handleScrollToTop} variant="ghost" size="sm">
              <ArrowUp width={16} />
            </Button>
          </UserInput.TopRight>

          {/* {isExecuting ? executingLabel : sendLabel} */}

          {isExecuting ? (
            <UserInput.Sender>
              <Square size={20} />
            </UserInput.Sender>
          ) : null}

          {/* {messages.length > 0 && (
            <Button variant="outline" onClick={handleClear} className="w-full">
              清空会话
            </Button>
          )} */}
        </UserInput>
      </div>
    </div>
  );
}
