"use client";

import * as React from "react";
import { Uuid4 } from "id128";
import { toast } from "sonner";

import { Button } from "@/components/ui/button";
import { Conversation } from "@/components/message/conversation";
import { cn } from "@/lib/utils";
import type { AiMessage, ProcessedMessageItem } from "@/types";
import type { HumanGateRequest, HumanGateResponse } from "@/api/execution-ws";
import {
  createUserTextMessage,
  executeWithWebSocketStream,
  mergeStreamingMessagesById,
  toExecutionWsUserInput,
} from "@/lib/execution-stream";
import { UserInput, UserInputRef } from "./user-input";
import { HumanGateApproval } from "./human-gate-approval";
import { ArrowUp, Eraser, Square } from "lucide-react";
import { Separator } from "../ui/separator";
import { clearProjectContextRecords } from "@/api/task-client";
import { QuickTextDialog } from "../task/quick-text-dialog";

export interface ChatProps {
  executionId: string | null | undefined;
  agentType: number;
  projectId: string;
  contextId?: string | null;
  resume?: boolean;
  resetSignal?: string | number | boolean;
  placeholder?: string;
  className?: string;
  onExecutionComplete?: () => void | Promise<void>;
  onExecutionError?: (error: unknown) => void;
}

function nextContextId(): string {
  return Uuid4.generate().toCanonical();
}

export function Chat({
  executionId, // agent executionId
  agentType, // agent type
  projectId,
  contextId,
  resume = false,
  resetSignal,
  placeholder = "Type your message...",
  className,
  onExecutionComplete,
  onExecutionError,
}: ChatProps) {
  const [isExecuting, setIsExecuting] = React.useState(false);
  const [messages, setMessages] = React.useState<AiMessage[]>([]);
  const [curContextId, setContextId] = React.useState<string>(
    contextId ?? nextContextId(),
  );
  const messagesEndRef = React.useRef<HTMLDivElement>(null!);
  const messagesStartRef = React.useRef<HTMLDivElement>(null!);
  const userInputRef = React.useRef<UserInputRef | null>(null);
  const humanGateResolverRef = React.useRef<((response: HumanGateResponse) => void) | null>(
    null,
  );
  const [pendingHumanGate, setPendingHumanGate] =
    React.useState<HumanGateRequest | null>(null);
  const shouldResume = resume || Boolean(contextId);

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

    return message.additionalProperties?.type === "turn-finished";
  }, []);

  React.useEffect(() => {
    setContextId(contextId ?? nextContextId());
    setMessages([]);
    setPendingHumanGate(null);
    humanGateResolverRef.current = null;
  }, [contextId, resetSignal]);

  React.useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages]);

  const handleQuickCommand = (text: string) => {
    userInputRef.current?.insertText(text);
  };

  const handleHumanGateRequest = React.useCallback((request: HumanGateRequest) => {
    setPendingHumanGate(request);
    return new Promise<HumanGateResponse>((resolve) => {
      humanGateResolverRef.current = resolve;
    });
  }, []);

  const submitHumanGateResponse = React.useCallback(
    (approved: boolean, responseText?: string) => {
      if (!pendingHumanGate || !humanGateResolverRef.current) {
        return;
      }

      humanGateResolverRef.current({
        requestId: pendingHumanGate.requestId,
        approved,
        responseText,
      });
      humanGateResolverRef.current = null;
      setPendingHumanGate(null);
      if (!approved) {
        setIsExecuting(false);
      }
    },
    [pendingHumanGate],
  );

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
      setPendingHumanGate(null);
      setIsExecuting(true);

      try {
        await executeWithWebSocketStream({
          id: executionId,
          request: {
            agentType,
            projectId,
            contextId: curContextId,
            resume: shouldResume,
            input: toExecutionWsUserInput(userMessage),
          },
          onMessage: (message) => {
            if (isTurnFinishedMessage(message)) {
              setIsExecuting(false);
            } else {
              setMessages((prev) => mergeStreamingMessagesById([...prev, message]));
            }
          },
          onHumanGateRequest: handleHumanGateRequest,
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
        setPendingHumanGate(null);
        humanGateResolverRef.current = null;
      }
    },
    [
      agentType,
      executionId,
      handleHumanGateRequest,
      isTurnFinishedMessage,
      onExecutionComplete,
      onExecutionError,
      projectId,
      shouldResume,
      curContextId,
    ],
  );

  const handleClear = React.useCallback(async () => {
    const contextToClear = contextId ?? curContextId;
    if (!contextToClear) {
      return;
    }

    const success = await clearProjectContextRecords(projectId, contextToClear);
    if (success) {
      setMessages([]);
    }
  }, [contextId, curContextId, projectId]);

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
        messages={messages}
        messagesStartRef={messagesStartRef}
        messagesEndRef={messagesEndRef}
        processMessages={processMessages}
      />

      {/* input */}
      <div className="absolute bottom-0 z-10 left-0 right-0 h-30 px-2 bg-linear-to-t from-bg-000 from-50% via-bg-000/80 via-70% to-transparent pointer-events-none">
        {pendingHumanGate ? (
          <div className="pointer-events-auto absolute bottom-[calc(100%+0.5rem)] left-2 right-2">
            <HumanGateApproval
              request={pendingHumanGate}
              onApprove={(responseText) => submitHumanGateResponse(true, responseText)}
              onReject={(responseText) => submitHumanGateResponse(false, responseText)}
            />
          </div>
        ) : null}
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
