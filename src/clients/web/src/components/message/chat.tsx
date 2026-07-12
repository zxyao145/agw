"use client";

import * as React from "react";
import { Uuid4 } from "id128";
import { toast } from "sonner";

import { Button } from "@/components/ui/button";
import { Conversation } from "@/components/message/conversation";
import { cn } from "@/lib/utils";
import type { AiMessage, ProcessedMessageItem } from "@/types";
import {
  ExecutionHubClient,
  getPendingHumanGate,
  getTurnFinishedStatus,
  type PendingHumanGate,
} from "@/api/execution-hub";
import {
  createUserTextMessage,
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
  targetId: string | null | undefined;
  agentType: number;
  projectId: string;
  contextId?: string | null;
  resume?: boolean;
  resetSignal?: string | number | boolean;
  placeholder?: string;
  className?: string;
  onExecutionComplete?: () => void | Promise<void>;
  onExecutionError?: (error: unknown) => void;
  active?: boolean;
}

function nextContextId(): string {
  return Uuid4.generate().toCanonical();
}

export function Chat({
  targetId,
  agentType, // agent type
  projectId,
  contextId,
  resetSignal,
  placeholder = "Type your message...",
  className,
  onExecutionComplete,
  onExecutionError,
  active = true,
}: ChatProps) {
  const [isExecuting, setIsExecuting] = React.useState(false);
  const [messages, setMessages] = React.useState<AiMessage[]>([]);
  const [curContextId, setContextId] = React.useState<string>(contextId ?? nextContextId());
  const messagesEndRef = React.useRef<HTMLDivElement>(null!);
  const messagesStartRef = React.useRef<HTMLDivElement>(null!);
  const userInputRef = React.useRef<UserInputRef | null>(null);
  const executionClientRef = React.useRef<ExecutionHubClient | null>(null);
  const configuredSessionRef = React.useRef<string | null>(null);
  const [pendingHumanGate, setPendingHumanGate] = React.useState<PendingHumanGate | null>(null);
  const processMessages = React.useCallback(
    (items: AiMessage[]): ProcessedMessageItem[] =>
      items.map((message) => ({ type: "normal", message })),
    [],
  );

  const interruptAndDispose = React.useCallback(async () => {
    const client = executionClientRef.current;
    executionClientRef.current = null;
    configuredSessionRef.current = null;
    if (!client) return;
    await client.interrupt("Execution drawer closed.").catch(() => undefined);
    await client.dispose();
  }, []);

  React.useEffect(() => {
    void interruptAndDispose();
    setContextId(contextId ?? nextContextId());
    setMessages([]);
    setPendingHumanGate(null);
  }, [contextId, interruptAndDispose, resetSignal]);

  React.useEffect(() => {
    if (!active) void interruptAndDispose();
    return () => {
      void interruptAndDispose();
    };
  }, [active, interruptAndDispose]);

  React.useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages]);

  const handleQuickCommand = (text: string) => {
    userInputRef.current?.insertText(text);
  };

  const submitHumanGateResponse = React.useCallback(
    async (approved: boolean, responseText?: string) => {
      const client = executionClientRef.current;
      if (!pendingHumanGate || !client) {
        return;
      }

      await client.submitHumanResponse({
        requestId: pendingHumanGate.requestId,
        approved,
        responseText,
      });
      setPendingHumanGate(null);
    },
    [pendingHumanGate],
  );

  const handleSend = React.useCallback(
    async (value: string) => {
      if (!targetId) {
        toast.error("Please select an execution target");
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
        let client = executionClientRef.current;
        const handlers = {
          onMessage: (message: AiMessage) => {
            const humanGate = getPendingHumanGate(message);
            if (humanGate) {
              setPendingHumanGate(humanGate);
              return;
            }
            if (message.additionalProperties?.type === "turn-start") {
              setIsExecuting(true);
              return;
            }
            const terminalStatus = getTurnFinishedStatus(message);
            if (terminalStatus) {
              setIsExecuting(false);
              setPendingHumanGate(null);
              if (terminalStatus === "failed") {
                const error = new Error("Execution failed");
                if (onExecutionError) onExecutionError(error);
                else toast.error(error.message);
              } else {
                void onExecutionComplete?.();
              }
              return;
            }
            if (message.role !== "user") {
              setMessages((prev) => mergeStreamingMessagesById([...prev, message]));
            }
          },
          onError: (error: Error) => {
            if (onExecutionError) onExecutionError(error);
            else toast.error(`Execute failed: ${error.message}`);
          },
          onClose: () => setIsExecuting(false),
        };
        if (!client) {
          client = new ExecutionHubClient(handlers);
          executionClientRef.current = client;
        } else {
          client.setHandlers(handlers);
        }
        const sessionKey = `${projectId}:${curContextId}`;
        if (configuredSessionRef.current !== sessionKey) {
          await client.configure({ projectId, contextId: curContextId });
          configuredSessionRef.current = sessionKey;
        }
        await client.execute({
          agentId: targetId,
          agentType,
          stream: true,
          input: toExecutionWsUserInput(userMessage),
        });
      } catch (error) {
        console.error("Execute failed:", error);
        if (onExecutionError) {
          onExecutionError(error);
        } else {
          toast.error(
            `Execute failed: ${error instanceof Error ? error.message : "Unknown error"}`,
          );
        }
        setIsExecuting(false);
        setPendingHumanGate(null);
      }
    },
    [agentType, targetId, onExecutionComplete, onExecutionError, projectId, curContextId],
  );

  const handleStop = React.useCallback(() => {
    const client = executionClientRef.current;
    if (client) {
      void client.interrupt("Stop requested by user.");
    }
  }, []);

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
          onStop={handleStop}
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
