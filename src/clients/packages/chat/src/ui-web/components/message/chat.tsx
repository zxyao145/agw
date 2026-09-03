"use client";

import * as React from "react";
import { useQuery } from "@agw/components/query";
import { toast } from "sonner";

import { apiGet } from "@agw/api";
import {
  buildConversationRenderModel,
  updateAutoScrollState,
  type AutoScrollState,
} from "@agw/chat-core";
import {
  DEFAULT_AGENT_MODE,
  getAgentMode,
  getAgentflowCheckpointMessage,
  hasPersistedDurableExecution,
  getMessageStreamingScopeId,
  getPendingHumanGate,
  getTurnFinishedStatus,
  getLatestAgentMode,
  isUserTurnMessage,
  isModeControlMessage,
  type AgentMode,
  type AgentflowCheckpointAvailability,
  type ExecutionReconnectState,
  type PendingHumanGate,
  type PermissionMode,
} from "../../../services/execution-hub";
import {
  clearProjectConversationRecords,
  getProjectConversationMessages,
  type LineComment,
} from "@agw/projects";
import { ChatAside } from "./chat-aside";
import { ChatInput } from "./chat-input";
import { Conversation } from "./conversation";
import type { UserInputRef } from "./user-input";
import {
  getAgentSuggestionQueryParams,
  toCommandSource,
  type AgentSuggestionsResponse,
} from "../../../lib/chat/agent-suggestions";
import { getClaudeInitCommands, prepareClaudeHistory } from "../../../lib/chat/ai-message-handlers";
import {
  createStreamingMessageBatcher,
  createUserMessage,
  mergeStreamingMessages,
  replaceStreamingScope,
  scopeMessagesByUserTurn,
  scopeStreamingMessage,
  toExecutionUserInput,
  type StreamingMessageBatcher,
} from "../../../services/execution-stream";
import { addTokenUsage, EMPTY_TOKEN_USAGE, getMessageTokenUsage, type TokenUsage } from "@agw/api";
import { createUuidV7 } from "@agw/api";
import { cn } from "@agw/components";
import { useExecutionPlatform } from "../../execution-platform";
import {
  executionSessionManager,
  type ManagedExecutionHandle,
} from "../../../services/execution-session-manager";
import type { AiMessage } from "@agw/api";
import type { ChatTargetOption } from "@agw/api";
import { buildFileCommentPrompt } from "../../../lib/chat/file-comment-prompt";
import type { ChatImageAttachment } from "../../../lib/chat/image-attachments";

export interface ChatSessionSeed {
  revision: string | number;
  contextId: string | null;
  messages: AiMessage[];
  usage: TokenUsage;
  olderMessagesCursor: string | null;
  hasOlderMessages: boolean;
  agentMode: AgentMode | null;
}

export interface ChatProps {
  target: Pick<ChatTargetOption, "id" | "type"> | null;
  projectId: string | null;
  conversationId: string | null;
  sessionSeed: ChatSessionSeed;
  environmentVariables?: Record<string, string>;
  placeholder?: string;
  className?: string;
  onConversationIdChange?: (conversationId: string | null) => void;
  onConversationAccepted?: (conversationId: string) => void;
  onContextIdChange?: (contextId: string | null) => void;
  onConversationChange?: () => void | Promise<void>;
  onExecutionError?: (error: unknown) => void;
  pendingFileComments?: readonly LineComment[];
  onPendingFileCommentsRemove?: (commentIds: readonly string[]) => void;
  showUserInputNavigation?: boolean;
  /** 将 SignalR 重连状态同步给更高层的工作区遮罩。 */
  onReconnectStateChange?: (state: ExecutionReconnectState | null) => void;
  /** 历史水合完成后，允许仅对已有 durable attachment 自动重订阅。 */
  restoreDurableExecution?: boolean;
  active?: boolean;
}

const EMPTY_FILE_COMMENTS: readonly LineComment[] = [];

function prepareChatHistory(messages: AiMessage[]) {
  const preparedHistory = prepareClaudeHistory(messages);
  return {
    ...preparedHistory,
    messages: scopeMessagesByUserTurn(preparedHistory.messages),
  };
}

function calculateConversationUsage(messages: AiMessage[]): TokenUsage {
  return messages.reduce((usage, message) => {
    const messageUsage = getMessageTokenUsage(message);
    return messageUsage ? addTokenUsage(usage, messageUsage) : usage;
  }, EMPTY_TOKEN_USAGE);
}

function truncateAtCheckpoint(
  messages: AiMessage[],
  occurrenceId: string,
  resumedMessages: AiMessage[],
): AiMessage[] {
  let boundaryIndex = -1;
  for (let index = 0; index < messages.length; index += 1) {
    if (getAgentflowCheckpointMessage(messages[index])?.occurrenceId === occurrenceId) {
      boundaryIndex = index;
    }
  }
  if (boundaryIndex < 0) return messages;

  return [...messages.slice(0, boundaryIndex + 1), ...resumedMessages];
}

function prependUniqueMessages(
  olderMessages: AiMessage[],
  currentMessages: AiMessage[],
): AiMessage[] {
  const currentMessageIds = new Set(
    currentMessages.map((message) => message.messageId).filter((messageId) => Boolean(messageId)),
  );
  return [
    ...olderMessages.filter(
      (message) => !message.messageId || !currentMessageIds.has(message.messageId),
    ),
    ...currentMessages,
  ];
}

/**
 * Shared chat container that owns session state, execution, message rendering, and input.
 * 共享聊天容器，拥有会话状态、执行、消息渲染和输入。
 * */
