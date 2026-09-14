"use client";

import { getPermissionStatus, type InteractionResponse } from "@agw/execution-core";

import * as React from "react";
import { useQuery } from "@agw/components/query";
import { toast } from "sonner";

import { apiGet } from "@agw/api";
import {
  buildConversationRenderModel,
  getCurrentTurnTodoItems,
  updateAutoScrollState,
  type AutoScrollState,
} from "@agw/chat-core";
import {
  DEFAULT_AGENT_MODE,
  getExecutionReconnectProgress,
  getAgentMode,
  getAgentflowCheckpointMessage,
  getMessageStreamingScopeId,
  getPendingInteraction,
  getTurnFinishedStatus,
  getLatestAgentMode,
  isUserTurnMessage,
  isModeControlMessage,
  type AgentMode,
  type AgentflowCheckpointAvailability,
  type ExecutionReconnectState,
  type PendingInteraction,
  type PermissionMode,
} from "../../../services/execution-hub";
import {
  clearProjectConversationRecords,
  getProjectConversationMessages,
  type LineComment,
  type ProjectDirectoryOption,
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
import { ToolDirectoriesContext } from "./tool-directory";

export interface ChatSessionSeed {
  revision: string | number;
  contextId: string | null;
  messages: AiMessage[];
  usage: TokenUsage;
  olderMessagesCursor: string | null;
  hasOlderMessages: boolean;
  agentMode: AgentMode | null;
}

export type ConversationChangeOptions = {
  cancelConversationLoad?: boolean;
};

export interface ChatProps {
  target: Pick<ChatTargetOption, "id" | "type"> | null;
  projectId: string | null;
  directoryId?: string | null;
  searchDirectoryIds?: readonly (string | null)[];
  directories?: readonly ProjectDirectoryOption[];
  conversationId: string | null;
  sessionSeed: ChatSessionSeed;
  isLoadingConversation?: boolean;
  environmentVariables?: Record<string, string>;
  placeholder?: string;
  className?: string;
  onConversationIdChange?: (conversationId: string | null) => void;
  onConversationAccepted?: (conversationId: string) => void;
  onContextIdChange?: (contextId: string | null) => void;
  onConversationChange?: (options?: ConversationChangeOptions) => void | Promise<void>;
  onExecutionError?: (error: unknown) => void;
  pendingFileComments?: readonly LineComment[];
  onPendingFileCommentsRemove?: (commentIds: readonly string[]) => void;
  showUserInputNavigation?: boolean;
  /** 将 SignalR 重连状态同步给更高层的工作区遮罩。 */
  onReconnectStateChange?: (state: ExecutionReconnectState | null) => void;
  /** 历史水合后查询服务端活动执行，并恢复 durable attachment。 */
  restoreExecution?: boolean;
  active?: boolean;
}

const EMPTY_FILE_COMMENTS: readonly LineComment[] = [];
const EMPTY_PROJECT_DIRECTORIES: readonly ProjectDirectoryOption[] = [];

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
  directoryId,
  searchDirectoryIds,
  directories = EMPTY_PROJECT_DIRECTORIES,
  conversationId,
  sessionSeed,
  isLoadingConversation = false,
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
  restoreExecution = false,
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
  const [activePermissionMode, setActivePermissionMode] = React.useState<PermissionMode | null>(
    null,
  );
  const [permissionChangePending, setPermissionChangePending] = React.useState(false);
  const permissionCapabilities = useQuery({
    queryKey: ["execution-permissions", executionServerId, target?.type, target?.id],
    enabled: Boolean(target),
    queryFn: () =>
      apiGet("/api/agents/permission-capabilities", {
        params: { query: { type: target!.type === "agent" ? 0 : 1, id: target!.id } },
      }),
  });
  const supportedPermissionModes = permissionCapabilities.data?.supportedPermissionModes ?? [];
  const permissionUnavailable = !supportedPermissionModes.includes(permissionMode)
    ? permissionCapabilities.isPending
      ? "Loading permissions…"
      : permissionCapabilities.error
        ? "Unable to load supported permissions. Retry before sending."
        : "Select a supported permission mode before sending."
    : undefined;
  const [agentMode, setAgentMode] = React.useState<AgentMode>(
    () => sessionSeed.agentMode ?? getLatestAgentMode(sessionSeed.messages),
  );
  const [hasOlderMessages, setHasOlderMessages] = React.useState(sessionSeed.hasOlderMessages);
  const [isLoadingOlderMessages, setIsLoadingOlderMessages] = React.useState(false);
  const [isJumpingToTop, setIsJumpingToTop] = React.useState(false);
  const [pendingInteraction, setPendingInteraction] = React.useState<PendingInteraction | null>(
    null,
  );
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
  const [restoredExecutionKey, setRestoredExecutionKey] = React.useState<string | null>(null);
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
        pendingInteraction,
        checkpointAvailability,
      }),
    [checkpointAvailability, messages, pendingInteraction],
  );
  const currentTurnTodos = React.useMemo(() => getCurrentTurnTodoItems(messages), [messages]);
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
  const isHydratingSession =
    isLoadingConversation ||
    hydratedSessionRevision !== sessionSeed.revision ||
    Boolean(conversationId && !contextId);
  const executionRestoreKey =
    restoreExecution && projectId && contextId
      ? JSON.stringify([executionServerId, projectId, contextId, sessionSeed.revision])
      : null;
  const isRestoringExecution =
    executionRestoreKey !== null && restoredExecutionKey !== executionRestoreKey;
  const showReconnect = getExecutionReconnectProgress(reconnectState) !== null;
  const checkpointResumeDisabled =
    isExecuting || isTransitioning || isHydratingSession || isRestoringExecution || showReconnect;

  React.useEffect(() => {
    if (!isExecuting || !onConversationChange) return;
    const timer = window.setInterval(() => {
      if (window.document.visibilityState === "visible") void onConversationChange();
    }, 5_000);
    return () => window.clearInterval(timer);
  }, [isExecuting, onConversationChange]);

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
    setPendingInteraction(null);
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
    setPendingInteraction(null);
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
    conversationIdRef.current = conversationId;
    announcedConversationIdRef.current = conversationId;
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
  }, [messages, pendingInteraction?.interactionId, syncConversationScrollPosition]);

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

      const permissionStatus = getPermissionStatus(message);
      if (permissionStatus) {
        setActivePermissionMode(permissionStatus.activePermissionMode);
        if (permissionStatus.nextPermissionMode)
          setPermissionMode(permissionStatus.nextPermissionMode);
        setPermissionChangePending(permissionStatus.permissionChangePending);
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

      const interaction = getPendingInteraction(message);
      if (interaction) {
        streamingMessageBatcherRef.current?.flush(generation);
        setPendingInteraction(
          interaction.kind === "user-input"
            ? {
                ...interaction,
                streamingScopeId:
                  interaction.streamingScopeId ?? activeStreamingScopeRef.current ?? undefined,
              }
            : interaction,
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
        const hadActiveTurn = activeStreamingScopeRef.current !== null;
        streamingMessageBatcherRef.current?.flush(generation);
        activeStreamingScopeRef.current = null;
        setIsExecuting(false);
        setPendingInteraction(null);
        if (hadActiveTurn) void onConversationChange?.();
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

      const prepared = prepareChatHistory(
        snapshot.messages.filter((message) => !getPermissionStatus(message)),
      );
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
        const permissionStatus = getPermissionStatus(message);
        if (permissionStatus) {
          setActivePermissionMode(permissionStatus.activePermissionMode);
          if (permissionStatus.nextPermissionMode)
            setPermissionMode(permissionStatus.nextPermissionMode);
          setPermissionChangePending(permissionStatus.permissionChangePending);
        }
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
        setPendingInteraction(null);
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
    targetKey,
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
              setPendingInteraction(null);
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
    if (!executionRestoreKey || !projectId || !contextId || isHydratingSession) return;
    let cancelled = false;
    const generation = executionGenerationRef.current;
    void ensureConfiguredClient(contextId, generation)
      .then((client) => {
        if (
          cancelled ||
          !client ||
          generation !== executionGenerationRef.current ||
          executionClientRef.current !== client
        )
          return;
        setIsExecuting(["running", "waiting-approval", "detached"].includes(client.getStatus()));
        setRestoredExecutionKey(executionRestoreKey);
      })
      .catch(() => {
        // configure 保留失败的 reconnect 状态；查询失败不能解除发送阻塞。
        if (!cancelled && generation === executionGenerationRef.current)
          setRestoredExecutionKey(executionRestoreKey);
      });
    return () => {
      cancelled = true;
    };
  }, [
    executionRestoreKey,
    projectId,
    contextId,
    isHydratingSession,
    ensureConfiguredClient,
    targetKey,
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
      if (isExecuting || isHydratingSession || isRestoringExecution || showReconnect) return;
      if (isTransitioning) {
        toast.error("Please wait for the previous execution to stop");
        return;
      }

      if (permissionUnavailable) {
        toast.error(permissionUnavailable);
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
      setPendingInteraction(null);
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
          const stillActive = ["running", "waiting-approval", "detached"].includes(
            executionClientRef.current?.getStatus() ?? "idle",
          );
          if (!stillActive) {
            activeStreamingScopeRef.current = null;
            setPendingInteraction(null);
          }
          setIsExecuting(stillActive);
          reportExecutionErrorOnce(error);
        }
      }
    },
    [
      ensureConfiguredClient,
      ensureConversationId,
      ensureContextId,
      isExecuting,
      isHydratingSession,
      isRestoringExecution,
      isTransitioning,
      notifyExecutionError,
      onConversationAccepted,
      onConversationChange,
      onPendingFileCommentsRemove,
      pendingFileComments,
      permissionUnavailable,
      projectId,
      showReconnect,
      target,
    ],
  );

  const handlePermissionModeChange = React.useCallback(
    (nextPermissionMode: PermissionMode) => {
      if (!supportedPermissionModes.includes(nextPermissionMode)) return;
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
    [
      ensureConfiguredClient,
      ensureContextId,
      notifyExecutionError,
      permissionMode,
      projectId,
      supportedPermissionModes,
    ],
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
      setPendingInteraction(null);
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
          const stillActive = ["running", "waiting-approval", "detached"].includes(
            executionClientRef.current?.getStatus() ?? "idle",
          );
          if (!stillActive) activeStreamingScopeRef.current = null;
          setIsExecuting(stillActive);
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

  const submitInteractionResponse = React.useCallback(
    (response: InteractionResponse) => {
      const client = executionClientRef.current;
      if (
        !pendingInteraction ||
        !client ||
        pendingInteraction.interactionId !== response.interactionId ||
        pendingInteraction.kind !== response.kind
      ) {
        return;
      }

      const generation = executionGenerationRef.current;
      const interactionId = pendingInteraction.interactionId;
      void client
        .submitHumanResponse({
          executionId: pendingInteraction.executionId,
          response,
        })
        .then(() => {
          if (
            generation !== executionGenerationRef.current ||
            executionClientRef.current !== client
          ) {
            return;
          }

          setPendingInteraction((current) =>
            current?.interactionId === interactionId ? null : current,
          );
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
    [notifyExecutionError, pendingInteraction],
  );

  const clearInFlightRef = React.useRef(false);
  const handleClear = React.useCallback(() => {
    if (clearInFlightRef.current) return;
    const conversationToClear = conversationId;
    const generation = executionGenerationRef.current;
    const clearLocalState = async () => {
      olderMessagesAbortRef.current?.abort();
      olderMessagesAbortRef.current = null;
      await interruptAndDispose("Conversation cleared.");
      setPendingInteraction(null);
      setCheckpointAvailability([]);
      messagesRef.current = [];
      setMessages([]);
      setClaudeCommands([]);
      setHasOlderMessages(false);
      setIsLoadingOlderMessages(false);
      setIsJumpingToTop(false);
      olderMessagesCursorRef.current = null;
      hasOlderMessagesRef.current = false;
      isLoadingOlderMessagesRef.current = false;
      userInputRef.current?.setInput("");
    };

    clearInFlightRef.current = true;
    void (async () => {
      try {
        if (projectId && conversationToClear) {
          const cleared = await clearProjectConversationRecords(projectId, conversationToClear);
          if (!cleared) throw new Error("Conversation not found.");
          const isStillCurrent =
            conversationIdRef.current === conversationToClear &&
            executionGenerationRef.current === generation;
          await onConversationChange?.(
            isStillCurrent ? { cancelConversationLoad: true } : undefined,
          );
          if (
            conversationIdRef.current === conversationToClear &&
            executionGenerationRef.current === generation
          ) {
            await clearLocalState();
          }
        } else {
          await clearLocalState();
          await onConversationChange?.();
        }
      } catch (error) {
        notifyExecutionError(error);
      } finally {
        clearInFlightRef.current = false;
      }
    })();
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
        inert={showReconnect}
        aria-hidden={showReconnect}
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
            <ToolDirectoriesContext.Provider value={directories}>
              <Conversation
                items={renderItems}
                scrollElementRef={conversationScrollRef}
                userInputNavigationHost={userInputNavigationHost}
                onUserInputNavigate={handleUserInputNavigate}
                hasOlderMessages={hasOlderMessages}
                isLoadingOlderMessages={isLoadingOlderMessages}
                isInitialLoading={isLoadingConversation}
                onLoadOlderMessages={() => void loadOlderMessages()}
                permissionMode={activePermissionMode ?? undefined}
                onHumanResponse={submitInteractionResponse}
                showCheckpointResume={target?.type === "agentflow"}
                checkpointResumeDisabled={checkpointResumeDisabled}
                onCheckpointResume={handleResumeCheckpoint}
              />
            </ToolDirectoriesContext.Provider>
          </div>

          {renderItems.length > 0 ? (
            <ChatAside usage={conversationUsage} todos={currentTurnTodos} />
          ) : null}
        </div>
      </div>

      <div
        inert={showReconnect}
        aria-hidden={showReconnect}
        className="pointer-events-none absolute inset-x-0 bottom-0 z-10 flex justify-center"
      >
        <div className="relative min-h-30 min-w-0 max-w-5xl flex-1 bg-linear-to-t from-background from-50% via-background/80 via-70% to-transparent px-6">
          {/* 输入框 */}
          <ChatInput
            isExecuting={isExecuting}
            isTransitioning={
              isTransitioning || isHydratingSession || isLoadingConversation || isRestoringExecution
            }
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
            directoryIds={searchDirectoryIds?.length ? searchDirectoryIds : [directoryId ?? null]}
            commandSource={commandSource}
            permissionMode={permissionMode}
            activePermissionMode={activePermissionMode}
            permissionChangePending={permissionChangePending && isExecuting}
            supportedPermissionModes={supportedPermissionModes}
            permissionReason={permissionCapabilities.data?.reason ?? undefined}
            permissionUnavailable={permissionUnavailable}
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
