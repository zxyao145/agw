import {
  buildChatTargetOptions,
  createUuidV7,
  getTargetValue,
  type AiMessage,
  type AgwApiClient,
  type ChatTargetOption,
  type components,
} from "@agw/api";
import {
  createUserMessage,
  getAgentflowCheckpointMessage,
  getAgentSuggestionQueryParams,
  getClaudeInitCommands,
  getPendingHumanGate,
  prepareClaudeHistory,
  toCommandSource,
  toExecutionUserInput,
  type AgentSuggestionsResponse,
  type ChatImageAttachment,
  type CommandSource,
  type AgentflowCheckpointAvailability,
  type PendingHumanGate,
} from "@agw/chat-core";
import {
  DEFAULT_AGENT_MODE,
  createStreamingMessageBatcher,
  getAgentMode,
  getLatestAgentMode,
  getMessageStreamingScopeId,
  getTurnFinishedStatus,
  isModeControlMessage,
  isUserTurnMessage,
  mergeStreamingMessages,
  scopeMessagesByUserTurn,
  scopeStreamingMessage,
  type AgentMode,
  type PermissionMode,
  type StreamingMessageBatcher,
  type TurnFinishedStatus,
} from "@agw/execution-core";
import {
  createProjectConversationService,
  createProjectFilesService,
  type ConversationSummary,
  type ProjectConversationService,
  type ProjectFilesService,
} from "@agw/projects-core";
import { useQuery } from "@tanstack/react-query";
import React from "react";

import { getDefaultChatTargetValue } from "./chat-targets";
import { type ExecutionReconnectState, MobileExecutionSession } from "./native-execution-session";

export type Project = components["schemas"]["ProjectResponse"];
export type Agent = components["schemas"]["AgentResponse"];
export type Agentflow = components["schemas"]["Agentflow"];
export type AgentSuggestion = components["schemas"]["AgentSuggestionResponse"];

export type NativeWorkspaceContextValue = {
  projects: Project[];
  targets: ChatTargetOption[];
  conversations: ConversationSummary[];
  messages: AiMessage[];
  selectedProjectId: string | null;
  selectedTargetValue: string | null;
  selectedConversationId: string | null;
  selectedContextId: string | null;
  selectedProject: Project | null;
  selectedTarget: ChatTargetOption | null;
  permissionMode: PermissionMode;
  agentMode: AgentMode;
  commandSource: CommandSource;
  agentSuggestions: AgentSuggestion[];
  supportsAgentMode: boolean;
  isSuggestionsLoading: boolean;
  suggestionsError: string | null;
  isDependenciesLoading: boolean;
  isHistoryLoading: boolean;
  isChatLoading: boolean;
  isExecuting: boolean;
  reconnectState: ExecutionReconnectState | null;
  pendingHumanGate: PendingHumanGate | null;
  checkpointAvailability: AgentflowCheckpointAvailability[];
  error: string | null;
  conversationService: ProjectConversationService | null;
  filesService: ProjectFilesService | null;
  selectProject(projectId: string): void;
  selectTarget(value: string): void;
  setPermissionMode(mode: PermissionMode): void;
  setAgentMode(mode: AgentMode): void;
  selectConversation(conversationId: string): void;
  newChat(): Promise<void>;
  sendMessage(text: string, attachments: readonly ChatImageAttachment[]): Promise<void>;
  stopExecution(): void;
  submitHumanResponse(response: {
    approved: boolean;
    responseText?: string;
    approvalScope?: "once" | "always-tool" | "always-arguments";
    responseData?: unknown;
  }): Promise<void>;
  resumeCheckpoint(occurrenceId: string): Promise<void>;
  clearCurrentConversation(): Promise<void>;
  renameConversation(conversationId: string, title: string): Promise<void>;
  deleteConversation(conversationId: string): Promise<void>;
  refreshConversations(): Promise<void>;
  refreshDependencies(): Promise<void>;
};

const WorkspaceContext = React.createContext<NativeWorkspaceContextValue | null>(null);

export type NativeVerifiedServer = {
  profile: { id: string; serverUrl: string };
  client: AgwApiClient;
  token: string;
};

