import {
  buildChatTargetOptions,
  createUuidV7,
  getTargetValue,
  type AiMessage,
  type ChatTargetOption,
  type components,
} from "@agw/api";
import { createUserMessage, toExecutionUserInput, type ChatImageAttachment } from "@agw/chat-core";
import {
  createStreamingMessageBatcher,
  mergeStreamingMessages,
  scopeMessagesByUserTurn,
  scopeStreamingMessage,
  type PermissionMode,
  type StreamingMessageBatcher,
} from "@agw/execution-core";
import {
  createProjectContextService,
  createProjectFilesService,
  type ContextSummary,
  type ProjectContextService,
  type ProjectFilesService,
} from "@agw/projects-core";
import { useQuery } from "@tanstack/react-query";
import React from "react";

import { getErrorMessage } from "@/lib/errors";
import { useSession } from "@/features/servers/session-provider";
import { getDefaultChatTargetValue } from "@/features/chat/chat-targets";
import {
  type AgentMode,
  executeWithWebSocket,
  type ExecutionReconnectState,
  type MobileExecutionHandle,
} from "@/features/chat/execution-ws";

export type Project = components["schemas"]["ProjectResponse"];
export type Agent = components["schemas"]["AgentResponse"];
export type Agentflow = components["schemas"]["Agentflow"];
export type AgentSuggestion = components["schemas"]["AgentSuggestionResponse"];
type AgentSuggestionsResponse = components["schemas"]["AgentSuggestionsResponse"];

type WorkspaceContextValue = {
  projects: Project[];
  targets: ChatTargetOption[];
  contexts: ContextSummary[];
  messages: AiMessage[];
  selectedProjectId: string | null;
  selectedTargetValue: string | null;
  selectedContextId: string | null;
  selectedProject: Project | null;
  selectedTarget: ChatTargetOption | null;
  permissionMode: PermissionMode;
  agentMode: AgentMode;
  agentSuggestions: AgentSuggestion[];
  supportsAgentMode: boolean;
  isSuggestionsLoading: boolean;
  suggestionsError: string | null;
  isDependenciesLoading: boolean;
  isHistoryLoading: boolean;
  isChatLoading: boolean;
  isExecuting: boolean;
  reconnectState: ExecutionReconnectState | null;
  error: string | null;
  contextService: ProjectContextService | null;
  filesService: ProjectFilesService | null;
  selectProject(projectId: string): void;
  selectTarget(value: string): void;
  setPermissionMode(mode: PermissionMode): void;
  setAgentMode(mode: AgentMode): void;
  selectContext(contextId: string): void;
  newChat(): void;
  sendMessage(text: string, attachments: readonly ChatImageAttachment[]): Promise<void>;
  stopExecution(): void;
  clearCurrentContext(): Promise<void>;
  renameContext(contextId: string, title: string): Promise<void>;
  deleteContext(contextId: string): Promise<void>;
  refreshContexts(): Promise<void>;
  refreshDependencies(): Promise<void>;
};

const WorkspaceContext = React.createContext<WorkspaceContextValue | null>(null);

