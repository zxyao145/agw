"use client";

import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { Uuid4 } from "id128";
import { toast } from "sonner";

import { apiGet } from "@/api/client";
import {
  ExecutionHubClient,
  getPendingHumanGate,
  getTurnFinishedStatus,
  type PendingHumanGate,
} from "@/api/execution-hub";
import { clearProjectContextRecords } from "@/api/task-client";
import { ChatAside } from "@/components/message/chat-aside";
import { ChatInput } from "@/components/message/chat-input";
import { Conversation } from "@/components/message/conversation";
import { HumanGateApproval } from "@/components/message/human-gate-approval";
import type { UserInputRef } from "@/components/message/user-input";
import {
  getAgentSuggestionQueryParams,
  toCommandSource,
  type AgentSuggestionsResponse,
} from "@/lib/chat/agent-suggestions";
import { getClaudeInitCommands, prepareClaudeHistory } from "@/lib/chat/ai-message-handlers";
import {
  createUserTextMessage,
  mergeStreamingMessagesById,
  toExecutionUserInput,
} from "@/lib/execution-stream";
import {
  addTokenUsage,
  EMPTY_TOKEN_USAGE,
  getMessageTokenUsage,
  stripUsageContents,
  type TokenUsage,
} from "@/lib/token-usage";
import { cn } from "@/lib/utils";
import type { AiMessage } from "@/types";
import type { ChatTargetOption } from "@/types/chat-target";

export interface ChatSessionSeed {
  revision: string | number;
  contextId: string | null;
  messages: AiMessage[];
  usage: TokenUsage;
}

export interface ChatProps {
  target: Pick<ChatTargetOption, "id" | "type"> | null;
  projectId: string | null;
  sessionSeed: ChatSessionSeed;
  environmentVariables?: Record<string, string>;
  placeholder?: string;
  className?: string;
  onContextIdChange?: (contextId: string | null) => void;
  onConversationChange?: () => void | Promise<void>;
  onExecutionError?: (error: unknown) => void;
  active?: boolean;
}

function nextContextId(): string {
  return Uuid4.generate().toCanonical();
}

/**
 * Shared chat container that owns session state, execution, message rendering, and input.
 * 共享聊天容器，拥有会话状态、执行、消息渲染和输入。
 * */