export function NativeWorkspaceProvider({
  verifiedServer,
  onTurnFinished,
  children,
}: {
  verifiedServer: NativeVerifiedServer | null;
  onTurnFinished?: (status: TurnFinishedStatus) => void;
  children: React.ReactNode;
}): React.JSX.Element {
  const profileId = verifiedServer?.profile.id ?? null;
  const client = verifiedServer?.client ?? null;
  const conversationService = React.useMemo(
    () => (client ? createProjectConversationService(client) : null),
    [client],
  );
  const filesService = React.useMemo(
    () => (client ? createProjectFilesService(client) : null),
    [client],
  );
  const [selectedProjectId, setSelectedProjectId] = React.useState<string | null>(null);
  const [selectedTargetValue, setSelectedTargetValue] = React.useState<string | null>(null);
  const [selectedConversationId, setSelectedConversationId] = React.useState<string | null>(null);
  const [selectedContextId, setSelectedContextId] = React.useState<string | null>(null);
  const [permissionMode, setPermissionModeState] = React.useState<PermissionMode>("fullAccess");
  const [agentMode, setAgentModeState] = React.useState<AgentMode>(DEFAULT_AGENT_MODE);
  const [claudeCommands, setClaudeCommands] = React.useState<string[]>([]);
  const [messages, setMessages] = React.useState<AiMessage[]>([]);
  const [isExecuting, setIsExecuting] = React.useState(false);
  const [reconnectState, setReconnectState] = React.useState<ExecutionReconnectState | null>(null);
  const [pendingHumanGate, setPendingHumanGate] = React.useState<PendingHumanGate | null>(null);
  const [checkpointAvailability, setCheckpointAvailability] = React.useState<
    AgentflowCheckpointAvailability[]
  >([]);
  const [operationError, setOperationError] = React.useState<string | null>(null);
  const executionSessionRef = React.useRef<MobileExecutionSession | null>(null);
  const executionSessionKeyRef = React.useRef<string | null>(null);
  const configuredSessionKeyRef = React.useRef<string | null>(null);
  const sessionConfigurationRef = React.useRef<{
    key: string;
    permissionMode: PermissionMode;
    promise: Promise<void>;
  } | null>(null);
  const executionGenerationRef = React.useRef(0);
  const modeChangeGenerationRef = React.useRef(0);
  const stopTimerRef = React.useRef<ReturnType<typeof setTimeout> | null>(null);
  const hydratedConversationRef = React.useRef<string | null>(null);
  const selectedConversationIdRef = React.useRef<string | null>(null);
  const selectedContextIdRef = React.useRef<string | null>(null);
  const activeStreamingScopeRef = React.useRef<string | null>(null);
  const confirmedAgentModeRef = React.useRef<AgentMode>(DEFAULT_AGENT_MODE);
  const batcherRef = React.useRef<StreamingMessageBatcher<AiMessage> | null>(null);
  const resumeBufferRef = React.useRef<AiMessage[] | null>(null);
  const onTurnFinishedRef = React.useRef(onTurnFinished);
  React.useEffect(() => {
    onTurnFinishedRef.current = onTurnFinished;
  }, [onTurnFinished]);

  if (!batcherRef.current) {
    batcherRef.current = createStreamingMessageBatcher((incoming, generation) => {
      if (generation !== executionGenerationRef.current) return;
      if (resumeBufferRef.current) {
        resumeBufferRef.current = mergeStreamingMessages(resumeBufferRef.current, incoming);
      } else {
        setMessages((current) => mergeStreamingMessages(current, incoming));
      }
    });
  }

  const projectsQuery = useQuery({
    queryKey: ["mobile", profileId, "projects"],
    enabled: Boolean(client),
    queryFn: async () => (await client!.apiGet("/api/projects")) as unknown as Project[],
  });
  const agentsQuery = useQuery({
    queryKey: ["mobile", profileId, "agents"],
    enabled: Boolean(client),
    queryFn: async () => (await client!.apiGet("/api/agents")) as unknown as Agent[],
  });
  const agentflowsQuery = useQuery({
    queryKey: ["mobile", profileId, "agentflows"],
    enabled: Boolean(client),
    queryFn: async () => (await client!.apiGet("/api/agentflows")) as unknown as Agentflow[],
  });
  const conversationsQuery = useQuery({
    queryKey: ["mobile", profileId, "conversations", selectedProjectId],
    enabled: Boolean(conversationService && selectedProjectId),
    queryFn: () => conversationService!.getProjectConversations(selectedProjectId!),
  });
  const conversationExists = Boolean(
    selectedConversationId &&
    conversationsQuery.data?.some((item) => item.conversationId === selectedConversationId),
  );
  const conversationDetailsQuery = useQuery({
    queryKey: ["mobile", profileId, "conversation", selectedProjectId, selectedConversationId],
    enabled: Boolean(
      conversationService && selectedProjectId && selectedConversationId && conversationExists,
    ),
    queryFn: () =>
      conversationService!.getProjectConversationHistory(
        selectedProjectId!,
        selectedConversationId!,
      ),
  });

  const projects = projectsQuery.data ?? [];
  const agents = agentsQuery.data ?? [];
  const agentflows = agentflowsQuery.data ?? [];
  const conversations = conversationsQuery.data ?? [];
  const targets = React.useMemo(
    () => buildChatTargetOptions({ projectId: selectedProjectId, agents, agentflows }),
    [agentflows, agents, selectedProjectId],
  );
  const selectedProject = projects.find((project) => project.id === selectedProjectId) ?? null;
  const selectedTarget =
    targets.find((target) => getTargetValue(target) === selectedTargetValue) ?? null;
  const suggestionQueryParams = React.useMemo(
    () => getAgentSuggestionQueryParams(selectedProjectId, selectedTarget),
    [selectedProjectId, selectedTarget],
  );
  const suggestionsQuery = useQuery({
    queryKey: [
      "mobile",
      profileId,
      "agent-suggestions",
      suggestionQueryParams?.projectId,
      suggestionQueryParams?.agentId,
    ],
    enabled: Boolean(client && suggestionQueryParams),
    queryFn: async () => {
      if (!suggestionQueryParams) {
        throw new Error("Agent suggestion query requires an agent.");
      }

      return (await client!.apiGet("/api/agents/suggestions", {
        params: {
          query: suggestionQueryParams,
        },
      })) as AgentSuggestionsResponse;
    },
  });
  const commandSource = React.useMemo(
    () => toCommandSource(suggestionsQuery.data, claudeCommands),
    [claudeCommands, suggestionsQuery.data],
  );
  const agentSuggestions = commandSource.mode === "system" ? commandSource.suggestions : [];
  const supportsAgentMode = agentSuggestions.some((suggestion) => suggestion.text === "/mode_set");

  const applyExecutionMessage = React.useCallback(
    (incoming: AiMessage) => {
      const generation = executionGenerationRef.current;
      if (incoming.additionalProperties?.type === "mode-change-failed") {
        batcherRef.current?.flush(generation);
        modeChangeGenerationRef.current += 1;
        setAgentModeState(confirmedAgentModeRef.current);
        const detail = incoming.contents.find(
          (content) => typeof content.content === "string" && content.content.trim(),
        )?.content;
        setOperationError(typeof detail === "string" ? detail : "Failed to change agent mode.");
        return;
      }

      const nextAgentMode = getAgentMode(incoming);
      if (nextAgentMode) {
        batcherRef.current?.flush(generation);
        confirmedAgentModeRef.current = nextAgentMode;
        setAgentModeState(nextAgentMode);
        if (isModeControlMessage(incoming)) return;
      }

      const initCommands = getClaudeInitCommands(incoming);
      if (initCommands !== null) {
        batcherRef.current?.flush(generation);
        setClaudeCommands(initCommands);
        return;
      }

      const checkpoint = getAgentflowCheckpointMessage(incoming);
      if (checkpoint) {
        batcherRef.current?.flush(generation);
        batcherRef.current?.enqueue(
          scopeStreamingMessage(
            incoming,
            getMessageStreamingScopeId(incoming) ??
              activeStreamingScopeRef.current ??
              incoming.messageId,
          ),
          generation,
        );
        const session = executionSessionRef.current;
        if (session && selectedTarget?.type === "agentflow") {
          void session
            .listAgentflowCheckpoints(selectedTarget.id)
            .then(setCheckpointAvailability)
            .catch(() => undefined);
        }
        return;
      }

      const humanGate = getPendingHumanGate(incoming);
      if (humanGate) {
        batcherRef.current?.flush(generation);
        setPendingHumanGate({
          ...humanGate,
          streamingScopeId:
            humanGate.streamingScopeId ?? activeStreamingScopeRef.current ?? undefined,
        });
        return;
      }

      if (incoming.additionalProperties?.type === "turn-start") {
        batcherRef.current?.flush(generation);
        activeStreamingScopeRef.current =
          getMessageStreamingScopeId(incoming) ??
          activeStreamingScopeRef.current ??
          incoming.messageId;
        setIsExecuting(true);
        return;
      }

      const finishedStatus = getTurnFinishedStatus(incoming);
      if (finishedStatus) {
        const hadActiveTurn = activeStreamingScopeRef.current !== null;
        batcherRef.current?.flush(generation);
        activeStreamingScopeRef.current = null;
        setPendingHumanGate(null);
        setIsExecuting(false);
        if (hadActiveTurn) onTurnFinishedRef.current?.(finishedStatus);
        return;
      }
      if (isUserTurnMessage(incoming)) return;

      batcherRef.current?.enqueue(
        scopeStreamingMessage(
          incoming,
          getMessageStreamingScopeId(incoming) ??
            activeStreamingScopeRef.current ??
            incoming.messageId,
        ),
        generation,
      );
    },
    [selectedTarget],
  );

  const disposeExecutionSession = React.useCallback((resetReconnectState = true) => {
    const session = executionSessionRef.current;
    executionSessionRef.current = null;
    executionSessionKeyRef.current = null;
    configuredSessionKeyRef.current = null;
    sessionConfigurationRef.current = null;
    activeStreamingScopeRef.current = null;
    if (resetReconnectState) setReconnectState(null);
    if (session) void session.dispose().catch(() => undefined);
  }, []);

  const ensureContextId = React.useCallback(() => {
    const contextId = selectedContextIdRef.current ?? createUuidV7();
    if (selectedContextIdRef.current === null) {
      selectedContextIdRef.current = contextId;
      setSelectedContextId(contextId);
    }
    return contextId;
  }, []);

  const ensureConfiguredSession = React.useCallback(
    async (
      contextId: string,
      nextPermissionMode: PermissionMode,
    ): Promise<{
      session: MobileExecutionSession;
      configuredWithRequestedPermission: boolean;
    } | null> => {
      if (!verifiedServer || !selectedProjectId) {
        throw new Error("Please select a project.");
      }

      const sessionKey = JSON.stringify([profileId, selectedProjectId, contextId]);
      let session = executionSessionRef.current;
      if (!session || executionSessionKeyRef.current !== sessionKey) {
        disposeExecutionSession();
        let nextSession!: MobileExecutionSession;
        nextSession = new MobileExecutionSession({
          serverUrl: verifiedServer.profile.serverUrl,
          token: verifiedServer.token,
          onMessage: (incoming) => {
            if (executionSessionRef.current === nextSession) applyExecutionMessage(incoming);
          },
          onClose: () => {
            if (executionSessionRef.current === nextSession) disposeExecutionSession();
          },
          onReconnecting: (state) => {
            if (executionSessionRef.current === nextSession) setReconnectState(state);
          },
        });
        session = nextSession;
        executionSessionRef.current = session;
        executionSessionKeyRef.current = sessionKey;
      }

      if (configuredSessionKeyRef.current !== sessionKey) {
        let configuration = sessionConfigurationRef.current;
        if (!configuration || configuration.key !== sessionKey) {
          configuration = {
            key: sessionKey,
            permissionMode: nextPermissionMode,
            promise: session.configure({
              projectId: selectedProjectId,
              contextId,
              permissionMode: nextPermissionMode,
            }),
          };
          sessionConfigurationRef.current = configuration;
        }

        try {
          await configuration.promise;
        } catch (error) {
          if (executionSessionRef.current !== session) return null;
          disposeExecutionSession();
          throw error;
        } finally {
          if (sessionConfigurationRef.current === configuration) {
            sessionConfigurationRef.current = null;
          }
        }

        if (executionSessionRef.current !== session) return null;
        configuredSessionKeyRef.current = sessionKey;
        return {
          session,
          configuredWithRequestedPermission: configuration.permissionMode === nextPermissionMode,
        };
      }

      return executionSessionRef.current === session
        ? { session, configuredWithRequestedPermission: false }
        : null;
    },
    [applyExecutionMessage, disposeExecutionSession, profileId, selectedProjectId, verifiedServer],
  );

  React.useEffect(() => {
    if (projects.length === 0) {
      setSelectedProjectId(null);
      return;
    }
    if (!projects.some((project) => project.id === selectedProjectId)) {
      setSelectedProjectId(projects[0].id);
    }
  }, [projects, selectedProjectId]);

  React.useEffect(() => {
    if (targets.length === 0) {
      setSelectedTargetValue(null);
      return;
    }
    if (!targets.some((target) => getTargetValue(target) === selectedTargetValue)) {
      setSelectedTargetValue(getDefaultChatTargetValue(targets));
    }
  }, [selectedTargetValue, targets]);

  React.useEffect(() => {
    modeChangeGenerationRef.current += 1;
    confirmedAgentModeRef.current = DEFAULT_AGENT_MODE;
    setAgentModeState(DEFAULT_AGENT_MODE);
    setClaudeCommands([]);
  }, [profileId, selectedTargetValue]);

  React.useEffect(() => {
    const key =
      selectedProjectId && selectedConversationId
        ? `${selectedProjectId}:${selectedConversationId}`
        : null;
    if (!key) {
      hydratedConversationRef.current = null;
      selectedContextIdRef.current = null;
      setSelectedContextId(null);
      setMessages([]);
      setClaudeCommands([]);
      setPendingHumanGate(null);
      setCheckpointAvailability([]);
      return;
    }
    if (conversationDetailsQuery.data && hydratedConversationRef.current !== key) {
      hydratedConversationRef.current = key;
      selectedContextIdRef.current = conversationDetailsQuery.data.contextId;
      setSelectedContextId(conversationDetailsQuery.data.contextId);
      const nextAgentMode = getLatestAgentMode(conversationDetailsQuery.data.messages);
      const claudeHistory = prepareClaudeHistory(conversationDetailsQuery.data.messages);
      confirmedAgentModeRef.current = nextAgentMode;
      setAgentModeState(nextAgentMode);
      setMessages(scopeMessagesByUserTurn(claudeHistory.messages));
      setClaudeCommands(claudeHistory.commands);
      setPendingHumanGate(null);
    }
  }, [conversationDetailsQuery.data, selectedConversationId, selectedProjectId]);

  React.useEffect(() => {
    if (
      !selectedContextId ||
      selectedTarget?.type !== "agentflow" ||
      !messages.some((message) => getAgentflowCheckpointMessage(message))
    ) {
      setCheckpointAvailability([]);
      return;
    }

    let active = true;
    void ensureConfiguredSession(selectedContextId, permissionMode)
      .then((configured) => configured?.session.listAgentflowCheckpoints(selectedTarget.id))
      .then((checkpoints) => {
        if (active && checkpoints) setCheckpointAvailability(checkpoints);
      })
      .catch(() => undefined);
    return () => {
      active = false;
    };
  }, [ensureConfiguredSession, messages, permissionMode, selectedContextId, selectedTarget]);

  React.useEffect(() => {
    executionGenerationRef.current += 1;
    modeChangeGenerationRef.current += 1;
    batcherRef.current?.discard();
    disposeExecutionSession();
    selectedConversationIdRef.current = null;
    selectedContextIdRef.current = null;
    setSelectedProjectId(null);
    setSelectedTargetValue(null);
    setSelectedConversationId(null);
    setSelectedContextId(null);
    setMessages([]);
    setClaudeCommands([]);
    setIsExecuting(false);
    setReconnectState(null);
    setPendingHumanGate(null);
    setCheckpointAvailability([]);
    setOperationError(null);
    return () => {
      disposeExecutionSession(false);
      batcherRef.current?.discard();
    };
  }, [disposeExecutionSession, profileId]);

  const ensureIdle = React.useCallback(() => {
    if (isExecuting) throw new Error("Stop the current execution before switching context.");
  }, [isExecuting]);

  const newChat = React.useCallback(async () => {
    ensureIdle();

    const conversation =
      conversationService && selectedProjectId
        ? await conversationService.createProjectConversation(selectedProjectId)
        : null;

    executionGenerationRef.current += 1;
    modeChangeGenerationRef.current += 1;
    batcherRef.current?.discard();
    disposeExecutionSession();
    hydratedConversationRef.current = conversation
      ? `${selectedProjectId}:${conversation.conversationId}`
      : null;
    selectedConversationIdRef.current = conversation?.conversationId ?? null;
    selectedContextIdRef.current = conversation?.contextId ?? null;
    confirmedAgentModeRef.current = DEFAULT_AGENT_MODE;
    setSelectedConversationId(conversation?.conversationId ?? null);
    setSelectedContextId(conversation?.contextId ?? null);
    setMessages([]);
    setClaudeCommands([]);
    setAgentModeState(DEFAULT_AGENT_MODE);
    setPendingHumanGate(null);
    setCheckpointAvailability([]);
    setOperationError(null);
    if (conversation) {
      await conversationsQuery.refetch();
    }
  }, [
    conversationService,
    conversationsQuery,
    disposeExecutionSession,
    ensureIdle,
    selectedProjectId,
  ]);

  const selectProject = React.useCallback(
    (projectId: string) => {
      ensureIdle();
      executionGenerationRef.current += 1;
      modeChangeGenerationRef.current += 1;
      batcherRef.current?.discard();
      disposeExecutionSession();
      selectedConversationIdRef.current = null;
      selectedContextIdRef.current = null;
      confirmedAgentModeRef.current = DEFAULT_AGENT_MODE;
      setSelectedProjectId(projectId);
      setSelectedConversationId(null);
      setSelectedContextId(null);
      setMessages([]);
      setClaudeCommands([]);
      setAgentModeState(DEFAULT_AGENT_MODE);
      setPendingHumanGate(null);
      setCheckpointAvailability([]);
      hydratedConversationRef.current = null;
      setOperationError(null);
    },
    [disposeExecutionSession, ensureIdle],
  );
  const selectConversation = React.useCallback(
    (conversationId: string) => {
      if (selectedConversationIdRef.current === conversationId) return;
      ensureIdle();
      executionGenerationRef.current += 1;
      modeChangeGenerationRef.current += 1;
      batcherRef.current?.discard();
      disposeExecutionSession();
      hydratedConversationRef.current = null;
      selectedConversationIdRef.current = conversationId;
      selectedContextIdRef.current = null;
      confirmedAgentModeRef.current = DEFAULT_AGENT_MODE;
      setSelectedConversationId(conversationId);
      setSelectedContextId(null);
      setMessages([]);
      setClaudeCommands([]);
      setAgentModeState(DEFAULT_AGENT_MODE);
      setPendingHumanGate(null);
      setCheckpointAvailability([]);
      setOperationError(null);
    },
    [disposeExecutionSession, ensureIdle],
  );

  const setPermissionMode = React.useCallback(
    (nextPermissionMode: PermissionMode) => {
      const previousPermissionMode = permissionMode;
      setPermissionModeState(nextPermissionMode);
      setOperationError(null);
      if (!selectedProjectId) return;

      const contextId = ensureContextId();
      const generation = executionGenerationRef.current;
      void ensureConfiguredSession(contextId, nextPermissionMode)
        .then((configured) => {
          if (
            !configured ||
            generation !== executionGenerationRef.current ||
            configured.configuredWithRequestedPermission
          ) {
            return;
          }
          return configured.session.setPermissionMode(nextPermissionMode);
        })
        .catch((error) => {
          if (generation !== executionGenerationRef.current) return;
          setPermissionModeState(previousPermissionMode);
          setOperationError(getErrorMessage(error));
        });
    },
    [ensureConfiguredSession, ensureContextId, permissionMode, selectedProjectId],
  );

  const setAgentMode = React.useCallback(
    (nextAgentMode: AgentMode) => {
      if (!selectedProjectId || !selectedTarget || selectedTarget.type !== "agent") {
        setOperationError("Please select a mode-capable agent.");
        return;
      }

      const previousAgentMode = agentMode;
      const changeGeneration = modeChangeGenerationRef.current + 1;
      modeChangeGenerationRef.current = changeGeneration;
      setAgentModeState(nextAgentMode);
      setOperationError(null);
      const contextId = ensureContextId();
      const executionGeneration = executionGenerationRef.current;
      void ensureConfiguredSession(contextId, permissionMode)
        .then((configured) => {
          if (
            !configured ||
            executionGeneration !== executionGenerationRef.current ||
            changeGeneration !== modeChangeGenerationRef.current
          ) {
            return;
          }
          return configured.session.setMode(selectedTarget.id, nextAgentMode);
        })
        .catch((error) => {
          if (
            executionGeneration !== executionGenerationRef.current ||
            changeGeneration !== modeChangeGenerationRef.current
          ) {
            return;
          }
          setAgentModeState(previousAgentMode);
          setOperationError(getErrorMessage(error));
        });
    },
    [
      agentMode,
      ensureConfiguredSession,
      ensureContextId,
      permissionMode,
      selectedProjectId,
      selectedTarget,
    ],
  );

  const sendMessage = React.useCallback(
    async (text: string, attachments: readonly ChatImageAttachment[]) => {
      if (
        !verifiedServer ||
        !selectedProjectId ||
        !selectedTarget ||
        isExecuting ||
        (!text.trim() && attachments.length === 0)
      ) {
        return;
      }
      const contextId = ensureContextId();
      const executionId = createUuidV7();
      const userMessage = createUserMessage(text, attachments);
      const scopedUserMessage = scopeStreamingMessage(userMessage, userMessage.messageId);
      const generation = executionGenerationRef.current + 1;
      executionGenerationRef.current = generation;
      hydratedConversationRef.current = null;
      activeStreamingScopeRef.current = userMessage.messageId;
      setMessages((current) => [...current, scopedUserMessage]);
      setIsExecuting(true);
      setOperationError(null);

      try {
        const configured = await ensureConfiguredSession(contextId, permissionMode);
        if (!configured || generation !== executionGenerationRef.current) return;
        await configured.session.execute({
          agentId: selectedTarget.id,
          agentType: selectedTarget.type === "agentflow" ? 1 : 0,
          executionId,
          input: toExecutionUserInput(userMessage),
        });
        if (generation !== executionGenerationRef.current) return;
        batcherRef.current?.flush(generation);
        const refreshedConversations = await conversationsQuery.refetch();
        const persistedConversation = refreshedConversations.data?.find(
          (conversation) => conversation.contextId === contextId,
        );
        if (persistedConversation && selectedContextIdRef.current === contextId) {
          selectedConversationIdRef.current = persistedConversation.conversationId;
          setSelectedConversationId(persistedConversation.conversationId);
          hydratedConversationRef.current = `${selectedProjectId}:${persistedConversation.conversationId}`;
        }
      } catch (caught) {
        if (generation !== executionGenerationRef.current) return;
        batcherRef.current?.flush(generation);
        setOperationError(getErrorMessage(caught));
      } finally {
        if (generation === executionGenerationRef.current) {
          if (stopTimerRef.current) clearTimeout(stopTimerRef.current);
          stopTimerRef.current = null;
          activeStreamingScopeRef.current = null;
          setIsExecuting(false);
          setReconnectState(null);
        }
      }
    },
    [
      conversationsQuery,
      ensureConfiguredSession,
      ensureContextId,
      isExecuting,
      permissionMode,
      selectedProjectId,
      selectedTarget,
      verifiedServer,
    ],
  );

  const stopExecution = React.useCallback(() => {
    const session = executionSessionRef.current;
    if (!session) return;
    if (stopTimerRef.current) clearTimeout(stopTimerRef.current);
    stopTimerRef.current = setTimeout(() => {
      batcherRef.current?.flush(executionGenerationRef.current);
      executionGenerationRef.current += 1;
      activeStreamingScopeRef.current = null;
      if (executionSessionRef.current === session) disposeExecutionSession();
      setIsExecuting(false);
      setOperationError("Disconnected from the server; the execution may still be running.");
    }, 3_000);
    void session.interrupt("Stop requested by user.").catch(() => {
      if (stopTimerRef.current) clearTimeout(stopTimerRef.current);
      stopTimerRef.current = null;
      executionGenerationRef.current += 1;
      activeStreamingScopeRef.current = null;
      if (executionSessionRef.current === session) disposeExecutionSession();
      setIsExecuting(false);
      setOperationError("Failed to stop the current execution.");
    });
  }, [disposeExecutionSession]);

  const submitHumanResponse = React.useCallback(
    async ({
      approved,
      responseText,
      approvalScope = "once",
      responseData,
    }: {
      approved: boolean;
      responseText?: string;
      approvalScope?: "once" | "always-tool" | "always-arguments";
      responseData?: unknown;
    }) => {
      const request = pendingHumanGate;
      const session = executionSessionRef.current;
      if (!request || !session) return;
      try {
        await session.submitHumanResponse({
          requestId: request.requestId,
          approved,
          responseText,
          approvalScope,
          responseData,
        });
        setPendingHumanGate((current) =>
          current?.requestId === request.requestId ? null : current,
        );
      } catch (caught) {
        setOperationError(getErrorMessage(caught));
      }
    },
    [pendingHumanGate],
  );

  const resumeCheckpoint = React.useCallback(
    async (occurrenceId: string) => {
      if (!selectedProjectId || !selectedContextId || selectedTarget?.type !== "agentflow") {
        return;
      }
      ensureIdle();
      const boundaryIndex = messages.findLastIndex(
        (message) => getAgentflowCheckpointMessage(message)?.occurrenceId === occurrenceId,
      );
      if (boundaryIndex < 0) return;

      const generation = executionGenerationRef.current + 1;
      executionGenerationRef.current = generation;
      const resumeExecutionId = createUuidV7();
      resumeBufferRef.current = [];
      activeStreamingScopeRef.current = null;
      setPendingHumanGate(null);
      setIsExecuting(true);
      setOperationError(null);

      try {
        const configured = await ensureConfiguredSession(selectedContextId, permissionMode);
        if (!configured || generation !== executionGenerationRef.current) return;
        await configured.session.resumeCheckpoint({
          checkpointOccurrenceId: occurrenceId,
          agentflowId: selectedTarget.id,
          resumeExecutionId,
        });
        if (generation !== executionGenerationRef.current) return;
        batcherRef.current?.flush(generation);
        const buffered = resumeBufferRef.current ?? [];
        resumeBufferRef.current = null;
        setMessages((current) =>
          mergeStreamingMessages(current.slice(0, boundaryIndex + 1), buffered),
        );
        setCheckpointAvailability((current) =>
          current.filter(
            (checkpoint) =>
              checkpoint.boundarySequence <=
              (current.find((item) => item.occurrenceId === occurrenceId)?.boundarySequence ??
                Number.MAX_SAFE_INTEGER),
          ),
        );
      } catch (caught) {
        resumeBufferRef.current = null;
        setIsExecuting(false);
        setOperationError(getErrorMessage(caught));
      }
    },
    [
      ensureConfiguredSession,
      ensureIdle,
      messages,
      permissionMode,
      selectedContextId,
      selectedProjectId,
      selectedTarget,
    ],
  );

  const clearCurrentConversation = React.useCallback(async () => {
    if (!conversationService || !selectedProjectId || !selectedConversationId || !selectedContextId)
      return;
    ensureIdle();
    const conversationToClear = selectedConversationId;
    const contextToClear = selectedContextId;
    // Clearing records keeps the conversation identity; only New Chat replaces it.
    selectedContextIdRef.current = contextToClear;
    await conversationService.clearProjectConversationRecords(
      selectedProjectId,
      conversationToClear,
    );
    if (
      selectedConversationIdRef.current !== conversationToClear ||
      selectedContextIdRef.current !== contextToClear
    ) {
      await conversationsQuery.refetch();
      return;
    }
    executionGenerationRef.current += 1;
    modeChangeGenerationRef.current += 1;
    batcherRef.current?.discard();
    disposeExecutionSession();
    hydratedConversationRef.current = `${selectedProjectId}:${conversationToClear}`;
    setSelectedConversationId(conversationToClear);
    setSelectedContextId(contextToClear);
    confirmedAgentModeRef.current = DEFAULT_AGENT_MODE;
    setMessages([]);
    setClaudeCommands([]);
    setAgentModeState(DEFAULT_AGENT_MODE);
    setPendingHumanGate(null);
    setCheckpointAvailability([]);
    await conversationsQuery.refetch();
  }, [
    conversationService,
    conversationsQuery,
    disposeExecutionSession,
    ensureIdle,
    selectedConversationId,
    selectedContextId,
    selectedProjectId,
  ]);

  const renameConversation = React.useCallback(
    async (conversationId: string, title: string) => {
      if (!conversationService || !selectedProjectId) return;
      ensureIdle();
      await conversationService.updateProjectConversationTitle(
        selectedProjectId,
        conversationId,
        title,
      );
      await conversationsQuery.refetch();
    },
    [conversationService, conversationsQuery, ensureIdle, selectedProjectId],
  );
  const deleteConversation = React.useCallback(
    async (conversationId: string) => {
      if (!conversationService || !selectedProjectId) return;
      ensureIdle();
      await conversationService.deleteProjectConversation(selectedProjectId, conversationId);
      if (selectedConversationId === conversationId) await newChat();
      await conversationsQuery.refetch();
    },
    [
      conversationService,
      conversationsQuery,
      ensureIdle,
      newChat,
      selectedConversationId,
      selectedProjectId,
    ],
  );

  const dependencyError = projectsQuery.error ?? agentsQuery.error ?? agentflowsQuery.error;
  const value = React.useMemo<NativeWorkspaceContextValue>(
    () => ({
      projects,
      targets,
      conversations,
      messages,
      selectedProjectId,
      selectedTargetValue,
      selectedConversationId,
      selectedContextId,
      selectedProject,
      selectedTarget,
      permissionMode,
      agentMode,
      commandSource,
      agentSuggestions,
      supportsAgentMode,
      isSuggestionsLoading: suggestionsQuery.isLoading,
      suggestionsError: suggestionsQuery.error ? getErrorMessage(suggestionsQuery.error) : null,
      isDependenciesLoading:
        projectsQuery.isLoading || agentsQuery.isLoading || agentflowsQuery.isLoading,
      isHistoryLoading: conversationsQuery.isLoading,
      isChatLoading: conversationDetailsQuery.isLoading,
      isExecuting,
      reconnectState,
      pendingHumanGate,
      checkpointAvailability,
      error: operationError ?? (dependencyError ? getErrorMessage(dependencyError) : null),
      conversationService,
      filesService,
      selectProject,
      selectTarget: setSelectedTargetValue,
      setPermissionMode,
      setAgentMode,
      selectConversation,
      newChat,
      sendMessage,
      stopExecution,
      submitHumanResponse,
      resumeCheckpoint,
      clearCurrentConversation,
      renameConversation,
      deleteConversation,
      refreshConversations: async () => {
        await conversationsQuery.refetch();
      },
      refreshDependencies: async () => {
        await Promise.all([
          projectsQuery.refetch(),
          agentsQuery.refetch(),
          agentflowsQuery.refetch(),
        ]);
      },
    }),
    [
      projects,
      targets,
      conversations,
      messages,
      selectedProjectId,
      selectedTargetValue,
      selectedConversationId,
      selectedContextId,
      selectedProject,
      selectedTarget,
      permissionMode,
      agentMode,
      commandSource,
      agentSuggestions,
      supportsAgentMode,
      suggestionsQuery.isLoading,
      suggestionsQuery.error,
      projectsQuery,
      agentsQuery,
      agentflowsQuery,
      conversationsQuery,
      conversationDetailsQuery.isLoading,
      isExecuting,
      reconnectState,
      pendingHumanGate,
      checkpointAvailability,
      operationError,
      dependencyError,
      conversationService,
      filesService,
      selectProject,
      selectConversation,
      setPermissionMode,
      setAgentMode,
      newChat,
      sendMessage,
      stopExecution,
      submitHumanResponse,
      resumeCheckpoint,
      clearCurrentConversation,
      renameConversation,
      deleteConversation,
    ],
  );

  return <WorkspaceContext.Provider value={value}>{children}</WorkspaceContext.Provider>;
}

export function useNativeWorkspace(): NativeWorkspaceContextValue {
  const value = React.useContext(WorkspaceContext);
  if (!value) throw new Error("useNativeWorkspace must be used inside NativeWorkspaceProvider.");
  return value;
}

function getErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
