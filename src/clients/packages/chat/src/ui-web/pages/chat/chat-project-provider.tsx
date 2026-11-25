"use client";

import * as React from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { toast } from "sonner";
import { apiGet, getApiErrorMessage, type components } from "@agw/api";
import { useQuery } from "@agw/components/query";
import { getProjectDirectories, type ProjectDirectory, type LineComment } from "@agw/projects";
import {
  executionSessionManager,
  getExecutionReconnectProgress,
  type ExecutionReconnectState,
} from "@agw/chat-runtime";
import { buildChatHref } from "../../../lib/chat-route";
import {
  conversationComposerStorage,
  type ConversationComposerScope,
} from "../../../lib/chat/conversation-composer-storage";
import { conversationImageDrafts } from "../../../lib/chat/conversation-image-drafts";
import { useConversationStatuses } from "../../conversation-statuses";
import { useExecutionPlatform } from "../../execution-platform";
import { ExecutionReconnectingDialog } from "../../components/message/execution-reconnecting-dialog";
import { notifyImageDraftError } from "../../components/message/use-conversation-image-draft";
import { buildChatTargetOptions } from "./lib/target-options";
import { chatSettingsStorage } from "./settings-storage";
import type { EnvVar } from "./types";

export type ChatWorkspaceProject = {
  id: string;
  name: string;
  workspace?: string | null;
  additionalDirectories?: ProjectDirectory[] | null;
};

type Agent = {
  id: string;
  displayName: string;
  name: string;
  enable: boolean;
  resultFormat?: components["schemas"]["ResultFormat"];
};
type Agentflow = { id: string; name: string; enable: boolean };
type FileChange = { kind: "written" | "reset"; directoryId: string | null; path: string };
type ReconnectReport = { state: ExecutionReconnectState; retry: () => Promise<void> };
export type ChatSettingsDraft = { envVars: EnvVar[]; resultOnly: boolean };
export type ChatProjectProviderProps = {
  routeBasePath: string;
  activeConversationId?: string | null;
  children: React.ReactNode;
};
const DEFAULT_PROJECT_VALUE = "default-built-in";

function readSettings(projectId: string | null): ChatSettingsDraft {
  const value = projectId ? chatSettingsStorage.get(projectId) : {};
  return { envVars: value.envVars ?? [], resultOnly: value.resultOnly ?? false };
}

function useProjectSelection(routeBasePath: string, projects: ChatWorkspaceProject[]) {
  const router = useRouter();
  const search = useSearchParams();
  const queryProjectId = search.get("projectId");
  const [requestedProjectId, setRequestedProjectId] = React.useState(queryProjectId);
  const [previousQuery, setPreviousQuery] = React.useState(queryProjectId);
  if (previousQuery !== queryProjectId) {
    setPreviousQuery(queryProjectId);
    setRequestedProjectId(queryProjectId);
  }

  const selectedProject =
    projects.find((project) => project.id === requestedProjectId) ??
    projects.find(
      (project) => project.id === DEFAULT_PROJECT_VALUE || project.name === DEFAULT_PROJECT_VALUE,
    ) ??
    projects[0] ??
    null;
  const selectedProjectId = selectedProject?.id ?? null;
  const selectProject = React.useCallback(
    (id: string) => {
      if (id === selectedProjectId) return;
      setRequestedProjectId(id);
      router.replace(buildChatHref(routeBasePath, { projectId: id, conversationId: null }), {
        scroll: false,
      });
    },
    [routeBasePath, router, selectedProjectId],
  );
  return { selectedProject, selectedProjectId, selectProject };
}

function useChatDependencies() {
  const projectsQuery = useQuery({
    queryKey: ["projects"],
    queryFn: async () => (await apiGet("/api/projects")) as ChatWorkspaceProject[],
  });
  const agentsQuery = useQuery({
    queryKey: ["agents"],
    queryFn: async () => (await apiGet("/api/agents")) as Agent[],
  });
  const agentflowsQuery = useQuery({
    queryKey: ["agentflows"],
    queryFn: async () => (await apiGet("/api/agentflows")) as Agentflow[],
  });
  const dependencyErrorMessage =
    projectsQuery.isError || agentsQuery.isError || agentflowsQuery.isError
      ? getApiErrorMessage(projectsQuery.error ?? agentsQuery.error ?? agentflowsQuery.error)
      : null;
  return {
    projects: projectsQuery.data ?? [],
    agents: agentsQuery.data,
    agentflows: agentflowsQuery.data,
    projectsLoaded: projectsQuery.isSuccess && projectsQuery.fetchStatus === "idle",
    dependencyErrorMessage,
  };
}

