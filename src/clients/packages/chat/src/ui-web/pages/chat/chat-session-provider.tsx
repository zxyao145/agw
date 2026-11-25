"use client";

import * as React from "react";
import { ApiError, getApiErrorMessage } from "@agw/api";
import {
  chatHttpClient,
  executionSessionManager,
  migrateDurableExecutionAttachment,
  type ChatConversationScope,
  type ExecutionReconnectState,
} from "@agw/chat-runtime";
import {
  conversationComposerStorage,
  type ComposerDraftKey,
  type ConversationComposerScope,
} from "../../../lib/chat/conversation-composer-storage";
import { conversationImageDrafts } from "../../../lib/chat/conversation-image-drafts";
import type { AgentSelection } from "../../components/agent-selector";
import type { ChatSessionSeed, ConversationChangeOptions } from "../../components/message/chat";
import type { ChatComposerHandle } from "../../components/message/chat-extension-types";
import { notifyImageDraftError } from "../../components/message/use-conversation-image-draft";
import { useExecutionPlatform } from "../../execution-platform";
import { useChatProject, type ChatProjectContextValue } from "./chat-project-provider";
import { getTargetValue, getTargetValueFromMetadata } from "./lib/target-options";
import { chatSettingsStorage } from "./settings-storage";
import type { ChatTargetOption } from "./types";

export type ChatSessionProviderProps = {
  conversationId: string | null;
  draftId: string;
  newConversationId: string | null;
  draftSubmitted: boolean;
  visible: boolean;
  onConversationAccepted(conversationId: string): void;
  onDraftLost?(): void;
  onTitleChange?(title: string): void;
  onSubmitted?(): void;
  onSelectionChange?(selection: {
    conversationId: string | null;
    target: ChatTargetOption | null;
  }): void;
  children: React.ReactNode;
};

type Details = Awaited<ReturnType<typeof chatHttpClient.getConversation>>;
type LoadedHistory = Awaited<ReturnType<typeof readSessionHistory>>;
type SessionProps = ChatSessionProviderProps & { initialHistory?: LoadedHistory };
type Callbacks = React.RefObject<ChatSessionProviderProps>;
type Recovery = { id: string; keepLive: boolean; details?: Details };
const DEFAULT_AGENT_LABEL = "Hello";
const noRetry = async () => {};
const emptySeed: ChatSessionSeed = {
  revision: 0,
  conversationId: null,
  messages: [],
  historyTurns: [],
  olderMessagesCursor: null,
  hasOlderMessages: false,
  agentMode: null,
};

export async function readSessionHistory(
  scope: ChatConversationScope,
  signal: AbortSignal,
  existingDetails?: Details,
) {
  const [details, page] = await Promise.all([
    existingDetails ?? chatHttpClient.getConversation(scope, signal),
    chatHttpClient.getConversationMessages(scope, { direction: "older", pageSize: 50, signal }),
  ]);
  signal.throwIfAborted();
  migrateDurableExecutionAttachment({
    projectId: scope.projectId,
    conversationId: scope.conversationId,
    contextId: details.contextId,
  });
  const mode = details.resumeState?.agentMode;
  const seed: ChatSessionSeed = {
    revision: 1,
    conversationId: scope.conversationId,
    messages: page.items,
    historyTurns: page.turns,
    olderMessagesCursor: page.nextCursor,
    hasOlderMessages: page.hasMore,
    agentMode: mode === "plan" || mode === "execute" ? mode : null,
  };
  return {
    seed,
    title: details.title,
    target: getTargetValueFromMetadata(
      details.resumeState?.targetType,
      details.resumeState?.targetId,
    ),
  };
}

function selectedOption(
  scope: ConversationComposerScope | null,
  key: ComposerDraftKey,
  options: ChatTargetOption[],
  selected: string | null,
) {
  if (!scope || !options.length) return null;
  const candidates = [
    conversationComposerStorage.get(scope, key).targetValue,
    selected,
    chatSettingsStorage.get(scope.projectId).targetValue,
  ];
  for (const value of candidates) {
    const found = options.find((option) => getTargetValue(option) === value);
    if (found) return found;
  }
  return (
    options.find((option) => option.type === "agent" && option.label === DEFAULT_AGENT_LABEL) ??
    options[0]
  );
}

function useTarget(
  scope: ConversationComposerScope | null,
  key: ComposerDraftKey,
  options: ChatTargetOption[],
  restored: string | null,
) {
  const [selection, setSelection] = React.useState<string | null>(null);
  const selectedTarget = React.useMemo(
    () => selectedOption(scope, key, options, selection ?? restored),
    [scope, key, options, selection, restored],
  );
  const selectAgent = React.useCallback(
    ({ agentType, agentId }: AgentSelection) => {
      if (!scope) return;
      const value = getTargetValue({ id: agentId, type: agentType === 0 ? "agent" : "agentflow" });
      chatSettingsStorage.set(scope.projectId, { targetValue: value });
      conversationComposerStorage.set(scope, key, { targetValue: value });
      setSelection(value);
    },
    [scope, key],
  );
  return { selectedTarget, selectAgent };
}