export function Chat({
  target,
  projectId,
  sessionSeed,
  environmentVariables,
  placeholder = "Type your message...",
  className,
  onContextIdChange,
  onConversationChange,
  onExecutionError,
  active = true,
}: ChatProps) {
  const initialHistory = React.useMemo(
    () => prepareClaudeHistory(sessionSeed.messages),
    [sessionSeed.revision],
  );
  const [isExecuting, setIsExecuting] = React.useState(false);
  const [isTransitioning, setIsTransitioning] = React.useState(false);
  const [messages, setMessages] = React.useState<AiMessage[]>(initialHistory.messages);
  const [claudeCommands, setClaudeCommands] = React.useState<string[]>(initialHistory.commands);
  const [conversationUsage, setConversationUsage] = React.useState<TokenUsage>(sessionSeed.usage);
  const [contextId, setContextId] = React.useState<string | null>(sessionSeed.contextId);
  const [pendingHumanGate, setPendingHumanGate] = React.useState<PendingHumanGate | null>(null);
  const messagesEndRef = React.useRef<HTMLDivElement>(null!);
  const messagesStartRef = React.useRef<HTMLDivElement>(null!);
  const userInputRef = React.useRef<UserInputRef | null>(null);
  const executionClientRef = React.useRef<ExecutionHubClient | null>(null);
  const configuredSessionRef = React.useRef<string | null>(null);
  const executionGenerationRef = React.useRef(0);
  const pendingTeardownCountRef = React.useRef(0);
  const targetKey = target ? `${target.type}:${target.id}` : "";
  const previousTargetKeyRef = React.useRef(targetKey);

  const suggestionQueryParams = React.useMemo(
    () => getAgentSuggestionQueryParams(projectId, target),
    [projectId, target],
  );
  const agentSuggestionsQuery = useQuery({
    queryKey: [
      "agentSuggestions",
      suggestionQueryParams?.projectId,
      suggestionQueryParams?.agentId,
    ],
    queryFn: async () => {
      if (!suggestionQueryParams) {
        throw new Error("Agent suggestion query requires an agent.");
      }

      return (await apiGet("/api/agents/suggestions", {
        params: { query: suggestionQueryParams },
      })) as AgentSuggestionsResponse;
    },
    enabled: suggestionQueryParams !== null,
    retry: false,
  });
  const commandSource = React.useMemo(
    () => toCommandSource(agentSuggestionsQuery.data, claudeCommands),
    [agentSuggestionsQuery.data, claudeCommands],
  );
  const visibleMessages = React.useMemo(() => stripUsageContents(messages), [messages]);

  const notifyExecutionError = React.useCallback(
    (error: unknown) => {
      if (onExecutionError) {
        onExecutionError(error);
        return;
      }

      toast.error(`Execute failed: ${error instanceof Error ? error.message : "Unknown error"}`);
    },
    [onExecutionError],
  );

  const interruptAndDispose = React.useCallback(async (reason: string) => {
    executionGenerationRef.current += 1;
    const client = executionClientRef.current;
    executionClientRef.current = null;
    configuredSessionRef.current = null;
    pendingTeardownCountRef.current += 1;
    setIsTransitioning(true);

    try {
      if (client) {
        await client.interruptAndWait(reason).catch(() => undefined);
        await client.dispose().catch(() => undefined);
      }
    } finally {
      pendingTeardownCountRef.current -= 1;
      if (pendingTeardownCountRef.current === 0) {
        setIsTransitioning(false);
        setIsExecuting(false);
      }
    }
  }, []);

  React.useEffect(() => {
    if (previousTargetKeyRef.current === targetKey) {
      return;
    }

    previousTargetKeyRef.current = targetKey;
    void interruptAndDispose("Execution target changed.");
    setPendingHumanGate(null);
    setClaudeCommands([]);
  }, [interruptAndDispose, targetKey]);

  React.useEffect(() => {
    const preparedHistory = prepareClaudeHistory(sessionSeed.messages);
    void interruptAndDispose("Execution session changed.");
    setPendingHumanGate(null);
    setMessages(preparedHistory.messages);
    setClaudeCommands(preparedHistory.commands);
    setConversationUsage(sessionSeed.usage);
    setContextId(sessionSeed.contextId);
    userInputRef.current?.setInput("");
  }, [interruptAndDispose, sessionSeed.revision]);

  React.useEffect(() => {
    if (!active) {
      void interruptAndDispose("Execution drawer closed.");
    }

    return () => {
      void interruptAndDispose("Chat unmounted.");
    };
  }, [active, interruptAndDispose]);

  React.useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages]);

  const applyExecutionMessage = React.useCallback(
    (message: AiMessage, generation: number) => {
      if (generation !== executionGenerationRef.current) {
        return;
      }

      const initCommands = getClaudeInitCommands(message);
      if (initCommands !== null) {
        setClaudeCommands(initCommands);
        return;
      }

      const messageUsage = getMessageTokenUsage(message);
      if (messageUsage) {
        setConversationUsage((current) => addTokenUsage(current, messageUsage));
      }

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
        void onConversationChange?.();
        if (terminalStatus === "failed") {
          notifyExecutionError(new Error("Execution failed"));
        }
        return;
      }

      if (message.role !== "user") {
        setMessages((current) => mergeStreamingMessagesById([...current, message]));
      }
    },
    [notifyExecutionError, onConversationChange],
  );

  const handleExecute = React.useCallback(
    async (value: string) => {
      if (isTransitioning) {
        toast.error("Please wait for the previous execution to stop");
        return;
      }

      const trimmedValue = value.trim();
      if (!trimmedValue) {
        toast.error("Please enter a prompt");
        return;
      }
      if (!projectId) {
        toast.error("Please select a project");
        return;
      }
      if (!target) {
        toast.error("Please select an execution target");
        return;
      }

      const nextId = contextId ?? nextContextId();
      if (!contextId) {
        setContextId(nextId);
        onContextIdChange?.(nextId);
      }

      const userMessage = createUserTextMessage(trimmedValue);
      const firstContent = userMessage.contents[0];
      if (firstContent) {
        firstContent.additionalProperties = {
          ...firstContent.additionalProperties,
          targetType: target.type,
          targetId: target.id,
        };
      }

      setMessages((current) => [...current, userMessage]);
      setPendingHumanGate(null);
      setIsExecuting(true);
      const generation = executionGenerationRef.current;
      let didReportExecutionError = false;
      const reportExecutionErrorOnce = (error: unknown) => {
        if (didReportExecutionError) {
          return;
        }

        didReportExecutionError = true;
        notifyExecutionError(error);
      };

      try {
        let client = executionClientRef.current;
        const handlers = {
          onMessage: (message: AiMessage) => applyExecutionMessage(message, generation),
          onClose: (error?: Error) => {
            if (generation !== executionGenerationRef.current) {
              return;
            }

            configuredSessionRef.current = null;
            if (executionClientRef.current === client) {
              executionClientRef.current = null;
            }
            setIsExecuting(false);
            setPendingHumanGate(null);
            if (error) {
              reportExecutionErrorOnce(error);
            }
          },
        };
        if (!client) {
          client = new ExecutionHubClient(handlers);
          executionClientRef.current = client;
        } else {
          client.setHandlers(handlers);
        }

        const configurationKey = JSON.stringify({
          projectId,
          contextId: nextId,
          environmentVariables,
        });
        if (configuredSessionRef.current !== configurationKey) {
          await client.configure({
            projectId,
            contextId: nextId,
            environmentVariables,
          });
          if (
            generation !== executionGenerationRef.current ||
            executionClientRef.current !== client
          ) {
            return;
          }
          configuredSessionRef.current = configurationKey;
        }

        if (
          generation !== executionGenerationRef.current ||
          executionClientRef.current !== client
        ) {
          return;
        }
        await client.execute({
          agentId: target.id,
          agentType: target.type === "agent" ? 0 : 1,
          stream: true,
          input: toExecutionUserInput(userMessage),
        });
        if (
          generation !== executionGenerationRef.current ||
          executionClientRef.current !== client
        ) {
          return;
        }
        void onConversationChange?.();
      } catch (error) {
        if (generation === executionGenerationRef.current) {
          setIsExecuting(false);
          setPendingHumanGate(null);
          reportExecutionErrorOnce(error);
        }
      }
    },
    [
      applyExecutionMessage,
      contextId,
      environmentVariables,
      isTransitioning,
      notifyExecutionError,
      onContextIdChange,
      onConversationChange,
      projectId,
      target,
    ],
  );

  const handleInterrupt = React.useCallback(() => {
    const client = executionClientRef.current;
    if (!client) {
      toast.error("No active session to interrupt");
      return;
    }

    const generation = executionGenerationRef.current;
    void client.interrupt("Stop requested by user.").catch((error) => {
      if (generation === executionGenerationRef.current && executionClientRef.current === client) {
        notifyExecutionError(error);
      }
    });
  }, [notifyExecutionError]);

  const submitHumanGateResponse = React.useCallback(
    (approved: boolean, responseText?: string) => {
      const client = executionClientRef.current;
      if (!pendingHumanGate || !client) {
        toast.error("No active HumanGate request");
        return;
      }

      const generation = executionGenerationRef.current;
      const requestId = pendingHumanGate.requestId;
      void client
        .submitHumanResponse({
          requestId,
          approved,
          responseText,
        })
        .then(() => {
          if (
            generation !== executionGenerationRef.current ||
            executionClientRef.current !== client
          ) {
            return;
          }

          setPendingHumanGate((current) => (current?.requestId === requestId ? null : current));
        })
        .catch((error) => {
          if (
            generation === executionGenerationRef.current &&
            executionClientRef.current === client
          ) {
            notifyExecutionError(error);
          }
        });
    },
    [notifyExecutionError, pendingHumanGate],
  );

  const handleClear = React.useCallback(() => {
    const contextToClear = contextId;
    void interruptAndDispose("Conversation cleared.");
    setPendingHumanGate(null);
    setMessages([]);
    setClaudeCommands([]);
    setConversationUsage(EMPTY_TOKEN_USAGE);
    setContextId(null);
    userInputRef.current?.setInput("");
    onContextIdChange?.(null);

    if (projectId && contextToClear) {
      void clearProjectContextRecords(projectId, contextToClear)
        .then(() => onConversationChange?.())
        .catch(notifyExecutionError);
    } else {
      void onConversationChange?.();
    }
  }, [
    contextId,
    interruptAndDispose,
    notifyExecutionError,
    onContextIdChange,
    onConversationChange,
    projectId,
  ]);

  const handleScrollToTop = React.useCallback(() => {
    messagesStartRef.current?.scrollIntoView({
      behavior: "smooth",
      block: "start",
    });
  }, []);

  return (
    <div className={cn("@container relative h-full min-h-0 w-full overflow-hidden", className)}>
      <div className="h-full w-full overflow-y-auto">
        <div className="mx-auto flex min-h-full w-full justify-center">
          <div className="relative flex min-h-full min-w-0 max-w-5xl flex-1">
            {/* 对话列表 */}
            <Conversation
              messages={visibleMessages}
              messagesStartRef={messagesStartRef}
              messagesEndRef={messagesEndRef}
              scrollable={false}
            />
          </div>

          {visibleMessages.length > 0 ? <ChatAside usage={conversationUsage} /> : null}
        </div>
      </div>

      <div className="pointer-events-none absolute inset-x-0 bottom-0 z-10 flex justify-center">
        <div className="relative h-30 min-w-0 max-w-5xl flex-1 bg-linear-to-t from-bg-000 from-50% via-bg-000/80 via-70% to-transparent px-6">
          {/* 用户确认 */}
          {pendingHumanGate ? (
            <div className="pointer-events-auto absolute bottom-[calc(100%+0.5rem)] left-2 right-2">
              <HumanGateApproval
                request={pendingHumanGate}
                onApprove={(responseText) => submitHumanGateResponse(true, responseText)}
                onReject={(responseText) => submitHumanGateResponse(false, responseText)}
              />
            </div>
          ) : null}
          {/* 输入框 */}
          <ChatInput
            isExecuting={isExecuting}
            isTransitioning={isTransitioning}
            hasMessages={visibleMessages.length > 0}
            onExecute={(value) => {
              void handleExecute(value);
            }}
            onInterrupt={handleInterrupt}
            onClearSession={handleClear}
            onScrollToTop={handleScrollToTop}
            projectId={projectId}
            commandSource={commandSource}
            placeholder={placeholder}
            userInputRef={userInputRef}
          />
        </div>
        {visibleMessages.length > 0 ? (
          <div className="hidden w-75 shrink-0 @min-[64rem]:block" aria-hidden="true" />
        ) : null}
      </div>
    </div>
  );
}