function useProjectDirectories(selectedProject: ChatWorkspaceProject | null) {
  const selectedProjectId = selectedProject?.id ?? null;
  const projectDirectories = React.useMemo(
    () => (selectedProject ? getProjectDirectories(selectedProject) : []),
    [selectedProject],
  );
  const searchDirectoryIds = React.useMemo(
    () => projectDirectories.map((directory) => directory.id),
    [projectDirectories],
  );
  const [directories, setDirectories] = React.useState(new Map<string, string | null>());
  const selectedDirectory =
    projectDirectories.find(
      (directory) => selectedProjectId && directory.id === directories.get(selectedProjectId),
    ) ?? projectDirectories[0];
  const selectDirectory = React.useCallback(
    (id: string | null) => {
      if (selectedProjectId)
        setDirectories((current) =>
          current.get(selectedProjectId) === id
            ? current
            : new Map(current).set(selectedProjectId, id),
        );
    },
    [selectedProjectId],
  );

  return {
    projectDirectories,
    searchDirectoryIds,
    selectedDirectory,
    selectDirectory,
    resolvedWorkspace: selectedDirectory?.path ?? "",
  };
}

function useProjectComments(selectedProjectId: string | null) {
  const [projectComments, setProjectComments] = React.useState(new Map<string, LineComment[]>());
  const comments = (selectedProjectId && projectComments.get(selectedProjectId)) || [];
  const setComments = React.useCallback(
    (update: React.SetStateAction<LineComment[]>) => {
      if (!selectedProjectId) return;
      setProjectComments((current) =>
        new Map(current).set(
          selectedProjectId,
          typeof update === "function" ? update(current.get(selectedProjectId) ?? []) : update,
        ),
      );
    },
    [selectedProjectId],
  );
  const removePendingFileComments = React.useCallback(
    (ids: readonly string[]) => {
      const removed = new Set(ids);
      setComments((previous) => previous.filter((comment) => !removed.has(comment.id)));
    },
    [setComments],
  );
  return { comments, setComments, removePendingFileComments };
}

function useProjectFileChanges(directoryId: string | null) {
  const [onlyDiff, setOnlyDiff] = React.useState(false);
  const [fileTreeRefreshSignal, refreshFiles] = React.useReducer((n: number) => n + 1, 0);
  const listeners = React.useRef(new Set<(change: FileChange) => void>());
  const subscribeFileChanges = React.useCallback((listener: (change: FileChange) => void) => {
    listeners.current.add(listener);
    return () => {
      listeners.current.delete(listener);
    };
  }, []);
  const notifyFileWritten = React.useCallback((directoryId: string | null, path: string) => {
    refreshFiles();
    for (const listener of listeners.current) listener({ kind: "written", directoryId, path });
  }, []);
  const notifyFileReset = React.useCallback(
    (path: string) => {
      for (const listener of listeners.current) listener({ kind: "reset", directoryId, path });
    },
    [directoryId],
  );

  return {
    onlyDiff,
    setOnlyDiff,
    recursiveMode: true,
    fileTreeRefreshSignal,
    subscribeFileChanges,
    notifyFileWritten,
    notifyFileReset,
  };
}

function useProjectConversations(scope: ConversationComposerScope | null) {
  const [conversationListRefreshSignal, refreshConversations] = React.useReducer(
    (n: number) => n + 1,
    0,
  );
  const handleConversationDeleted = React.useCallback(
    (conversationId: string) => {
      if (!scope) return;
      executionSessionManager.conversationStatuses.remove(scope, conversationId);
      conversationComposerStorage.remove(scope, { conversationId });
      void conversationImageDrafts.remove(scope, { conversationId }).catch(notifyImageDraftError);
      void executionSessionManager.discard({ ...scope, conversationId });
    },
    [scope],
  );
  const handleProjectConversationsCleared = React.useCallback(
    (keepDraft?: { draftId: string }) => {
      if (!scope) return;
      executionSessionManager.conversationStatuses.removeScope(scope);
      conversationComposerStorage.removeConversations(scope, keepDraft);
      void conversationImageDrafts
        .removeConversations(scope, keepDraft)
        .catch(notifyImageDraftError);
      void executionSessionManager.discardProject(scope);
    },
    [scope],
  );

  return {
    handleConversationDeleted,
    handleProjectConversationsCleared,
    conversationListRefreshSignal,
    refreshConversations,
  };
}