export function WorkspaceProvider({ children }: { children: React.ReactNode }): React.JSX.Element {
  const { verifiedServer } = useSession();
  const profileId = verifiedServer?.profile.id ?? null;
  const client = verifiedServer?.client ?? null;
  const contextService = React.useMemo(
    () => (client ? createProjectContextService(client) : null),
    [client],
  );
  const filesService = React.useMemo(
    () => (client ? createProjectFilesService(client) : null),
    [client],
  );
  const [selectedProjectId, setSelectedProjectId] = React.useState<string | null>(null);
  const [selectedTargetValue, setSelectedTargetValue] = React.useState<string | null>(null);
  const [selectedContextId, setSelectedContextId] = React.useState<string | null>(null);
  const [permissionMode, setPermissionMode] = React.useState<PermissionMode>("fullAccess");
  const [agentMode, setAgentMode] = React.useState<AgentMode>("execute");
  const [messages, setMessages] = React.useState<AiMessage[]>([]);
  const [isExecuting, setIsExecuting] = React.useState(false);
  const [reconnectState, setReconnectState] = React.useState<ExecutionReconnectState | null>(null);
  const [operationError, setOperationError] = React.useState<string | null>(null);
  const executionHandleRef = React.useRef<MobileExecutionHandle | null>(null);
  const executionGenerationRef = React.useRef(0);
  const stopTimerRef = React.useRef<ReturnType<typeof setTimeout> | null>(null);
  const hydratedContextRef = React.useRef<string | null>(null);
  const batcherRef = React.useRef<StreamingMessageBatcher<AiMessage> | null>(null);

  if (!batcherRef.current) {
    batcherRef.current = createStreamingMessageBatcher((incoming, generation) => {
      if (generation !== executionGenerationRef.current) return;
      setMessages((current) => mergeStreamingMessages(current, incoming));
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
  const contextsQuery = useQuery({
    queryKey: ["mobile", profileId, "contexts", selectedProjectId],
    enabled: Boolean(contextService && selectedProjectId),
    queryFn: () => contextService!.getProjectContexts(selectedProjectId!),
  });
  const contextExists = Boolean(
    selectedContextId && contextsQuery.data?.some((item) => item.contextId === selectedContextId),
  );
  const contextDetailsQuery = useQuery({
    queryKey: ["mobile", profileId, "context", selectedProjectId, selectedContextId],
    enabled: Boolean(contextService && selectedProjectId && selectedContextId && contextExists),
    queryFn: () => contextService!.getProjectContextDetails(selectedProjectId!, selectedContextId!),
  });

  const projects = projectsQuery.data ?? [];
  const agents = agentsQuery.data ?? [];
  const agentflows = agentflowsQuery.data ?? [];
  const contexts = contextsQuery.data ?? [];
  const targets = React.useMemo(
    () => buildChatTargetOptions({ projectId: selectedProjectId, agents, agentflows }),
    [agentflows, agents, selectedProjectId],
  );
  const selectedProject = projects.find((project) => project.id === selectedProjectId) ?? null;
  const selectedTarget =
    targets.find((target) => getTargetValue(target) === selectedTargetValue) ?? null;
  const suggestionsQuery = useQuery({
    queryKey: [
      "mobile",
      profileId,
      "agent-suggestions",
      selectedProjectId,
      selectedTarget?.type,
      selectedTarget?.id,
    ],
    enabled: Boolean(client && selectedTarget?.type === "agent"),
    queryFn: async () =>
      (await client!.apiGet("/api/agents/suggestions", {
        params: {
          query: {
            projectId: selectedProjectId ?? undefined,
            agentId: selectedTarget!.id,
          },
        },
      })) as AgentSuggestionsResponse,
  });
  const agentSuggestions =
    suggestionsQuery.data?.mode === "system" ? suggestionsQuery.data.suggestions : [];
  const supportsAgentMode = agentSuggestions.some((suggestion) => suggestion.text === "/mode_set");

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
    setAgentMode("execute");
  }, [profileId, selectedTargetValue]);

  React.useEffect(() => {
    const key =
      selectedProjectId && selectedContextId ? `${selectedProjectId}:${selectedContextId}` : null;
    if (!key) {
      hydratedContextRef.current = null;
      setMessages([]);
      return;
    }
    if (contextDetailsQuery.data && hydratedContextRef.current !== key) {
      hydratedContextRef.current = key;
      setMessages(scopeMessagesByUserTurn(contextDetailsQuery.data.messages));
    }
  }, [contextDetailsQuery.data, selectedContextId, selectedProjectId]);

  React.useEffect(() => {
    executionGenerationRef.current += 1;
    batcherRef.current?.discard();
    executionHandleRef.current?.close();
    executionHandleRef.current = null;
    setSelectedProjectId(null);
    setSelectedTargetValue(null);
    setSelectedContextId(null);
    setMessages([]);
    setIsExecuting(false);
    setReconnectState(null);
    setOperationError(null);
    return () => {
      executionHandleRef.current?.close();
      batcherRef.current?.discard();
    };
  }, [profileId]);

  const ensureIdle = React.useCallback(() => {
    if (isExecuting) throw new Error("Stop the current execution before switching context.");
  }, [isExecuting]);

  const newChat = React.useCallback(() => {
    ensureIdle();
    executionGenerationRef.current += 1;
    batcherRef.current?.discard();
    hydratedContextRef.current = null;
    setSelectedContextId(null);
    setMessages([]);
    setOperationError(null);
  }, [ensureIdle]);

  const selectProject = React.useCallback(
    (projectId: string) => {
      ensureIdle();
      setSelectedProjectId(projectId);
      setSelectedContextId(null);
      setMessages([]);
      hydratedContextRef.current = null;
      setOperationError(null);
    },
    [ensureIdle],
  );
  const selectContext = React.useCallback(
    (contextId: string) => {
      ensureIdle();
      hydratedContextRef.current = null;
      setSelectedContextId(contextId);
      setOperationError(null);
    },
    [ensureIdle],
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
      const contextId = selectedContextId ?? createUuidV7();
      const executionId = createUuidV7();
      const userMessage = createUserMessage(text, attachments);
      const scopedUserMessage = scopeStreamingMessage(userMessage, userMessage.messageId);
      const generation = executionGenerationRef.current + 1;
      executionGenerationRef.current = generation;
      hydratedContextRef.current = `${selectedProjectId}:${contextId}`;
      setSelectedContextId(contextId);
      setMessages((current) => [...current, scopedUserMessage]);
      setIsExecuting(true);
      setOperationError(null);

      const handle = executeWithWebSocket({
        serverUrl: verifiedServer.profile.serverUrl,
        token: verifiedServer.token,
        request: {
          projectId: selectedProjectId,
          contextId,
          agentId: selectedTarget.id,
          agentType: selectedTarget.type === "agentflow" ? 1 : 0,
          executionId,
          permissionMode,
          agentMode,
          input: toExecutionUserInput(userMessage),
        },
        onMessage: (incoming) => {
          if (generation !== executionGenerationRef.current || incoming.role === "user") return;
          batcherRef.current?.enqueue(
            scopeStreamingMessage(incoming, userMessage.messageId),
            generation,
          );
        },
        onReconnecting: setReconnectState,
      });
      executionHandleRef.current = handle;

      try {
        await handle.promise;
        batcherRef.current?.flush(generation);
        await contextsQuery.refetch();
      } catch (caught) {
        batcherRef.current?.flush(generation);
        setOperationError(getErrorMessage(caught));
      } finally {
        if (stopTimerRef.current) clearTimeout(stopTimerRef.current);
        stopTimerRef.current = null;
        if (executionHandleRef.current === handle) executionHandleRef.current = null;
        setIsExecuting(false);
        setReconnectState(null);
      }
    },
    [
      contextsQuery,
      agentMode,
      isExecuting,
      selectedContextId,
      permissionMode,
      selectedProjectId,
      selectedTarget,
      verifiedServer,
    ],
  );

  const stopExecution = React.useCallback(() => {
    const handle = executionHandleRef.current;
    if (!handle) return;
    if (stopTimerRef.current) clearTimeout(stopTimerRef.current);
    stopTimerRef.current = setTimeout(() => {
      if (executionHandleRef.current === handle) executionHandleRef.current = null;
      batcherRef.current?.flush(executionGenerationRef.current);
      handle.close();
      setIsExecuting(false);
      setOperationError("Disconnected from the server; the execution may still be running.");
    }, 3_000);
    void handle.interrupt("Stop requested by user.").catch(() => {
      if (stopTimerRef.current) clearTimeout(stopTimerRef.current);
      stopTimerRef.current = null;
      handle.close();
      setIsExecuting(false);
      setOperationError("Failed to stop the current execution.");
    });
  }, []);

  const clearCurrentContext = React.useCallback(async () => {
    if (!contextService || !selectedProjectId || !selectedContextId) return;
    ensureIdle();
    await contextService.clearProjectContextRecords(selectedProjectId, selectedContextId);
    setMessages([]);
    await contextsQuery.refetch();
  }, [contextService, contextsQuery, ensureIdle, selectedContextId, selectedProjectId]);

  const renameContext = React.useCallback(
    async (contextId: string, title: string) => {
      if (!contextService || !selectedProjectId) return;
      ensureIdle();
      await contextService.updateProjectContextTitle(selectedProjectId, contextId, title);
      await contextsQuery.refetch();
    },
    [contextService, contextsQuery, ensureIdle, selectedProjectId],
  );
  const deleteContext = React.useCallback(
    async (contextId: string) => {
      if (!contextService || !selectedProjectId) return;
      ensureIdle();
      await contextService.deleteProjectContext(selectedProjectId, contextId);
      if (selectedContextId === contextId) newChat();
      await contextsQuery.refetch();
    },
    [contextService, contextsQuery, ensureIdle, newChat, selectedContextId, selectedProjectId],
  );

  const dependencyError = projectsQuery.error ?? agentsQuery.error ?? agentflowsQuery.error;
  const value = React.useMemo<WorkspaceContextValue>(
    () => ({
      projects,
      targets,
      contexts,
      messages,
      selectedProjectId,
      selectedTargetValue,
      selectedContextId,
      selectedProject,
      selectedTarget,
      permissionMode,
      agentMode,
      agentSuggestions,
      supportsAgentMode,
      isSuggestionsLoading: suggestionsQuery.isLoading,
      suggestionsError: suggestionsQuery.error ? getErrorMessage(suggestionsQuery.error) : null,
      isDependenciesLoading:
        projectsQuery.isLoading || agentsQuery.isLoading || agentflowsQuery.isLoading,
      isHistoryLoading: contextsQuery.isLoading,
      isChatLoading: contextDetailsQuery.isLoading,
      isExecuting,
      reconnectState,
      error: operationError ?? (dependencyError ? getErrorMessage(dependencyError) : null),
      contextService,
      filesService,
      selectProject,
      selectTarget: setSelectedTargetValue,
      setPermissionMode,
      setAgentMode,
      selectContext,
      newChat,
      sendMessage,
      stopExecution,
      clearCurrentContext,
      renameContext,
      deleteContext,
      refreshContexts: async () => {
        await contextsQuery.refetch();
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
      contexts,
      messages,
      selectedProjectId,
      selectedTargetValue,
      selectedContextId,
      selectedProject,
      selectedTarget,
      permissionMode,
      agentMode,
      agentSuggestions,
      supportsAgentMode,
      suggestionsQuery.isLoading,
      suggestionsQuery.error,
      projectsQuery,
      agentsQuery,
      agentflowsQuery,
      contextsQuery,
      contextDetailsQuery.isLoading,
      isExecuting,
      reconnectState,
      operationError,
      dependencyError,
      contextService,
      filesService,
      selectProject,
      selectContext,
      newChat,
      sendMessage,
      stopExecution,
      clearCurrentContext,
      renameContext,
      deleteContext,
    ],
  );

  return <WorkspaceContext.Provider value={value}>{children}</WorkspaceContext.Provider>;
}

export function useWorkspace(): WorkspaceContextValue {
  const value = React.useContext(WorkspaceContext);
  if (!value) throw new Error("useWorkspace must be used inside WorkspaceProvider.");
  return value;
}
