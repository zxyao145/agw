"use client";

import * as React from "react";
import { Uuid4 } from "id128";
import { toast } from "sonner";

import { Button } from "@/components/ui/button";
import { Conversation } from "@/components/message/conversation";
import { cn } from "@/lib/utils";
import type { AiMessage, ProcessedMessageItem } from "@/types";
import {
  createUserTextMessage,
  executeWithWebSocketStream,
  mergeStreamingMessagesById,
  toExecutionWsUserInput,
} from "@/lib/execution-stream";
import { UserInput, UserInputRef } from "./user-input";
import { ArrowUp, Eraser, Square } from "lucide-react";
import { Separator } from "../ui/separator";
import { useQuery } from "@tanstack/react-query";
import { deleteTaskById, getTaskDetails } from "@/api/task-client";
import { QuickTextDialog } from "../task/quick-text-dialog";

export interface ChatProps {
  executionId: string | null | undefined;
  agentType: number;
  projectId: string;
  taskId?: string | null;
  resume?: boolean;
  resetSignal?: string | number | boolean;
  placeholder?: string;
  className?: string;
  onExecutionComplete?: () => void | Promise<void>;
  onExecutionError?: (error: unknown) => void;
}

function nextTaskId(): string {
  return Uuid4.generate().toCanonical();
}

export function Chat({
  executionId, // agent executionId
  agentType, // agent type
  projectId,
  taskId,
  resume = false,
  resetSignal,
  placeholder = "Type your message...",
  className,
  onExecutionComplete,
  onExecutionError,
}: ChatProps) {
  const [isExecuting, setIsExecuting] = React.useState(false);
  const [messages, setMessages] = React.useState<AiMessage[]>([]);
  const [curTaskId, setTaskId] = React.useState<string>(taskId ?? nextTaskId());
  const messagesEndRef = React.useRef<HTMLDivElement>(null!);
  const messagesStartRef = React.useRef<HTMLDivElement>(null!);
  const userInputRef = React.useRef<UserInputRef | null>(null);

  const processMessages = React.useCallback(
    (items: AiMessage[]): ProcessedMessageItem[] =>
      items.map((message) => ({ type: "normal", message })),
    [],
  );

  const isTurnFinishedMessage = React.useCallback((message: AiMessage): boolean => {
    if (message.role?.toLowerCase() !== "system") {
      return false;
    }

    if (message.author !== "$agw-server") {
      return false;
    }

    return message.contents.some(
      (content) => content.additionalProperties?.type === "turn-finished",
    );
  }, []);

  const sessionQuery = useQuery({
    queryKey: ["projects", projectId, "tasks", taskId, "task-record"],
    queryFn: async () => {
      return await getTaskDetails(taskId!, projectId);
    },
    enabled: Boolean(taskId),
    refetchInterval: false,
  });

  React.useEffect(() => {
    setTaskId(taskId ?? nextTaskId());
    setMessages([]);
  }, [resetSignal, taskId]);

  React.useEffect(() => {
    const sessionMessages = sessionQuery.data?.messages ?? [];
    if (sessionMessages.length > 0) {
      setMessages(sessionMessages);
    }
    messagesEndRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [sessionQuery.data]);

  React.useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages]);

  const handleQuickCommand = (text: string) => {
    userInputRef.current?.insertText(text);
  };

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

      const userMessage = createUserTextMessage(value);
      setMessages((prev) => [...prev, userMessage]);
      setIsExecuting(true);

      try {
        await executeWithWebSocketStream({
          id: executionId,
          request: {
            agentType,
            projectId,
            taskId,
            resume,
            input: toExecutionWsUserInput(userMessage),
          },
          onMessage: (message) => {
            if (isTurnFinishedMessage(message)) {
              setIsExecuting(false);
            } else {
              setMessages((prev) => mergeStreamingMessagesById([...prev, message]));
            }
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
      isTurnFinishedMessage,
      onExecutionComplete,
      onExecutionError,
      projectId,
      resume,
      taskId,
      curTaskId,
    ],
  );

  const handleClear = React.useCallback(async () => {
    if (!taskId) {
      return;
    }

    const success = await deleteTaskById(taskId, projectId);
    if (success) {
      setMessages([]);
    }
  }, [projectId, taskId]);

  const handleScrollToTop = () => {
    messagesStartRef.current?.scrollIntoView({
      behavior: "smooth",
      block: "start",
    });
  };

  return (
    <div className={cn("flex h-full relative", className)}>
      {/* Conversation */}
      <Conversation
        taskId={taskId}
        messages={messages}
        messagesStartRef={messagesStartRef}
        messagesEndRef={messagesEndRef}
        processMessages={processMessages}
      />

      {/* input */}
      <div className="absolute bottom-0 z-10 left-0 right-0 h-30 px-2 bg-linear-to-t from-bg-000 from-50% via-bg-000/80 via-70% to-transparent pointer-events-none">
        <UserInput
          ref={userInputRef}
          isExecuting={isExecuting}
          onExecute={handleSend}
          placeholder={placeholder}
        >
          {/* <UserInput.TopLeft></UserInput.TopLeft> */}

          <UserInput.TopRight>
            <QuickTextDialog onCommandSelect={handleQuickCommand} />
            <Separator orientation="vertical" />
            <Button onClick={handleClear} disabled={isExecuting} variant="ghost" size="sm">
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
