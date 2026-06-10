import React from "react";
import {
  KeyboardAvoidingView,
  Platform,
  ScrollView,
  Text,
  View,
} from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { createAgwApiClient, AgwApiError } from "../../api/agw-api-client";
import type {
  AgwAgent,
  AgwAgentflow,
  AgwContextDetails,
  AgwContextSummary,
  AgwMessage,
  AgwProject,
} from "../../api/agw-api-types";
import type { AgwLocalConfig } from "../../config/agw-config";
import { readLocalConfig, writeLocalConfig } from "../../config/config-store";
import { ChatPanel } from "./components/chat-panel";
import { Composer } from "./components/composer";
import { ConfigSetupSheet } from "./components/config-setup-sheet";
import { FilesPanel } from "./components/files-panel";
import { HistoryDrawer } from "./components/history-drawer";
import {
  executeWithWebSocket,
  mergeStreamingMessages,
  parseExecutionWsMessage,
  toExecutionWsUserInput,
} from "./lib/execution-ws";
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
    null,
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
  const [contexts, setContexts] = React.useState<AgwContextSummary[]>([]);
  const [currentContextId, setCurrentContextId] = React.useState<string | null>(null);
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
    null,
  );
  const chatScrollRef = React.useRef<ScrollView | null>(null);
  const isTurnFinishedMessage = React.useCallback(
    (message: AgwMessage): boolean => {
      if (message.role?.toLowerCase() !== "system") {
        return false;
      }

      return (
        message.author === "$agw-server" &&
        message.contents.some(
          (content) => content.additionalProperties?.type === "turn-finished",
        )
      );
    },
    [],
  );

  const apiClient = React.useMemo(
    () => (config ? createAgwApiClient(config) : null),
    [config],
  );

  const selectedProject = React.useMemo(
    () => projects.find((project) => project.id === selectedProjectId) ?? null,
    [projects, selectedProjectId],
  );

  const targets = React.useMemo(
    () =>
      buildAgwTargetOptions({
        projectId: selectedProjectId,
        agents,
        agentflows,
      }),
    [agentflows, agents, selectedProjectId],
  );

  const selectedTarget = React.useMemo(
    () =>
      targets.find(
        (target) => getTargetValue(target) === selectedTargetValue,
      ) ?? null,
    [selectedTargetValue, targets],
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
          error instanceof Error ? error.message : "Configuration is invalid.",
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
      setContexts([]);
      setCurrentContextId(null);
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
        setAgentflows(
          nextAgentflows.filter((agentflow) => agentflow.enable !== false),
        );
      } catch (error) {
        if (!isMounted) {
          return;
        }

        setProjects([]);
        setAgents([]);
        setAgentflows([]);
        setContexts([]);
        setCurrentContextId(null);
        setCurrentTaskId(null);
        setMessages([]);
        setDependenciesError(
          `Failed to load mobile data: ${getErrorMessage(error)}`,
        );
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
          project.name === DEFAULT_PROJECT_VALUE,
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
          target.type === "agent" && target.label === DEFAULT_AGENT_LABEL,
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
      setContexts([]);
      setCurrentContextId(null);
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
        const nextContexts = await apiClient!.getJson<AgwContextSummary[]>(
          `/api/projects/${encodeURIComponent(selectedProjectId!)}/contexts`,
        );

        if (!isMounted) {
          return;
        }

        setContexts(nextContexts);
        setCurrentContextId((currentContextIdValue) => {
          if (
            currentContextIdValue &&
            nextContexts.some((context) => context.contextId === currentContextIdValue)
          ) {
            return currentContextIdValue;
          }

          return nextContexts[0]?.contextId ?? null;
        });
        setCurrentTaskId((currentTaskIdValue) => {
          if (
            currentTaskIdValue &&
            nextContexts.some((context) => context.latestTaskId === currentTaskIdValue)
          ) {
            return currentTaskIdValue;
          }

          return nextContexts[0]?.latestTaskId ?? null;
        });
      } catch (error) {
        if (!isMounted) {
          return;
        }

        setContexts([]);
        setCurrentContextId(null);
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
    if (!apiClient || !selectedProjectId || !currentContextId) {
      setMessages([]);
      setChatError(null);
      setIsChatLoading(false);
      return;
    }

    let isMounted = true;

    async function loadContextMessages() {
      setIsChatLoading(true);
      setChatError(null);

      try {
        const contextDetails = await apiClient!.getJson<AgwContextDetails>(
          `/api/projects/${encodeURIComponent(
            selectedProjectId!,
          )}/contexts/${encodeURIComponent(currentContextId!)}`,
        );

        if (!isMounted) {
          return;
        }

        setCurrentTaskId(contextDetails.latestTaskId ?? null);
        setMessages(contextDetails.messages ?? []);
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

    loadContextMessages();

    return () => {
      isMounted = false;
    };
  }, [apiClient, currentContextId, selectedProjectId]);

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

  function selectContext(contextId: string) {
    const context = contexts.find((item) => item.contextId === contextId);
    setCurrentContextId(contextId);
    setCurrentTaskId(context?.latestTaskId ?? null);
    setExecutionError(null);
    setIsDrawerOpen(false);
  }

  function selectProject(projectId: string) {
    setSelectedProjectId(projectId);
    setCurrentContextId(null);
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
      !config ||
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
    setCurrentTaskId(executionTaskId);
    setMessages((currentMessages) => [...currentMessages, userMessage]);
    setIsExecuting(true);

    try {
      await executeWithWebSocket(
        config.serverDomain,
        selectedTarget.id,
        {
          projectId: selectedProjectId,
          taskId: executionTaskId,
          workspace: selectedProject?.workspace,
          settingContent: selectedProject?.extraSetting,
          agentType: selectedTarget.agentType,
          input: toExecutionWsUserInput(userMessage),
        },
        (data) => {
          const incomingMessage = parseExecutionWsMessage(data);
          if (!incomingMessage || incomingMessage.role === "user") {
            return;
          }

          setMessages((currentMessages) =>
            mergeStreamingMessages(currentMessages, [incomingMessage]),
          );

          if (isTurnFinishedMessage(incomingMessage)) {
            setIsExecuting(false);
          }
        },
      );

      setCurrentTaskId(executionTaskId);

      const latestContexts = await apiClient.getJson<AgwContextSummary[]>(
        `/api/projects/${encodeURIComponent(selectedProjectId)}/contexts`,
      );
      setContexts(latestContexts);
      setCurrentContextId(
        latestContexts.find((context) => context.latestTaskId === executionTaskId)?.contextId ??
          latestContexts[0]?.contextId ??
          currentContextId,
      );
    } catch (error) {
      setExecutionError(`Failed to send message: ${getErrorMessage(error)}`);
    } finally {
      setIsExecuting(false);
    }
  }

  async function clearCurrentContextRecords() {
    if (!apiClient || !selectedProjectId || !currentContextId || isExecuting) {
      return;
    }

    setExecutionError(null);

    try {
      await apiClient.deleteJson(
        `/api/projects/${encodeURIComponent(
          selectedProjectId,
        )}/contexts/${encodeURIComponent(currentContextId)}/clear-records`,
      );
      setMessages([]);
    } catch (error) {
      if (error instanceof AgwApiError && error.status === 404) {
        return;
      }

      setExecutionError(`Failed to clear chat: ${getErrorMessage(error)}`);
    }
  }

  function scrollChatToTop() {
    chatScrollRef.current?.scrollTo({ animated: true, y: 0 });
  }

  return (
    <KeyboardAvoidingView
      behavior={Platform.OS === "ios" ? "padding" : undefined}
      style={styles.phoneFrame}
    >
      <View style={{ flex: 1, padding: 16 }}>
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
                  scrollViewRef={chatScrollRef}
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
            <View style={{ paddingBottom: safeAreaInsets.bottom }}>
              <Composer
                disabled={
                  Boolean(dependenciesError) ||
                  !selectedProjectId ||
                  !selectedTarget
                }
                isSending={isExecuting}
                message={composerText}
                onClear={clearCurrentContextRecords}
                onMessageChange={setComposerText}
                onScrollToTop={scrollChatToTop}
                onSend={sendMessage}
                safeBottom={safeAreaInsets.bottom}
              />
            </View>
          </>
        )}
        {configLoadState !== "loading" && isDrawerOpen ? (
          <HistoryDrawer
            contexts={contexts}
            currentContextId={currentContextId}
            historyError={historyError}
            isSettingsOpen={isSettingsOpen}
            isLoadingHistory={isHistoryLoading}
            onClose={closeDrawer}
            onCloseSettings={closeSettings}
            onOpenSettings={openSettings}
            onProjectSelect={selectProject}
            onSaveSettings={saveConfig}
            onContextSelect={selectContext}
            onTargetSelect={selectTarget}
            projects={projects}
            safeBottom={safeAreaInsets.bottom}
            safeTop={safeAreaInsets.top}
            selectedProjectId={selectedProjectId}
            selectedTargetValue={selectedTargetValue}
            settingsConfig={config}
            targets={targets}
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
    </KeyboardAvoidingView>
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