export function Chat({
  target,
  projectId,
  conversationId,
  sessionSeed,
  environmentVariables,
  placeholder = "Type your message...",
  className,
  onConversationIdChange,
  onConversationAccepted,
  onContextIdChange,
  onConversationChange,
  onExecutionError,
  pendingFileComments = EMPTY_FILE_COMMENTS,
  onPendingFileCommentsRemove,
  showUserInputNavigation = false,
  onReconnectStateChange,
  restoreDurableExecution = false,
}: ChatProps) {
  const executionServerId = useExecutionPlatform().serverId;
  const initialHistory = React.useMemo(
    () => prepareChatHistory(sessionSeed.messages),
    [sessionSeed.revision],
  );
  const [isExecuting, setIsExecuting] = React.useState(false);
  const [isTransitioning, setIsTransitioning] = React.useState(false);
  const [reconnectState, setReconnectState] = React.useState<ExecutionReconnectState | null>(null);
  const [messages, setMessages] = React.useState<AiMessage[]>(initialHistory.messages);
  const messagesRef = React.useRef<AiMessage[]>(initialHistory.messages);
  const [claudeCommands, setClaudeCommands] = React.useState<string[]>(initialHistory.commands);
  const [conversationUsage, setConversationUsage] = React.useState<TokenUsage>(sessionSeed.usage);
  const [contextId, setContextId] = React.useState<string | null>(sessionSeed.contextId);
  const [hydratedSessionRevision, setHydratedSessionRevision] = React.useState(
    sessionSeed.revision,
  );
  const [permissionMode, setPermissionMode] = React.useState<PermissionMode>("fullAccess");
  const [agentMode, setAgentMode] = React.useState<AgentMode>(
    () => sessionSeed.agentMode ?? getLatestAgentMode(sessionSeed.messages),
  );
  const [hasOlderMessages, setHasOlderMessages] = React.useState(sessionSeed.hasOlderMessages);
  const [isLoadingOlderMessages, setIsLoadingOlderMessages] = React.useState(false);
  const [isJumpingToTop, setIsJumpingToTop] = React.useState(false);
  const [pendingHumanGate, setPendingHumanGate] = React.useState<PendingHumanGate | null>(null);
  const [checkpointAvailability, setCheckpointAvailability] = React.useState<
    AgentflowCheckpointAvailability[]
  >([]);
  const contextIdRef = React.useRef<string | null>(sessionSeed.contextId);
  const announcedContextIdRef = React.useRef<string | null>(sessionSeed.contextId);
  const conversationIdRef = React.useRef<string | null>(conversationId);
  const announcedConversationIdRef = React.useRef<string | null>(conversationId);
  const conversationScrollRef = React.useRef<HTMLDivElement>(null);
  const conversationContentRef = React.useRef<HTMLDivElement>(null);
  const [userInputNavigationHost, setUserInputNavigationHost] =
    React.useState<HTMLDivElement | null>(null);
  const userInputRef = React.useRef<UserInputRef | null>(null);
  const executionClientRef = React.useRef<ManagedExecutionHandle | null>(null);
  const configuredSessionRef = React.useRef<string | null>(null);
  const durableRestoreAttemptRef = React.useRef<string | null>(null);
  const executionGenerationRef = React.useRef(0);
  const streamingMessageBatcherRef = React.useRef<StreamingMessageBatcher | null>(null);
  const checkpointResumeBufferRef = React.useRef<AiMessage[] | null>(null);
  const pendingTeardownCountRef = React.useRef(0);
  const activeStreamingScopeRef = React.useRef<string | null>(null);
  const olderMessagesAbortRef = React.useRef<AbortController | null>(null);
  const olderMessagesCursorRef = React.useRef<string | null>(sessionSeed.olderMessagesCursor);
  const hasOlderMessagesRef = React.useRef(sessionSeed.hasOlderMessages);
  const isLoadingOlderMessagesRef = React.useRef(false);
  const pendingPrependAnchorRef = React.useRef<{
    scrollHeight: number;
    scrollTop: number;
  } | null>(null);
  const confirmedAgentModeRef = React.useRef<AgentMode>(agentMode);
  const autoScrollStateRef = React.useRef<AutoScrollState>({
    shouldAutoScroll: true,
    scrollHeight: 0,
    scrollTop: 0,
  });
  const targetKey = target ? `${target.type}:${target.id}` : "";
  const previousTargetKeyRef = React.useRef(targetKey);

  if (streamingMessageBatcherRef.current === null) {
    streamingMessageBatcherRef.current = createStreamingMessageBatcher(
      (incomingMessages, generation) => {
        if (generation !== executionGenerationRef.current) {
          return;
        }

        if (checkpointResumeBufferRef.current) {
          checkpointResumeBufferRef.current = mergeStreamingMessages(
            checkpointResumeBufferRef.current,
            incomingMessages,
          );
          return;
        }

        setMessages((current) => {
          const nextMessages = mergeStreamingMessages(current, incomingMessages);
          messagesRef.current = nextMessages;
          return nextMessages;
        });
      },
    );
  }

  React.useEffect(() => {
    onReconnectStateChange?.(reconnectState);
  }, [onReconnectStateChange, reconnectState]);

  React.useEffect(() => {
    conversationIdRef.current = conversationId;
    announcedConversationIdRef.current = conversationId;
  }, [conversationId]);

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
  const renderItems = React.useMemo(
    () =>
      buildConversationRenderModel(messages, {
        collapseToolRuns: true,
        pendingHumanGate,
        checkpointAvailability,
      }),
    [checkpointAvailability, messages, pendingHumanGate],
  );
  const latestAvailableCheckpoint = React.useMemo(
    () =>
      checkpointAvailability
        .filter((checkpoint) => checkpoint.available)
        .reduce<AgentflowCheckpointAvailability | null>(
          (latest, checkpoint) =>
            !latest || checkpoint.boundarySequence > latest.boundarySequence ? checkpoint : latest,
          null,
        ),
    [checkpointAvailability],
  );
  const isHydratingSession = hydratedSessionRevision !== sessionSeed.revision;
  const checkpointResumeDisabled =
    isExecuting || isTransitioning || isHydratingSession || reconnectState !== null;

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
    streamingMessageBatcherRef.current?.flush(executionGenerationRef.current);
    executionGenerationRef.current += 1;
    activeStreamingScopeRef.current = null;
    const client = executionClientRef.current;
    executionClientRef.current = null;
    configuredSessionRef.current = null;
    setReconnectState(null);
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

  const detachExecution = React.useCallback((flushBufferedMessages = true) => {
    if (flushBufferedMessages) {
      streamingMessageBatcherRef.current?.flush(executionGenerationRef.current);
    } else {
      streamingMessageBatcherRef.current?.discard();
    }
    executionGenerationRef.current += 1;
    activeStreamingScopeRef.current = null;
    executionClientRef.current?.detach();
    executionClientRef.current = null;
    configuredSessionRef.current = null;
    setReconnectState(null);
    setIsExecuting(false);
    setIsTransitioning(false);
  }, []);

  React.useEffect(() => {
    if (previousTargetKeyRef.current === targetKey) {
      return;
    }

    previousTargetKeyRef.current = targetKey;
    detachExecution();
    setPendingHumanGate(null);
    setCheckpointAvailability([]);
    setClaudeCommands([]);
    confirmedAgentModeRef.current = DEFAULT_AGENT_MODE;
    setAgentMode(DEFAULT_AGENT_MODE);
  }, [detachExecution, targetKey]);

  React.useEffect(() => {
    const preparedHistory = prepareChatHistory(sessionSeed.messages);
    olderMessagesAbortRef.current?.abort();
    olderMessagesAbortRef.current = null;
    detachExecution();
    setPendingHumanGate(null);
    setCheckpointAvailability([]);
    autoScrollStateRef.current = {
      shouldAutoScroll: true,
      scrollHeight: 0,
      scrollTop: 0,
    };
    messagesRef.current = preparedHistory.messages;
    setMessages(preparedHistory.messages);
    setClaudeCommands(preparedHistory.commands);
    setConversationUsage(sessionSeed.usage);
    setContextId(sessionSeed.contextId);
    contextIdRef.current = sessionSeed.contextId;
    announcedContextIdRef.current = sessionSeed.contextId;
    setHasOlderMessages(sessionSeed.hasOlderMessages);
    setIsLoadingOlderMessages(false);
    setIsJumpingToTop(false);
    olderMessagesCursorRef.current = sessionSeed.olderMessagesCursor;
    hasOlderMessagesRef.current = sessionSeed.hasOlderMessages;
    isLoadingOlderMessagesRef.current = false;
    pendingPrependAnchorRef.current = null;
    const nextAgentMode = sessionSeed.agentMode ?? getLatestAgentMode(sessionSeed.messages);
    confirmedAgentModeRef.current = nextAgentMode;
    setAgentMode(nextAgentMode);
    userInputRef.current?.setInput("");
    setHydratedSessionRevision(sessionSeed.revision);
  }, [detachExecution, sessionSeed.revision]);

  React.useEffect(() => {
    return () => {
      olderMessagesAbortRef.current?.abort();
      detachExecution(false);
    };
  }, [detachExecution]);

  const syncConversationScrollPosition = React.useCallback(() => {
    const scrollContainer = conversationScrollRef.current;
    if (!scrollContainer) {
      return;
    }

    const prependAnchor = pendingPrependAnchorRef.current;
    if (prependAnchor) {
      pendingPrependAnchorRef.current = null;
      scrollContainer.scrollTop =
        prependAnchor.scrollTop + (scrollContainer.scrollHeight - prependAnchor.scrollHeight);
      autoScrollStateRef.current = {
        ...autoScrollStateRef.current,
        shouldAutoScroll: false,
        scrollHeight: scrollContainer.scrollHeight,
        scrollTop: scrollContainer.scrollTop,
      };
      return;
    }

    if (autoScrollStateRef.current.shouldAutoScroll) {
      scrollContainer.scrollTop = scrollContainer.scrollHeight;
    }

    autoScrollStateRef.current = {
      ...autoScrollStateRef.current,
      scrollHeight: scrollContainer.scrollHeight,
      scrollTop: scrollContainer.scrollTop,
    };
  }, []);

  React.useEffect(() => {
    syncConversationScrollPosition();
  }, [messages, pendingHumanGate?.requestId, syncConversationScrollPosition]);

  React.useEffect(() => {
    const conversationContent = conversationContentRef.current;
    if (!conversationContent || typeof ResizeObserver === "undefined") {
      return;
    }

    const resizeObserver = new ResizeObserver(syncConversationScrollPosition);
    resizeObserver.observe(conversationContent);
    return () => resizeObserver.disconnect();
  }, [syncConversationScrollPosition]);

  const refreshAgentflowCheckpoints = React.useCallback(
    async (client: ManagedExecutionHandle, generation: number) => {
      if (!target || target.type !== "agentflow") {
        setCheckpointAvailability([]);
        return;
      }

      const checkpoints = await client.listAgentflowCheckpoints(target.id);
      if (generation === executionGenerationRef.current && executionClientRef.current === client) {
        setCheckpointAvailability(checkpoints);
      }
    },
    [target],
  );

  const applyExecutionMessage = React.useCallback(
    (message: AiMessage, generation: number) => {
      if (generation !== executionGenerationRef.current) {
        return;
      }

      if (message.additionalProperties?.type === "mode-change-failed") {
        streamingMessageBatcherRef.current?.flush(generation);
        setAgentMode(confirmedAgentModeRef.current);
        const detail = message.contents.find(
          (content) => typeof content.content === "string",
        )?.content;
        toast.error(typeof detail === "string" ? detail : "Failed to change agent mode");
        return;
      }

      const nextAgentMode = getAgentMode(message);
      if (nextAgentMode) {
        streamingMessageBatcherRef.current?.flush(generation);
        confirmedAgentModeRef.current = nextAgentMode;
        setAgentMode(nextAgentMode);
        if (isModeControlMessage(message)) return;
      }

      const initCommands = getClaudeInitCommands(message);
      if (initCommands !== null) {
        streamingMessageBatcherRef.current?.flush(generation);
        setClaudeCommands(initCommands);
        return;
      }

      const messageUsage = getMessageTokenUsage(message);
      if (messageUsage) {
        setConversationUsage((current) => addTokenUsage(current, messageUsage));
      }

      if (getAgentflowCheckpointMessage(message)) {
        streamingMessageBatcherRef.current?.flush(generation);
        const scopedMessage = scopeStreamingMessage(
          message,
          getMessageStreamingScopeId(message) ??
            activeStreamingScopeRef.current ??
            message.messageId,
        );
        streamingMessageBatcherRef.current?.enqueue(scopedMessage, generation);
        const client = executionClientRef.current;
        if (client) {
          void refreshAgentflowCheckpoints(client, generation).catch(() => undefined);
        }
        return;
      }

      const humanGate = getPendingHumanGate(message);
      if (humanGate) {
        streamingMessageBatcherRef.current?.flush(generation);
        setPendingHumanGate(
          humanGate.requestType === "human-interaction"
            ? {
                ...humanGate,
                streamingScopeId:
                  humanGate.streamingScopeId ?? activeStreamingScopeRef.current ?? undefined,
              }
            : humanGate,
        );
        return;
      }

      if (message.additionalProperties?.type === "turn-start") {
        streamingMessageBatcherRef.current?.flush(generation);
        activeStreamingScopeRef.current =
          getMessageStreamingScopeId(message) ??
          activeStreamingScopeRef.current ??
          message.messageId;
        setIsExecuting(true);
        return;
      }

      const terminalStatus = getTurnFinishedStatus(message);
      if (terminalStatus) {
        streamingMessageBatcherRef.current?.flush(generation);
        activeStreamingScopeRef.current = null;
        setIsExecuting(false);
        setPendingHumanGate(null);
        void onConversationChange?.();
        const client = executionClientRef.current;
        if (client) {
          void refreshAgentflowCheckpoints(client, generation).catch(() => undefined);
        }
        if (terminalStatus === "failed") {
          notifyExecutionError(new Error("Execution failed"));
        }
        return;
      }

      if (!isUserTurnMessage(message)) {
        const scopedMessage = scopeStreamingMessage(
          message,
          getMessageStreamingScopeId(message) ??
            activeStreamingScopeRef.current ??
            message.messageId,
        );
        streamingMessageBatcherRef.current?.enqueue(scopedMessage, generation);
      }
    },
    [notifyExecutionError, onConversationChange, refreshAgentflowCheckpoints],
  );

  const restoreActiveTurnSnapshot = React.useCallback(
    (client: ManagedExecutionHandle, generation: number) => {
      if (generation !== executionGenerationRef.current) {
        return;
      }

      const snapshot = client.getActiveTurnSnapshot();
      if (!snapshot) {
        return;
      }

      const prepared = prepareChatHistory(snapshot.messages);
      const nextMessages = replaceStreamingScope(
        messagesRef.current,
        prepared.messages,
        snapshot.streamingScopeId,
      );
      messagesRef.current = nextMessages;
      setMessages(nextMessages);
      activeStreamingScopeRef.current = snapshot.streamingScopeId;

      let nextCommands: string[] | null = null;
      let nextMode: AgentMode | null = null;
      for (const message of snapshot.messages) {
        const initCommands = getClaudeInitCommands(message);
        if (initCommands !== null) {
          nextCommands = initCommands;
        }

        const messageMode = getAgentMode(message);
        if (messageMode) {
          nextMode = messageMode;
        }
      }
      if (nextCommands !== null) {
        setClaudeCommands(nextCommands);
      }
      if (nextMode) {
        confirmedAgentModeRef.current = nextMode;
        setAgentMode(nextMode);
      }
    },
    [],
  );

  React.useEffect(() => {
    if (isHydratingSession) {
      return;
    }
    if (!projectId || !contextId) {
      setReconnectState(null);
      return;
    }
    if (executionClientRef.current) return;
    const key = { serverId: executionServerId, projectId, contextId };
    if (!executionSessionManager.has(key)) {
      setReconnectState(null);
      return;
    }

    const generation = executionGenerationRef.current;
    let client: ManagedExecutionHandle;
    client = executionSessionManager.attach(key, {
      onMessage: (message) => applyExecutionMessage(message, generation),
      onClose: (error) => {
        if (
          generation !== executionGenerationRef.current ||
          executionClientRef.current !== client
        ) {
          return;
        }
        executionClientRef.current = null;
        configuredSessionRef.current = null;
        setReconnectState(null);
        setIsExecuting(false);
        setPendingHumanGate(null);
        if (error) notifyExecutionError(error);
      },
      onReconnecting: (state) => {
        if (
          generation === executionGenerationRef.current &&
          executionClientRef.current === client
        ) {
          setReconnectState(state);
        }
      },
      onReconnectFailed: (state) => {
        if (
          generation === executionGenerationRef.current &&
          executionClientRef.current === client
        ) {
          setReconnectState(state);
        }
      },
      onReconnected: () => {
        if (
          generation === executionGenerationRef.current &&
          executionClientRef.current === client
        ) {
          setReconnectState(null);
          setIsExecuting(["running", "waiting-approval", "detached"].includes(client.getStatus()));
          void refreshAgentflowCheckpoints(client, generation).catch(() => undefined);
        }
      },
    });
    executionClientRef.current = client;
    setReconnectState(client.getReconnectState());
    setIsExecuting(["running", "waiting-approval", "detached"].includes(client.getStatus()));
    restoreActiveTurnSnapshot(client, generation);

    return () => {
      if (executionClientRef.current === client) executionClientRef.current = null;
      client.detach();
    };
  }, [
    applyExecutionMessage,
    contextId,
    executionServerId,
    isHydratingSession,
    notifyExecutionError,
    projectId,
    refreshAgentflowCheckpoints,
    restoreActiveTurnSnapshot,
    sessionSeed.revision,
  ]);

  const ensureConfiguredClient = React.useCallback(
    async (
      nextContextId: string,
      generation: number,
      nextPermissionMode: PermissionMode = permissionMode,
    ): Promise<ManagedExecutionHandle | null> => {
      if (!projectId) {
        throw new Error("Please select a project");
      }

      const key = { serverId: executionServerId, projectId, contextId: nextContextId };
      let client = executionClientRef.current;
      if (client && !client.matchesKey(key)) {
        client.detach();
        if (executionClientRef.current === client) {
          executionClientRef.current = null;
        }
        configuredSessionRef.current = null;
        client = null;
      }
      if (!client) {
        let attachedClient!: ManagedExecutionHandle;
        attachedClient = executionSessionManager.attach(
          { serverId: executionServerId, projectId, contextId: nextContextId },
          {
            onMessage: (message) => applyExecutionMessage(message, generation),
            onClose: (error) => {
              if (
                generation !== executionGenerationRef.current ||
                executionClientRef.current !== attachedClient
              ) {
                return;
              }

              executionClientRef.current = null;
              configuredSessionRef.current = null;
              activeStreamingScopeRef.current = null;
              setReconnectState(null);
              setIsExecuting(false);
              setPendingHumanGate(null);
              if (error) notifyExecutionError(error);
            },
            onReconnecting: (state) => {
              if (
                generation === executionGenerationRef.current &&
                executionClientRef.current === attachedClient
              ) {
                setReconnectState(state);
              }
            },
            onReconnectFailed: (state) => {
              if (
                generation === executionGenerationRef.current &&
                executionClientRef.current === attachedClient
              ) {
                setReconnectState(state);
              }
            },
            onReconnected: () => {
              if (
                generation === executionGenerationRef.current &&
                executionClientRef.current === attachedClient
              ) {
                setReconnectState(null);
                setIsExecuting(
                  ["running", "waiting-approval", "detached"].includes(attachedClient.getStatus()),
                );
                void refreshAgentflowCheckpoints(attachedClient, generation).catch(() => undefined);
              }
            },
          },
        );
        client = attachedClient;
        executionClientRef.current = client;
        setReconnectState(client.getReconnectState());
        restoreActiveTurnSnapshot(client, generation);
      }

      const configurationKey = JSON.stringify({
        projectId,
        contextId: nextContextId,
        environmentVariables,
      });
      if (configuredSessionRef.current !== configurationKey) {
        await client.configure({
          projectId,
          contextId: nextContextId,
          environmentVariables,
          permissionMode: nextPermissionMode,
        });
        if (
          generation !== executionGenerationRef.current ||
          executionClientRef.current !== client
        ) {
          return null;
        }
        configuredSessionRef.current = configurationKey;
      }

      return generation === executionGenerationRef.current && executionClientRef.current === client
        ? client
        : null;
    },
    [
      applyExecutionMessage,
      environmentVariables,
      executionServerId,
      notifyExecutionError,
      permissionMode,
      projectId,
      refreshAgentflowCheckpoints,
      restoreActiveTurnSnapshot,
    ],
  );

  React.useEffect(() => {
    if (
      !projectId ||
      !contextId ||
      target?.type !== "agentflow" ||
      hydratedSessionRevision !== sessionSeed.revision
    ) {
      setCheckpointAvailability([]);
      return;
    }

    const generation = executionGenerationRef.current;
    void ensureConfiguredClient(contextId, generation)
      .then((client) =>
        client ? refreshAgentflowCheckpoints(client, generation) : Promise.resolve(),
      )
      .catch(() => {
        if (generation === executionGenerationRef.current) {
          setCheckpointAvailability([]);
        }
      });
  }, [
    contextId,
    ensureConfiguredClient,
    hydratedSessionRevision,
    projectId,
    refreshAgentflowCheckpoints,
    sessionSeed.revision,
    target?.type,
  ]);

  React.useEffect(() => {
    if (
      !restoreDurableExecution ||
      !projectId ||
      !contextId ||
      hydratedSessionRevision !== sessionSeed.revision ||
      executionClientRef.current ||
      !hasPersistedDurableExecution({ projectId, contextId })
    ) {
      return;
    }

    const restoreKey = JSON.stringify([
      executionServerId,
      projectId,
      contextId,
      sessionSeed.revision,
    ]);
    if (durableRestoreAttemptRef.current === restoreKey) {
      return;
    }
    durableRestoreAttemptRef.current = restoreKey;

    const generation = executionGenerationRef.current;
    void ensureConfiguredClient(contextId, generation)
      .then((client) => {
        if (
          !client ||
          generation !== executionGenerationRef.current ||
          executionClientRef.current !== client
        ) {
          return;
        }

        setIsExecuting(["running", "waiting-approval", "detached"].includes(client.getStatus()));
      })
      .catch(() => {
        // configure 已通过现有 reconnect 状态展示临时恢复失败和手动 Retry。
      });
  }, [
    contextId,
    ensureConfiguredClient,
    executionServerId,
    hydratedSessionRevision,
    projectId,
    restoreDurableExecution,
    sessionSeed.revision,
  ]);

  const ensureContextId = React.useCallback(
    (announce: boolean) => {
      const nextContextId = contextIdRef.current ?? createUuidV7();
      if (contextIdRef.current == null) {
        contextIdRef.current = nextContextId;
        setContextId(nextContextId);
      }
      if (announce && announcedContextIdRef.current !== nextContextId) {
        announcedContextIdRef.current = nextContextId;
        onContextIdChange?.(nextContextId);
      }
      return nextContextId;
    },
    [onContextIdChange],
  );

  const ensureConversationId = React.useCallback(() => {
    const nextConversationId = conversationIdRef.current ?? createUuidV7();
    if (conversationIdRef.current == null) {
      conversationIdRef.current = nextConversationId;
    }
    if (announcedConversationIdRef.current !== nextConversationId) {
      announcedConversationIdRef.current = nextConversationId;
      onConversationIdChange?.(nextConversationId);
    }
    return nextConversationId;
  }, [onConversationIdChange]);

  const handleExecute = React.useCallback(
    async (value: string, imageAttachments: readonly ChatImageAttachment[]) => {
      if (isHydratingSession || reconnectState) return;
      if (isTransitioning) {
        toast.error("Please wait for the previous execution to stop");
        return;
      }

      const submittedFileComments = [...pendingFileComments];
      const resolvedInput = buildFileCommentPrompt(value, submittedFileComments);
      if (!resolvedInput && imageAttachments.length === 0) {
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

      const nextConversationId = ensureConversationId();
      const nextId = ensureContextId(true);

      const userMessage = createUserMessage(resolvedInput, imageAttachments);
      const firstContent = userMessage.contents[0];
      if (firstContent) {
        firstContent.additionalProperties = {
          ...firstContent.additionalProperties,
          targetType: target.type,
          targetId: target.id,
        };
      }

      activeStreamingScopeRef.current = userMessage.messageId;
      const scopedUserMessage = scopeStreamingMessage(userMessage, userMessage.messageId);
      streamingMessageBatcherRef.current?.flush(executionGenerationRef.current);
      setMessages((current) => {
        const nextMessages = [...current, scopedUserMessage];
        messagesRef.current = nextMessages;
        return nextMessages;
      });
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
        const client = await ensureConfiguredClient(nextId, generation);
        if (!client) {
          activeStreamingScopeRef.current = null;
          setIsExecuting(false);
          return;
        }
        await client.execute({
          conversationId: nextConversationId,
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
        onConversationAccepted?.(nextConversationId);
        if (submittedFileComments.length > 0) {
          onPendingFileCommentsRemove?.(submittedFileComments.map((comment) => comment.id));
        }
        void onConversationChange?.();
      } catch (error) {
        if (generation === executionGenerationRef.current) {
          activeStreamingScopeRef.current = null;
          setIsExecuting(false);
          setPendingHumanGate(null);
          reportExecutionErrorOnce(error);
        }
      }
    },
    [
      ensureConfiguredClient,
      ensureConversationId,
      ensureContextId,
      isHydratingSession,
      isTransitioning,
      notifyExecutionError,
      onConversationAccepted,
      onConversationChange,
      onPendingFileCommentsRemove,
      pendingFileComments,
      projectId,
      reconnectState,
      target,
    ],
  );

  const handlePermissionModeChange = React.useCallback(
    (nextPermissionMode: PermissionMode) => {
      const previousPermissionMode = permissionMode;
      setPermissionMode(nextPermissionMode);
      if (!projectId) return;

      const nextContextId = ensureContextId(false);
      const generation = executionGenerationRef.current;
      setIsTransitioning(true);
      const currentClient = executionClientRef.current;
      const clientPromise = currentClient
        ? Promise.resolve(currentClient)
        : ensureConfiguredClient(nextContextId, generation, nextPermissionMode);
      void clientPromise
        .then(async (client) => {
          if (!client) return;
          await client.setPermissionMode(nextPermissionMode);
          if (
            generation === executionGenerationRef.current &&
            nextPermissionMode === "fullAccess"
          ) {
            setPendingHumanGate((current) =>
              current?.requestType === "tool-approval" ? null : current,
            );
          }
        })
        .catch((error) => {
          if (generation !== executionGenerationRef.current) return;
          setPermissionMode(previousPermissionMode);
          notifyExecutionError(error);
        })
        .finally(() => {
          if (generation === executionGenerationRef.current) setIsTransitioning(false);
        });
    },
    [ensureConfiguredClient, ensureContextId, notifyExecutionError, permissionMode, projectId],
  );

  const handleAgentModeChange = React.useCallback(
    (nextAgentMode: AgentMode) => {
      if (!projectId || !target || target.type !== "agent") {
        toast.error("Please select a mode-capable agent");
        return;
      }

      const previousAgentMode = agentMode;
      setAgentMode(nextAgentMode);
      const nextContextId = ensureContextId(false);
      const generation = executionGenerationRef.current;
      void ensureConfiguredClient(nextContextId, generation)
        .then((client) => client?.setMode(target.id, nextAgentMode))
        .catch((error) => {
          if (generation !== executionGenerationRef.current) return;
          setAgentMode(previousAgentMode);
          notifyExecutionError(error);
        });
    },
    [agentMode, ensureConfiguredClient, ensureContextId, notifyExecutionError, projectId, target],
  );

  const handleInterrupt = React.useCallback(() => {
    const client = executionClientRef.current;
    if (!client) {
      toast.error("No active session to interrupt");
      return;
    }

    const generation = executionGenerationRef.current;
    streamingMessageBatcherRef.current?.flush(generation);
    void client.interrupt("Stop requested by user.").catch((error) => {
      if (generation === executionGenerationRef.current && executionClientRef.current === client) {
        notifyExecutionError(error);
      }
    });
  }, [notifyExecutionError]);

  const handleResumeCheckpoint = React.useCallback(
    (occurrenceId?: string) => {
      if (!projectId || !contextId || target?.type !== "agentflow") {
        return;
      }
      if (checkpointResumeDisabled) {
        return;
      }

      const selectedCheckpoint = occurrenceId
        ? checkpointAvailability.find(
            (checkpoint) => checkpoint.occurrenceId === occurrenceId && checkpoint.available,
          )
        : latestAvailableCheckpoint;
      if (!selectedCheckpoint) {
        toast.error("No resumable checkpoint is available");
        return;
      }

      const generation = executionGenerationRef.current;
      const resumeExecutionId = globalThis.crypto.randomUUID();
      streamingMessageBatcherRef.current?.flush(generation);
      checkpointResumeBufferRef.current = [];
      activeStreamingScopeRef.current = null;
      setPendingHumanGate(null);
      setIsTransitioning(true);

      void ensureConfiguredClient(contextId, generation)
        .then(async (client) => {
          if (!client) {
            throw new Error("Execution session is no longer available");
          }
          await client.resumeCheckpoint({
            checkpointOccurrenceId: selectedCheckpoint.occurrenceId,
            agentflowId: target.id,
            resumeExecutionId,
          });
          if (
            generation !== executionGenerationRef.current ||
            executionClientRef.current !== client
          ) {
            return;
          }

          streamingMessageBatcherRef.current?.flush(generation);
          const resumedMessages = checkpointResumeBufferRef.current ?? [];
          checkpointResumeBufferRef.current = null;
          const retainedMessages = truncateAtCheckpoint(
            messagesRef.current,
            selectedCheckpoint.occurrenceId,
            resumedMessages,
          );
          messagesRef.current = retainedMessages;
          setMessages(retainedMessages);
          setConversationUsage(calculateConversationUsage(retainedMessages));
          setCheckpointAvailability((current) =>
            current.filter(
              (checkpoint) => checkpoint.boundarySequence <= selectedCheckpoint.boundarySequence,
            ),
          );
          setIsExecuting(true);
          void onConversationChange?.();
          void refreshAgentflowCheckpoints(client, generation).catch(() => undefined);
        })
        .catch((error) => {
          if (generation !== executionGenerationRef.current) return;
          checkpointResumeBufferRef.current = null;
          activeStreamingScopeRef.current = null;
          setIsExecuting(false);
          notifyExecutionError(error);
        })
        .finally(() => {
          if (generation === executionGenerationRef.current) {
            setIsTransitioning(false);
          }
        });
    },
    [
      checkpointAvailability,
      checkpointResumeDisabled,
      contextId,
      ensureConfiguredClient,
      latestAvailableCheckpoint,
      notifyExecutionError,
      onConversationChange,
      projectId,
      refreshAgentflowCheckpoints,
      target,
    ],
  );

  const submitHumanGateResponse = React.useCallback(
    (
      approved: boolean,
      responseText?: string,
      approvalScope: "once" | "always-tool" | "always-arguments" = "once",
      responseData?: unknown,
    ) => {
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
          approvalScope,
          responseData,
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
    const conversationToClear = conversationId;
    olderMessagesAbortRef.current?.abort();
    olderMessagesAbortRef.current = null;
    void interruptAndDispose("Conversation cleared.");
    setPendingHumanGate(null);
    setCheckpointAvailability([]);
    messagesRef.current = [];
    setMessages([]);
    setClaudeCommands([]);
    setConversationUsage(EMPTY_TOKEN_USAGE);
    setHasOlderMessages(false);
    setIsLoadingOlderMessages(false);
    setIsJumpingToTop(false);
    olderMessagesCursorRef.current = null;
    hasOlderMessagesRef.current = false;
    isLoadingOlderMessagesRef.current = false;
    userInputRef.current?.setInput("");

    if (projectId && conversationToClear) {
      void clearProjectConversationRecords(projectId, conversationToClear)
        .then(() => onConversationChange?.())
        .catch(notifyExecutionError);
    } else {
      void onConversationChange?.();
    }
  }, [conversationId, interruptAndDispose, notifyExecutionError, onConversationChange, projectId]);

  const handleClearPendingFileComments = React.useCallback(() => {
    if (pendingFileComments.length === 0) return;
    onPendingFileCommentsRemove?.(pendingFileComments.map((comment) => comment.id));
  }, [onPendingFileCommentsRemove, pendingFileComments]);

  const loadOlderMessages = React.useCallback(async () => {
    const activeProjectId = projectId;
    const activeConversationId = conversationIdRef.current;
    const activeContextId = contextIdRef.current;
    if (
      !activeProjectId ||
      !activeConversationId ||
      !activeContextId ||
      !hasOlderMessagesRef.current ||
      !olderMessagesCursorRef.current ||
      isLoadingOlderMessagesRef.current ||
      isJumpingToTop
    ) {
      return;
    }

    const abortController = new AbortController();
    olderMessagesAbortRef.current?.abort();
    olderMessagesAbortRef.current = abortController;
    isLoadingOlderMessagesRef.current = true;
    setIsLoadingOlderMessages(true);

    try {
      const page = await getProjectConversationMessages(activeProjectId, activeConversationId, {
        direction: "older",
        cursor: olderMessagesCursorRef.current,
        pageSize: 50,
        signal: abortController.signal,
      });
      if (
        abortController.signal.aborted ||
        conversationIdRef.current !== activeConversationId ||
        contextIdRef.current !== activeContextId
      ) {
        return;
      }

      if (page.items.length > 0) {
        const scrollContainer = conversationScrollRef.current;
        if (scrollContainer) {
          pendingPrependAnchorRef.current = {
            scrollHeight: scrollContainer.scrollHeight,
            scrollTop: scrollContainer.scrollTop,
          };
        }
        const pageCommands = prepareClaudeHistory(page.items).commands;
        setMessages((current) => {
          const prepared = prepareChatHistory(prependUniqueMessages(page.items, current));
          messagesRef.current = prepared.messages;
          return prepared.messages;
        });
        if (pageCommands.length > 0) {
          setClaudeCommands((current) => (current.length === 0 ? pageCommands : current));
        }
      } else {
        pendingPrependAnchorRef.current = null;
      }

      setHasOlderMessages(page.hasMore);
      olderMessagesCursorRef.current = page.nextCursor;
      hasOlderMessagesRef.current = page.hasMore;
    } catch (error) {
      pendingPrependAnchorRef.current = null;
      if (!abortController.signal.aborted) {
        notifyExecutionError(error);
      }
    } finally {
      if (olderMessagesAbortRef.current === abortController) {
        olderMessagesAbortRef.current = null;
        isLoadingOlderMessagesRef.current = false;
        setIsLoadingOlderMessages(false);
      }
    }
  }, [isJumpingToTop, notifyExecutionError, projectId]);

  React.useEffect(() => {
    const scrollContainer = conversationScrollRef.current;
    if (!scrollContainer || !hasOlderMessages || isLoadingOlderMessages) {
      return;
    }

    const frame = requestAnimationFrame(() => {
      if (scrollContainer.scrollHeight <= scrollContainer.clientHeight + 1) {
        void loadOlderMessages();
      }
    });
    return () => cancelAnimationFrame(frame);
  }, [hasOlderMessages, isLoadingOlderMessages, loadOlderMessages, renderItems.length]);

  const handleScrollToTop = React.useCallback(async () => {
    const scrollContainer = conversationScrollRef.current;
    if (!scrollContainer) return;

    autoScrollStateRef.current = {
      ...autoScrollStateRef.current,
      shouldAutoScroll: false,
    };

    if (!hasOlderMessagesRef.current || !olderMessagesCursorRef.current) {
      scrollContainer.scrollTo({ top: 0, behavior: "auto" });
      return;
    }

    const activeProjectId = projectId;
    const activeConversationId = conversationIdRef.current;
    const activeContextId = contextIdRef.current;
    if (
      !activeProjectId ||
      !activeConversationId ||
      !activeContextId ||
      isLoadingOlderMessagesRef.current
    ) {
      return;
    }

    const abortController = new AbortController();
    olderMessagesAbortRef.current?.abort();
    olderMessagesAbortRef.current = abortController;
    isLoadingOlderMessagesRef.current = true;
    setIsLoadingOlderMessages(true);
    setIsJumpingToTop(true);

    let cursor: string | null = olderMessagesCursorRef.current;
    let hasMore: boolean = hasOlderMessagesRef.current;
    const pages: AiMessage[][] = [];

    try {
      while (hasMore && cursor) {
        const page = await getProjectConversationMessages(activeProjectId, activeConversationId, {
          direction: "older",
          cursor,
          pageSize: 50,
          signal: abortController.signal,
        });
        if (
          abortController.signal.aborted ||
          conversationIdRef.current !== activeConversationId ||
          contextIdRef.current !== activeContextId
        ) {
          return;
        }

        pages.push(page.items);
        cursor = page.nextCursor;
        hasMore = page.hasMore && cursor !== null;
      }

      const olderMessages = pages.reverse().flat();
      if (olderMessages.length > 0) {
        const historyCommands = prepareClaudeHistory(olderMessages).commands;
        setMessages((current) => {
          const prepared = prepareChatHistory(prependUniqueMessages(olderMessages, current));
          messagesRef.current = prepared.messages;
          return prepared.messages;
        });
        if (historyCommands.length > 0) {
          setClaudeCommands((current) => (current.length === 0 ? historyCommands : current));
        }
      }

      olderMessagesCursorRef.current = cursor;
      hasOlderMessagesRef.current = hasMore;
      setHasOlderMessages(hasMore);

      requestAnimationFrame(() => {
        requestAnimationFrame(() => {
          if (
            conversationIdRef.current === activeConversationId &&
            contextIdRef.current === activeContextId
          ) {
            conversationScrollRef.current?.scrollTo({ top: 0, behavior: "auto" });
          }
        });
      });
    } catch (error) {
      if (!abortController.signal.aborted) {
        notifyExecutionError(error);
      }
    } finally {
      if (olderMessagesAbortRef.current === abortController) {
        olderMessagesAbortRef.current = null;
        isLoadingOlderMessagesRef.current = false;
        setIsLoadingOlderMessages(false);
        setIsJumpingToTop(false);
      }
    }
  }, [notifyExecutionError, projectId]);

  const handleScrollToBottom = React.useCallback(() => {
    const scrollContainer = conversationScrollRef.current;
    if (!scrollContainer) return;

    autoScrollStateRef.current = {
      ...autoScrollStateRef.current,
      shouldAutoScroll: true,
    };
    const scrollToLatestMessage = () => {
      const currentScrollContainer = conversationScrollRef.current;
      currentScrollContainer?.scrollTo({
        top: currentScrollContainer.scrollHeight,
        behavior: "auto",
      });
    };

    scrollToLatestMessage();
    requestAnimationFrame(() => {
      requestAnimationFrame(scrollToLatestMessage);
    });
  }, []);

  const handleConversationScroll = React.useCallback(
    (event: React.UIEvent<HTMLDivElement>) => {
      autoScrollStateRef.current = updateAutoScrollState(
        autoScrollStateRef.current,
        event.currentTarget,
      );
      if (event.currentTarget.scrollTop <= 320) {
        void loadOlderMessages();
      }
    },
    [loadOlderMessages],
  );

  const handleUserInputNavigate = React.useCallback(() => {
    autoScrollStateRef.current = {
      ...autoScrollStateRef.current,
      shouldAutoScroll: false,
    };
  }, []);

  return (
    <div className={cn("@container relative h-full min-h-0 w-full overflow-hidden", className)}>
      <div
        ref={conversationScrollRef}
        inert={reconnectState !== null}
        aria-hidden={reconnectState !== null}
        className="h-full w-full overflow-y-auto agw-scrollbar"
        onScroll={handleConversationScroll}
      >
        <div ref={conversationContentRef} className="mx-auto flex min-h-full w-full justify-center">
          {showUserInputNavigation ? (
            <div
              ref={setUserInputNavigationHost}
              className="pointer-events-none sticky top-0 z-20 hidden h-0 w-6 shrink-0 self-start @min-[56rem]:block"
            />
          ) : null}
          <div className="relative flex min-h-full min-w-0 max-w-5xl flex-1">
            {/* 对话列表 */}
            <Conversation
              items={renderItems}
              scrollElementRef={conversationScrollRef}
              userInputNavigationHost={userInputNavigationHost}
              onUserInputNavigate={handleUserInputNavigate}
              hasOlderMessages={hasOlderMessages}
              isLoadingOlderMessages={isLoadingOlderMessages}
              onLoadOlderMessages={() => void loadOlderMessages()}
              permissionMode={permissionMode}
              onHumanResponse={({ approved, responseText, approvalScope = "once", responseData }) =>
                submitHumanGateResponse(approved, responseText, approvalScope, responseData)
              }
              showCheckpointResume={target?.type === "agentflow"}
              checkpointResumeDisabled={checkpointResumeDisabled}
              onCheckpointResume={handleResumeCheckpoint}
            />
          </div>

          {renderItems.length > 0 ? <ChatAside usage={conversationUsage} /> : null}
        </div>
      </div>

      <div
        inert={reconnectState !== null}
        aria-hidden={reconnectState !== null}
        className="pointer-events-none absolute inset-x-0 bottom-0 z-10 flex justify-center"
      >
        <div className="relative min-h-30 min-w-0 max-w-5xl flex-1 bg-linear-to-t from-background from-50% via-background/80 via-70% to-transparent px-6">
          {/* 输入框 */}
          <ChatInput
            isExecuting={isExecuting}
            isTransitioning={isTransitioning || isHydratingSession}
            isLoadingHistory={isLoadingOlderMessages || isJumpingToTop}
            hasMessages={renderItems.length > 0}
            onExecute={(value, imageAttachments) => {
              void handleExecute(value, imageAttachments);
            }}
            onInterrupt={handleInterrupt}
            onClearSession={handleClear}
            onScrollToBottom={handleScrollToBottom}
            onScrollToTop={handleScrollToTop}
            showResume={target?.type === "agentflow"}
            canResume={!checkpointResumeDisabled && latestAvailableCheckpoint !== null}
            onResume={() => handleResumeCheckpoint()}
            projectId={projectId}
            commandSource={commandSource}
            permissionMode={permissionMode}
            agentMode={agentMode}
            onPermissionModeChange={handlePermissionModeChange}
            onAgentModeChange={handleAgentModeChange}
            pendingFileCommentCount={pendingFileComments.length}
            onClearPendingFileComments={handleClearPendingFileComments}
            placeholder={placeholder}
            userInputRef={userInputRef}
          />
        </div>
        {renderItems.length > 0 ? (
          <div className="hidden w-75 shrink-0 @min-[64rem]:block" aria-hidden="true" />
        ) : null}
      </div>
    </div>
  );
}