function useDraftAcceptance(scope: ConversationComposerScope | null, callbacks: Callbacks) {
  const { draftId } = callbacks.current;
  const accepted = React.useRef<string | null>(null);
  const [recovered, setRecovered] = React.useState<Recovery | null>(null);
  // 挂载时已提交的草稿需要补读历史。An already submitted draft needs history when it mounts again.
  const needsHistory = React.useRef(callbacks.current.draftSubmitted);
  const accept = React.useCallback(
    (id: string, details?: Details) => {
      if (!scope || accepted.current === id || callbacks.current.conversationId) return;
      const from = { draftId },
        to = { conversationId: id };
      conversationComposerStorage.move(scope, from, to);
      void conversationImageDrafts.accept(scope, from, to).catch(notifyImageDraftError);
      accepted.current = id;
      if (needsHistory.current || details) {
        setRecovered({ id, details, keepLive: executionSessionManager.has({ ...scope, ...to }) });
      }
      callbacks.current.onConversationAccepted(id);
    },
    [scope, callbacks, draftId],
  );
  const markLost = React.useCallback(() => {
    needsHistory.current = false;
    callbacks.current.onDraftLost?.();
  }, [callbacks]);
  return { accepted, recovered, accept, needsHistory: needsHistory.current, markLost };
}

function useDraftRecovery(
  scope: ConversationComposerScope | null,
  callbacks: Callbacks,
  acceptance: ReturnType<typeof useDraftAcceptance>,
  known: boolean,
  attempt: number,
) {
  const { conversationId, draftId, draftSubmitted } = callbacks.current;
  const { accepted, accept, markLost } = acceptance;
  const [errorMessage, setErrorMessage] = React.useState<string | null>(null);
  React.useEffect(() => {
    if (!scope || conversationId || !draftSubmitted || accepted.current === draftId) return;
    const key = { ...scope, conversationId: draftId };
    setErrorMessage(null);
    if (known) {
      accept(draftId);
      return;
    }
    if (executionSessionManager.has(key)) return;
    const controller = new AbortController();
    void chatHttpClient.getConversation(key, controller.signal).then(
      (details) => {
        if (!controller.signal.aborted) accept(draftId, details);
      },
      (error: unknown) => {
        if (controller.signal.aborted) return;
        if (error instanceof ApiError && error.status === 404) {
          markLost();
          return;
        }
        console.error("Failed to recover the submitted conversation.", error);
        setErrorMessage(getApiErrorMessage(error));
      },
    );
    return () => controller.abort();
  }, [scope, conversationId, draftId, draftSubmitted, accepted, accept, known, markLost, attempt]);
  return errorMessage;
}

function useHistory(
  key: ChatConversationScope | null,
  recovered: Recovery | null,
  props: SessionProps,
  callbacks: Callbacks,
  attempt: number,
) {
  const [history, setHistory] = React.useState(props.initialHistory);
  const [errorMessage, setErrorMessage] = React.useState<string | null>(null);
  const controller = React.useRef<AbortController | null>(null);
  React.useEffect(() => {
    if (!key || props.initialHistory?.seed.conversationId === key.conversationId) return;
    const request = new AbortController();
    controller.current = request;
    setErrorMessage(null);
    void readSessionHistory(key, request.signal, recovered?.details).then(
      (loaded) => {
        if (request.signal.aborted) return;
        setHistory((last) => ({
          ...loaded,
          seed: { ...loaded.seed, revision: Number(last?.seed.revision ?? 0) + 1 },
        }));
        if (loaded.title != null) callbacks.current.onTitleChange?.(loaded.title);
      },
      (error: unknown) => {
        if (request.signal.aborted) return;
        console.error("Failed to load conversation history.", error);
        setErrorMessage(getApiErrorMessage(error));
      },
    );
    return () => request.abort();
  }, [key, recovered, props.initialHistory, callbacks, attempt]);
  return { history, historyError: errorMessage, controller };
}

