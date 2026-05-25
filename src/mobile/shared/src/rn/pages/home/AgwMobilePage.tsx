import React from "react";
import { Text, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { createAgwApiClient, AgwApiError } from "../../api/agw-api-client";
import type {
  AgwAgent,
  AgwAgentflow,
  AgwExecutionResponse,
  AgwMessage,
  AgwProject,
  AgwTaskDetails,
  AgwTaskSummary,
} from "../../api/agw-api-types";
import type { AgwLocalConfig } from "../../config/agw-config";
import { readLocalConfig, writeLocalConfig } from "../../config/config-store";
import { ChatPanel } from "./components/chat-panel";
import { Composer } from "./components/composer";
import { ConfigSetupSheet } from "./components/config-setup-sheet";
import { FilesPanel } from "./components/files-panel";
import { HistoryDrawer } from "./components/history-drawer";
import {
  DEFAULT_AGENT_LABEL,
  DEFAULT_PROJECT_VALUE,
} from "./lib/default-selections";
import { buildAgwTargetOptions, getTargetValue } from "./lib/target-options";
import { styles } from "./components/styles";
import { TopBar } from "./components/top-bar";
import type { AgwTabName } from "./components/types";

export type { AgwTabName } from "./components/types";

type AgwMobilePageProps = {
  initialSettingsOpen?: boolean;
  initialTab?: AgwTabName;
};

function AgwMobilePage({
  initialSettingsOpen = false,
  initialTab = "chat",
}: AgwMobilePageProps): React.JSX.Element {
  const safeAreaInsets = useSafeAreaInsets();
  const [activeTab, setActiveTab] = React.useState<AgwTabName>(initialTab);
  const [config, setConfig] = React.useState<AgwLocalConfig | null>(null);
  const [configLoadState, setConfigLoadState] = React.useState<
    "loading" | "ready" | "missing"
  >("loading");
  const [configLoadError, setConfigLoadError] = React.useState<string | null>(
    null
  );
  const [isDrawerOpen, setIsDrawerOpen] = React.useState(initialSettingsOpen);
  const [isSettingsOpen, setIsSettingsOpen] =
    React.useState(initialSettingsOpen);
  const [projects, setProjects] = React.useState<AgwProject[]>([]);
  const [agents, setAgents] = React.useState<AgwAgent[]>([]);
  const [agentflows, setAgentflows] = React.useState<AgwAgentflow[]>([]);
  const [selectedProjectId, setSelectedProjectId] = React.useState<
    string | null
  >(null);
  const [selectedTargetValue, setSelectedTargetValue] = React.useState<
    string | null
  >(null);
  const [tasks, setTasks] = React.useState<AgwTaskSummary[]>([]);
  const [currentTaskId, setCurrentTaskId] = React.useState<string | null>(null);
  const [messages, setMessages] = React.useState<AgwMessage[]>([]);
  const [isDependenciesLoading, setIsDependenciesLoading] =
    React.useState(false);
  const [dependenciesError, setDependenciesError] = React.useState<
    string | null
  >(null);
  const [isHistoryLoading, setIsHistoryLoading] = React.useState(false);
  const [historyError, setHistoryError] = React.useState<string | null>(null);
  const [isChatLoading, setIsChatLoading] = React.useState(false);
  const [chatError, setChatError] = React.useState<string | null>(null);
  const [composerText, setComposerText] = React.useState("");
  const [isExecuting, setIsExecuting] = React.useState(false);
  const [executionError, setExecutionError] = React.useState<string | null>(
    null
  );

  const apiClient = React.useMemo(
    () => (config ? createAgwApiClient(config) : null),
    [config]
  );

  const selectedProject = React.useMemo(
    () => projects.find((project) => project.id === selectedProjectId) ?? null,
    [projects, selectedProjectId]
  );

  const targets = React.useMemo(
    () =>
      buildAgwTargetOptions({
        projectId: selectedProjectId,
        agents,
        agentflows,
      }),
    [agentflows, agents, selectedProjectId]
  );

  const selectedTarget = React.useMemo(
    () =>
      targets.find((target) => getTargetValue(target) === selectedTargetValue) ??
      null,
    [selectedTargetValue, targets]
  );

  React.useEffect(() => {
    let isMounted = true;

    async function loadConfig() {
      try {
        const storedConfig = await readLocalConfig();

        if (!isMounted) {
          return;
        }

        setConfig(storedConfig);
        setConfigLoadState(storedConfig ? "ready" : "missing");
        setConfigLoadError(null);
      } catch (error) {
        if (!isMounted) {
          return;
        }

        setConfig(null);
        setConfigLoadState("missing");
        setConfigLoadError(
          error instanceof Error ? error.message : "Configuration is invalid."
        );
      }
    }

    loadConfig();

    return () => {
      isMounted = false;
    };
  }, []);

  async function saveConfig(nextConfig: AgwLocalConfig) {
    await writeLocalConfig(nextConfig);
    setConfig(nextConfig);
    setConfigLoadState("ready");
    setConfigLoadError(null);
  }

  React.useEffect(() => {
    if (!apiClient) {
      setProjects([]);
      setAgents([]);
      setAgentflows([]);
      setTasks([]);
      setCurrentTaskId(null);
      setMessages([]);
      return;
    }

    let isMounted = true;

    async function loadDependencies() {
      setIsDependenciesLoading(true);
      setDependenciesError(null);

      try {
        const [nextProjects, nextAgents, nextAgentflows] = await Promise.all([
          apiClient!.getJson<AgwProject[]>("/api/projects"),
          apiClient!.getJson<AgwAgent[]>("/api/agents"),
          apiClient!.getJson<AgwAgentflow[]>("/api/agentflows"),
        ]);

        if (!isMounted) {
          return;
        }

        setProjects(nextProjects.filter((project) => project.enable));
        setAgents(nextAgents);
        setAgentflows(nextAgentflows.filter((agentflow) => agentflow.enable !== false));
      } catch (error) {
        if (!isMounted) {
          return;
        }

        setProjects([]);
        setAgents([]);
        setAgentflows([]);
        setTasks([]);
        setCurrentTaskId(null);
        setMessages([]);
        setDependenciesError(`Failed to load mobile data: ${getErrorMessage(error)}`);
      } finally {
        if (isMounted) {
          setIsDependenciesLoading(false);
        }
      }
    }

    loadDependencies();

    return () => {
      isMounted = false;
    };
  }, [apiClient]);

  React.useEffect(() => {
    setSelectedProjectId((currentProjectId) => {
      if (
        currentProjectId &&
        projects.some((project) => project.id === currentProjectId)
      ) {
        return currentProjectId;
      }

      const defaultProject = projects.find(
        (project) =>
          project.id === DEFAULT_PROJECT_VALUE ||
          project.name === DEFAULT_PROJECT_VALUE
      );

      return defaultProject?.id ?? projects[0]?.id ?? null;
    });
  }, [projects]);

  React.useEffect(() => {
    setSelectedTargetValue((currentTargetValue) => {
      if (
        currentTargetValue &&
        targets.some((target) => getTargetValue(target) === currentTargetValue)
      ) {
        return currentTargetValue;
      }

      const defaultTarget = targets.find(
        (target) =>
          target.type === "agent" && target.label === DEFAULT_AGENT_LABEL
      );

      return defaultTarget
        ? getTargetValue(defaultTarget)
        : targets[0]
          ? getTargetValue(targets[0])
          : null;
    });
  }, [targets]);

  React.useEffect(() => {
    if (!apiClient || !selectedProjectId) {
      setTasks([]);
      setCurrentTaskId(null);
      setHistoryError(null);
      setIsHistoryLoading(false);
      return;
    }

    let isMounted = true;

    async function loadHistory() {
      setIsHistoryLoading(true);
      setHistoryError(null);

      try {
        const nextTasks = await apiClient!.getJson<AgwTaskSummary[]>(
          `/api/projects/${encodeURIComponent(selectedProjectId!)}/tasks`
        );

        if (!isMounted) {
          return;
        }

        setTasks(nextTasks);
        setCurrentTaskId((currentTaskIdValue) => {
          if (
            currentTaskIdValue &&
            nextTasks.some((task) => task.id === currentTaskIdValue)
          ) {
            return currentTaskIdValue;
          }

          return nextTasks[0]?.id ?? null;
        });
      } catch (error) {
        if (!isMounted) {
          return;
        }

        setTasks([]);
        setCurrentTaskId(null);
        setHistoryError(`Failed to load history: ${getErrorMessage(error)}`);
      } finally {
        if (isMounted) {
          setIsHistoryLoading(false);
        }
      }
    }

    loadHistory();

    return () => {
      isMounted = false;
    };
  }, [apiClient, selectedProjectId]);

  React.useEffect(() => {
    if (!apiClient || !selectedProjectId || !currentTaskId) {
      setMessages([]);
      setChatError(null);
      setIsChatLoading(false);
      return;
    }

    let isMounted = true;

    async function loadTaskMessages() {
      setIsChatLoading(true);
      setChatError(null);

      try {
        const taskDetails = await apiClient!.getJson<AgwTaskDetails>(
          `/api/projects/${encodeURIComponent(
            selectedProjectId!
          )}/tasks/${encodeURIComponent(currentTaskId!)}`
        );

        if (!isMounted) {
          return;
        }

        setMessages(taskDetails.messages ?? []);
      } catch (error) {
        if (!isMounted) {
          return;
        }

        setMessages([]);
        setChatError(`Failed to load chat: ${getErrorMessage(error)}`);
      } finally {
        if (isMounted) {
          setIsChatLoading(false);
        }
      }
    }

    loadTaskMessages();

    return () => {
      isMounted = false;
    };
  }, [apiClient, currentTaskId, selectedProjectId]);

  function openSettings() {
    setIsDrawerOpen(true);
    setIsSettingsOpen(true);
  }

  function closeSettings() {
    setIsSettingsOpen(false);
  }

  function closeDrawer() {
    setIsDrawerOpen(false);
    setIsSettingsOpen(false);
  }

  function selectTask(taskId: string) {
    setCurrentTaskId(taskId);
    setExecutionError(null);
    setIsDrawerOpen(false);
  }

  function selectProject(projectId: string) {
    setSelectedProjectId(projectId);
    setCurrentTaskId(null);
    setMessages([]);
    setExecutionError(null);
  }

  function selectTarget(targetValue: string) {
    setSelectedTargetValue(targetValue);
    setExecutionError(null);
  }

  async function sendMessage() {
    const input = composerText.trim();
    if (
      !apiClient ||
      !selectedProjectId ||
      !selectedTarget ||
      !input ||
      isExecuting
    ) {
      return;
    }

    const executionTaskId = currentTaskId ?? createUuid();
    const userMessage = createUserMessage(input);

    setComposerText("");
    setExecutionError(null);
    setMessages((currentMessages) => [...currentMessages, userMessage]);
    setIsExecuting(true);

    try {
      const result = await apiClient.postJson<AgwExecutionResponse>(
        `/api/executions/${encodeURIComponent(selectedTarget.id)}/execute`,
        {
          agentType: selectedTarget.agentType,
          input,
          projectId: selectedProjectId,
          taskId: executionTaskId,
        }
      );

      setCurrentTaskId(result.taskId ?? executionTaskId);
      if (result.messages.length > 0) {
        setMessages((currentMessages) =>
          mergeMessages(currentMessages, result.messages)
        );
      }

      const latestTasks = await apiClient.getJson<AgwTaskSummary[]>(
        `/api/projects/${encodeURIComponent(selectedProjectId)}/tasks`
      );
      setTasks(latestTasks);
    } catch (error) {
      setExecutionError(`Failed to send message: ${getErrorMessage(error)}`);
    } finally {
      setIsExecuting(false);
    }
  }

  return (
    <View style={styles.root}>
      <View style={styles.phoneFrame}>
        {configLoadState === "loading" ? (
          <View style={styles.loadingPanel}>
            <Text style={styles.loadingText}>Loading Configuration</Text>
          </View>
        ) : (
          <>
            <TopBar
              activeTab={activeTab}
              onOpenDrawer={() => setIsDrawerOpen(true)}
              onTabChange={setActiveTab}
              safeTop={safeAreaInsets.top}
            />
            <View style={styles.mainPanel}>
              {activeTab === "chat" ? (
                <ChatPanel
                  error={dependenciesError ?? chatError ?? executionError}
                  isLoading={isDependenciesLoading || isChatLoading}
                  messages={messages}
                />
              ) : (
                <FilesPanel
                  apiClient={apiClient}
                  dependenciesError={dependenciesError}
                  isDependenciesLoading={isDependenciesLoading}
                  workspace={selectedProject?.workspace}
                />
              )}
            </View>
            <Composer
              disabled={
                Boolean(dependenciesError) ||
                !selectedProjectId ||
                !selectedTarget
              }
              isSending={isExecuting}
              message={composerText}
              onMessageChange={setComposerText}
              onSend={sendMessage}
              safeBottom={safeAreaInsets.bottom}
            />
          </>
        )}
        {configLoadState !== "loading" && isDrawerOpen ? (
          <HistoryDrawer
            currentTaskId={currentTaskId}
            historyError={historyError}
            isSettingsOpen={isSettingsOpen}
            isLoadingHistory={isHistoryLoading}
            onClose={closeDrawer}
            onCloseSettings={closeSettings}
            onOpenSettings={openSettings}
            onProjectSelect={selectProject}
            onSaveSettings={saveConfig}
            onTaskSelect={selectTask}
            onTargetSelect={selectTarget}
            projects={projects}
            safeBottom={safeAreaInsets.bottom}
            safeTop={safeAreaInsets.top}
            selectedProjectId={selectedProjectId}
            selectedTargetValue={selectedTargetValue}
            settingsConfig={config}
            targets={targets}
            tasks={tasks}
          />
        ) : null}
        {configLoadState === "missing" ? (
          <ConfigSetupSheet
            initialError={configLoadError}
            onSave={saveConfig}
            safeBottom={safeAreaInsets.bottom}
            safeTop={safeAreaInsets.top}
          />
        ) : null}
      </View>
    </View>
  );
}

export default AgwMobilePage;

function createUserMessage(content: string): AgwMessage {
  return {
    author: "$agw",
    contents: [
      {
        content,
        type: "TextContent",
      },
    ],
    messageId: createUuid(),
    role: "user",
  };
}

function mergeMessages(
  currentMessages: AgwMessage[],
  incomingMessages: AgwMessage[]
): AgwMessage[] {
  const merged = [...currentMessages];
  const indexesById = new Map(
    merged.map((message, index) => [message.messageId, index])
  );

  incomingMessages.forEach((message) => {
    const existingIndex = indexesById.get(message.messageId);

    if (existingIndex === undefined) {
      indexesById.set(message.messageId, merged.length);
      merged.push(message);
      return;
    }

    merged[existingIndex] = message;
  });

  return merged;
}

function createUuid(): string {
  return "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g, (token) => {
    const random = Math.floor(Math.random() * 16);
    const value = token === "x" ? random : (random & 0x3) | 0x8;
    return value.toString(16);
  });
}

function getErrorMessage(error: unknown): string {
  if (error instanceof AgwApiError) {
    if (isRecord(error.body) && typeof error.body.message === "string") {
      return error.body.message;
    }

    if (isRecord(error.body) && typeof error.body.detail === "string") {
      return error.body.detail;
    }
  }

  return error instanceof Error ? error.message : "Unknown error.";
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