function useProjectSettings(selectedProjectId: string | null) {
  const [settingsRevision, updateSettings] = React.useReducer((n: number) => n + 1, 0);
  const settings = React.useMemo(
    () => readSettings(selectedProjectId),
    [settingsRevision, selectedProjectId],
  );
  const environmentVariables = React.useMemo(
    () =>
      Object.fromEntries(
        settings.envVars.filter((v) => v.key.trim()).map((v) => [v.key.trim(), v.value]),
      ),
    [settings],
  );
  const saveSettings = React.useCallback(
    (draft: ChatSettingsDraft) => {
      if (!selectedProjectId) {
        toast.error("Please select a project.");
        return false;
      }
      chatSettingsStorage.set(selectedProjectId, draft);
      updateSettings();
      toast.success("Chat settings saved");
      return true;
    },
    [selectedProjectId],
  );
  return {
    environmentVariables,
    saveSettings,
    getSettingsDraft: readSettings,
    resultOnly: settings.resultOnly,
  };
}

function useReconnectReports() {
  const [reports, setReports] = React.useState(new Map<string, ReconnectReport>());
  const reportReconnectState = React.useCallback(
    (id: string, state: ExecutionReconnectState | null, retry: () => Promise<void>) => {
      setReports((current) => {
        const previous = current.get(id);
        if (state ? previous?.state === state && previous.retry === retry : !previous)
          return current;
        const next = new Map(current);
        next.delete(id);
        if (state) next.set(id, { state, retry });
        return next;
      });
    },
    [],
  );
  const reconnect = Array.from(reports.values()).at(-1);
  return {
    reconnect,
    reportReconnectState,
    isReconnecting: getExecutionReconnectProgress(reconnect?.state ?? null) !== null,
  };
}

function useProjectModel({ routeBasePath, activeConversationId }: ChatProjectProviderProps) {
  const serverId = useExecutionPlatform().serverId;
  const search = useSearchParams();
  const { agentflows, ...dependencies } = useChatDependencies();
  const { projects, agents } = dependencies;
  const selection = useProjectSelection(routeBasePath, projects);
  const { selectedProject, selectedProjectId: projectId } = selection;
  const scope = React.useMemo(
    () => (projectId ? { serverId, projectId } : null),
    [serverId, projectId],
  );
  const directories = useProjectDirectories(selectedProject);
  const targetOptions = React.useMemo(
    () => buildChatTargetOptions({ projectId, agents: agents ?? [], agentflows: agentflows ?? [] }),
    [projectId, agents, agentflows],
  );
  const conversationId =
    activeConversationId === undefined ? search.get("conversationId") : activeConversationId;
  const conversationStatuses = useConversationStatuses({ serverId, projectId, conversationId });
  return {
    ...dependencies,
    ...selection,
    ...directories,
    targetOptions,
    conversationStatuses,
    ...useProjectComments(projectId),
    ...useProjectFileChanges(directories.selectedDirectory?.id ?? null),
    ...useProjectConversations(scope),
    ...useProjectSettings(projectId),
    ...useReconnectReports(),
  };
}

export type ChatProjectContextValue = Omit<ReturnType<typeof useProjectModel>, "reconnect">;
const ChatProjectContext = React.createContext<ChatProjectContextValue | null>(null);

export function useChatProject(): ChatProjectContextValue {
  const value = React.useContext(ChatProjectContext);
  if (!value) throw new Error("useChatProject must be used inside ChatProjectProvider.");
  return value;
}

export function ChatProjectProvider(props: ChatProjectProviderProps) {
  const { reconnect, ...model } = useProjectModel(props);
  return (
    <ChatProjectContext value={model}>
      {props.children}
      {model.isReconnecting && reconnect && (
        <ExecutionReconnectingDialog
          state={reconnect.state}
          onRetry={() => {
            void reconnect.retry().catch((error: unknown) => {
              console.error("Failed to retry the conversation connection.", error);
              toast.error(getApiErrorMessage(error));
            });
          }}
        />
      )}
    </ChatProjectContext>
  );
}