function useSessionHistory(
  scope: ConversationComposerScope | null,
  callbacks: Callbacks,
  props: SessionProps,
  known: boolean,
) {
  const [attempt, retry] = React.useReducer((value: number) => value + 1, 0);
  const acceptance = useDraftAcceptance(scope, callbacks);
  const recoveryError = useDraftRecovery(scope, callbacks, acceptance, known, attempt);
  const { conversationId, draftId, draftSubmitted } = props;
  const recovered =
    acceptance.recovered?.id === (conversationId ?? draftId) ? acceptance.recovered : null;
  const historyId =
    recovered?.id ?? (acceptance.accepted.current === conversationId ? null : conversationId);
  const key = React.useMemo(
    () => (scope && historyId ? { ...scope, conversationId: historyId } : null),
    [scope, historyId],
  );
  const { history, historyError, controller } = useHistory(
    key,
    recovered,
    props,
    callbacks,
    attempt,
  );
  const chatSessionSeed = history?.seed ?? emptySeed;
  const hasDraftSession = Boolean(
    scope && !conversationId && executionSessionManager.has({ ...scope, conversationId: draftId }),
  );
  const waitingForDraft = scope && !conversationId && draftSubmitted && !hasDraftSession;
  return {
    controller,
    restoredTarget: history?.target ?? null,
    chatSessionSeed,
    keepLive: recovered?.keepLive ?? (acceptance.needsHistory && hasDraftSession),
    loadError: historyError ?? recoveryError,
    retry,
    handleConversationAccepted: acceptance.accept,
    isLoadingConversation: Boolean(
      waitingForDraft || (historyId && chatSessionSeed.conversationId !== historyId),
    ),
    restoreExecution:
      Number(chatSessionSeed.revision) > 0 && chatSessionSeed.conversationId === conversationId,
  };
}

function useSessionReconnect(
  visible: boolean,
  report: ChatProjectContextValue["reportReconnectState"],
) {
  const sessionId = React.useId();
  const latest = React.useRef<{
    state: ExecutionReconnectState | null;
    retry: () => Promise<void>;
  }>({ state: null, retry: noRetry });
  React.useEffect(() => {
    report(sessionId, visible ? latest.current.state : null, latest.current.retry);
    return () => report(sessionId, null, noRetry);
  }, [visible, sessionId, report]);
  return React.useCallback(
    (state: ExecutionReconnectState | null, retry: () => Promise<void>) => {
      latest.current = { state, retry };
      report(sessionId, visible ? state : null, retry);
    },
    [visible, sessionId, report],
  );
}

function useSessionIdentity(projectId: string | null, props: SessionProps) {
  const { conversationId, draftId } = props;
  const serverId = useExecutionPlatform().serverId;
  const scope = React.useMemo(
    () => (projectId ? { serverId, projectId } : null),
    [serverId, projectId],
  );
  const draftKey = React.useMemo(
    () => (conversationId ? { conversationId } : { draftId }),
    [conversationId, draftId],
  );
  const currentConversationTurnId =
    scope && conversationId
      ? (executionSessionManager.conversationStatuses.get(scope, conversationId)?.turnId ?? null)
      : null;
  return { scope, draftKey, currentConversationTurnId };
}

function useSessionModel(props: SessionProps) {
  const { conversationId, draftId, newConversationId, visible, onSubmitted } = props;
  const project = useChatProject();
  const { scope, draftKey, currentConversationTurnId } = useSessionIdentity(
    project.selectedProjectId,
    props,
  );
  const callbacks = React.useRef(props);
  callbacks.current = props;
  const { restoredTarget, controller, ...history } = useSessionHistory(
    scope,
    callbacks,
    props,
    project.conversationStatuses.has(draftId),
  );
  const target = useTarget(scope, draftKey, project.targetOptions, restoredTarget);
  const composerRef = React.useRef<ChatComposerHandle | null>(null);
  const handleReconnectStateChange = useSessionReconnect(visible, project.reportReconnectState);
  const { refreshConversations } = project;
  const handleConversationChange = React.useCallback(
    (options?: ConversationChangeOptions) => {
      if (options?.cancelConversationLoad) controller.current?.abort();
      refreshConversations();
    },
    [refreshConversations, controller],
  );
  React.useEffect(() => {
    if (visible)
      callbacks.current.onSelectionChange?.({ conversationId, target: target.selectedTarget });
  }, [visible, conversationId, target.selectedTarget]);
  return {
    ...target,
    ...history,
    conversationId,
    draftId,
    newConversationId,
    currentConversationTurnId,
    composerRef,
    handleConversationChange,
    handleReconnectStateChange,
    onSubmitted,
  };
}

export type ChatSessionContextValue = ReturnType<typeof useSessionModel>;
const ChatSessionContext = React.createContext<ChatSessionContextValue | null>(null);
export function useChatSession() {
  const value = React.useContext(ChatSessionContext);
  if (!value) throw new Error("useChatSession must be used inside ChatSessionProvider.");
  return value;
}
export function ChatSessionProvider(props: SessionProps) {
  const model = useSessionModel(props);
  return <ChatSessionContext value={model}>{props.children}</ChatSessionContext>;
}
