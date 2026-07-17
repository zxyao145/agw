"use client";

import * as React from "react";
import { FileText, PanelLeftClose, PanelLeftOpen, Plus, Settings, Trash2 } from "lucide-react";
import { useQuery } from "@agw/components/query";
import { useRouter, useSearchParams } from "next/navigation";
import { toast } from "sonner";

import { getFileDiff, readFile, type GitDiffResponse } from "@agw/projects";
import { apiGet } from "@agw/api";
import { getProjectContextDetails, type ContextSummary } from "@agw/projects";
import { AgentSelector, type AgentSelection } from "../../components/agent-selector";
import { Explorer, FileContent } from "@agw/projects";
import type { LineComment } from "@agw/projects";
import { Chat, type ChatSessionSeed } from "../../components/message/chat";
import { ConversationList } from "@agw/projects";
import { Button } from "@agw/components";
import { Card } from "@agw/components";
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from "@agw/components";
import { Drawer, DrawerContent, DrawerHeader, DrawerTitle } from "@agw/components";
import { Input } from "@agw/components";
import { Label } from "@agw/components";
import { SearchableSelect, type SearchableSelectOption } from "@agw/components";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@agw/components";
import { EMPTY_TOKEN_USAGE } from "@agw/api";
import { buildChatHref, type ChatRouteBasePath } from "../../../lib/chat-route";
import { cn } from "@agw/components";
import type { AiMessage } from "@agw/api";
import { chatSettingsStorage } from "./settings-storage";
import ColResizeSplit from "./components/split-layout";
import {
  CHAT_SETTINGS_DIALOG_BODY_CLASS_NAME,
  CHAT_SETTINGS_DIALOG_CONTENT_CLASS_NAME,
} from "./lib/chat-settings";
import {
  getChatRouteSessionAction,
  getContextHydrationKey,
  getRouteHydrationKey,
} from "./lib/session-routing";
import {
  buildChatTargetOptions,
  getTargetValue,
  getTargetValueFromMetadata,
} from "./lib/target-options";
import type { ChatProjectSettingsStorageValues, ChatTargetOption, EnvVar } from "./types";
import { getApiErrorMessage } from "@agw/api";

type ProjectDto = {
  id: string;
  name: string;
  workspace?: string | null;
};

type AgentDto = {
  id: string;
  displayName: string;
  name: string;
};

type AgentflowDto = {
  id: string;
  name: string;
};

const DEFAULT_PROJECT_VALUE = "default-built-in";
const DEFAULT_AGENT_LABEL = "Hello";

export type ChatWorkspaceProps = {
  routeBasePath: ChatRouteBasePath;
  showProjectSelect: boolean;
  compactToolbar?: boolean;
};

function getRestoredTargetValue(messages: AiMessage[]): string | null {
  for (let messageIndex = messages.length - 1; messageIndex >= 0; messageIndex -= 1) {
    const message = messages[messageIndex];
    if (message.role !== "user") {
      continue;
    }

    for (let contentIndex = message.contents.length - 1; contentIndex >= 0; contentIndex -= 1) {
      const content = message.contents[contentIndex];
      const restoredValue = getTargetValueFromMetadata(
        content.additionalProperties?.targetType,
        content.additionalProperties?.targetId,
      );

      if (restoredValue) {
        return restoredValue;
      }
    }
  }

  return null;
}

function clearLegacyChatSettingsUrl(): void {
  if (typeof window === "undefined") {
    return;
  }

  const searchParams = new URLSearchParams(window.location.search);
  if (!searchParams.has("settings") && !window.location.hash) {
    return;
  }

  searchParams.delete("settings");
  const nextSearch = searchParams.toString();
  const nextUrl = `${window.location.pathname}${nextSearch ? `?${nextSearch}` : ""}`;
  window.history.replaceState(window.history.state, "", nextUrl);
}

type ChatSettingsDraft = {
  envVars: EnvVar[];
};

function normalizeEnvVars(envVars: EnvVar[]): EnvVar[] {
  return envVars
    .map((envVar) => ({
      key: envVar.key.trim(),
      value: envVar.value,
    }))
    .filter((envVar) => envVar.key.length > 0 || envVar.value.trim().length > 0);
}

function areEnvVarsEqual(left: EnvVar[], right: EnvVar[]): boolean {
  if (left.length !== right.length) {
    return false;
  }

  return left.every((envVar, index) => {
    const rightEnvVar = right[index];
    return envVar.key === rightEnvVar.key && envVar.value === rightEnvVar.value;
  });
}

type ChatSettingsDialogProps = {
  selectedProjectId: string | null;
  getDraft: (projectId: string | null) => ChatSettingsDraft;
  onSave: (draft: ChatSettingsDraft) => boolean;
};

function ChatSettingsDialog({ selectedProjectId, getDraft, onSave }: ChatSettingsDialogProps) {
  const [open, setOpen] = React.useState(false);
  const [draftEnvVars, setDraftEnvVars] = React.useState<EnvVar[]>([]);

  React.useEffect(() => {
    if (!open) {
      return;
    }

    const draft = getDraft(selectedProjectId);
    setDraftEnvVars(draft.envVars);
  }, [getDraft, open, selectedProjectId]);

  const handleAddEnvVar = () => {
    setDraftEnvVars((current) => [...current, { key: "", value: "" }]);
  };

  const handleRemoveEnvVar = (index: number) => {
    setDraftEnvVars((current) => current.filter((_, currentIndex) => currentIndex !== index));
  };

  const handleUpdateEnvVar = (index: number, field: keyof EnvVar, value: string) => {
    setDraftEnvVars((current) =>
      current.map((envVar, currentIndex) =>
        currentIndex === index ? { ...envVar, [field]: value } : envVar,
      ),
    );
  };

  const handleSave = () => {
    const didSave = onSave({
      envVars: normalizeEnvVars(draftEnvVars),
    });

    if (didSave) {
      setOpen(false);
    }
  };

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger asChild>
        <Button
          variant="ghost"
          size="sm"
          className="cursor-pointer"
          aria-label="Open chat settings"
          disabled={!selectedProjectId}
        >
          <Settings className="h-4 w-4" />
        </Button>
      </DialogTrigger>
      <DialogContent size="md" className={CHAT_SETTINGS_DIALOG_CONTENT_CLASS_NAME}>
        <DialogHeader>
          <DialogTitle>Chat Settings</DialogTitle>
          <DialogDescription>
            Configure execution settings for the currently selected project.
          </DialogDescription>
        </DialogHeader>

        <div className={CHAT_SETTINGS_DIALOG_BODY_CLASS_NAME}>
          <div className="grid gap-4 py-2">
            <div className="grid gap-2">
              <div className="flex items-center justify-between">
                <Label>Environment Variables</Label>
                <Button type="button" variant="ghost" size="sm" onClick={handleAddEnvVar}>
                  <Plus className="h-4 w-4" />
                  Add
                </Button>
              </div>

              {draftEnvVars.length === 0 ? (
                <div className="rounded-md border border-dashed px-3 py-4 text-sm text-muted-foreground">
                  No environment variables configured.
                </div>
              ) : (
                <div className="rounded-md border">
                  <div className="grid grid-cols-12 gap-2 border-b bg-muted/50 p-2 text-xs font-medium text-muted-foreground">
                    <div className="col-span-5">Key</div>
                    <div className="col-span-6">Value</div>
                    <div className="col-span-1" />
                  </div>
                  {draftEnvVars.map((envVar, index) => (
                    <div
                      key={`${selectedProjectId ?? "chat"}-env-${index}`}
                      className="grid grid-cols-12 gap-2 border-b p-2 last:border-b-0"
                    >
                      <Input
                        value={envVar.key}
                        onChange={(event) => handleUpdateEnvVar(index, "key", event.target.value)}
                        placeholder="KEY"
                        className="col-span-5 h-8 text-xs md:text-xs"
                      />
                      <Input
                        value={envVar.value}
                        onChange={(event) => handleUpdateEnvVar(index, "value", event.target.value)}
                        placeholder="value"
                        className="col-span-6 h-8 text-xs md:text-xs"
                      />
                      <div className="col-span-1 flex items-center">
                        <Button
                          type="button"
                          variant="ghost"
                          size="sm"
                          onClick={() => handleRemoveEnvVar(index)}
                          className="h-8 w-8 p-0"
                        >
                          <Trash2 className="h-4 w-4" />
                        </Button>
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </div>
          </div>
        </div>

        <DialogFooter>
          <DialogClose asChild>
            <Button type="button" variant="outline">
              Close
            </Button>
          </DialogClose>
          <Button type="button" onClick={handleSave}>
            Save Settings
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function ProjectRequiredState() {
  return (
    <div className="flex h-full min-h-[320px] items-center justify-center px-6">
      <div className="flex max-w-md flex-col items-center gap-3 text-center">
        <FileText className="h-10 w-10 text-muted-foreground" />
        <div className="space-y-1">
          <div className="text-sm font-medium">No project selected</div>
          <p className="text-sm text-muted-foreground">
            Select a project to browse its configured file system.
          </p>
        </div>
      </div>
    </div>
  );
}

export function ChatWorkspace({
  routeBasePath,
  showProjectSelect,
  compactToolbar = false,
}: ChatWorkspaceProps) {
  const router = useRouter();
  const searchParams = useSearchParams();
  const queryProjectId = searchParams.get("projectId");
  const queryContextId = searchParams.get("contextId");

  React.useEffect(() => {
    clearLegacyChatSettingsUrl();
  }, []);

  const [currentTab, setCurrentTab] = React.useState("chat");
  const [isMobile, setIsMobile] = React.useState(false);
  const [isDrawerOpen, setIsDrawerOpen] = React.useState(false);
  const [selectedProjectId, setSelectedProjectId] = React.useState<string | null>(queryProjectId);
  const [selectedTargetValue, setSelectedTargetValue] = React.useState<string | null>(null);
  const [showChatHistory, setShowChatHistory] = React.useState(true);
  const [showFileExplorer, setShowFileExplorer] = React.useState(true);
  const [contextId, setContextId] = React.useState<string | null>(queryContextId);
  const [chatSessionSeed, setChatSessionSeed] = React.useState<ChatSessionSeed>({
    revision: 0,
    contextId: queryContextId,
    messages: [],
    usage: EMPTY_TOKEN_USAGE,
  });
  const [conversationListRefreshSignal, setConversationListRefreshSignal] = React.useState(0);
  const [drawerContent, setDrawerContent] = React.useState<"chat" | "files" | null>(null);
  const [selectedFile, setSelectedFile] = React.useState<string | null>(null);
  const [fileContent, setFileContent] = React.useState("");
  const [isLoadingContent, setIsLoadingContent] = React.useState(false);
  const [contentError, setContentError] = React.useState<string | null>(null);
  const [onlyDiff, setOnlyDiff] = React.useState(true);
  const [recursiveMode] = React.useState(true);
  const [diffContentData, setDiffContentData] = React.useState<GitDiffResponse | null>(null);
  const [comments, setComments] = React.useState<LineComment[]>([]);
  const [envVars, setEnvVars] = React.useState<EnvVar[]>([]);

  const hydratedContextKeyRef = React.useRef<string | null>(null);

  const projectsQuery = useQuery({
    queryKey: ["projects"],
    queryFn: async () => (await apiGet("/api/projects")) as ProjectDto[],
  });

  const agentsQuery = useQuery({
    queryKey: ["agents"],
    queryFn: async () => (await apiGet("/api/agents")) as Array<AgentDto>,
  });

  const agentflowsQuery = useQuery({
    queryKey: ["agentflows"],
    queryFn: async () => (await apiGet("/api/agentflows")) as Array<AgentflowDto>,
  });

  const projects = projectsQuery.data ?? [];

  const targetOptions = React.useMemo<ChatTargetOption[]>(
    () =>
      buildChatTargetOptions({
        projectId: selectedProjectId,
        agents: agentsQuery.data ?? [],
        agentflows: agentflowsQuery.data ?? [],
      }),
    [agentflowsQuery.data, agentsQuery.data, selectedProjectId],
  );

  const selectedTarget = React.useMemo(
    () => targetOptions.find((option) => getTargetValue(option) === selectedTargetValue) ?? null,
    [selectedTargetValue, targetOptions],
  );

  const selectedProject = React.useMemo(
    () => projects.find((project) => project.id === selectedProjectId) ?? null,
    [projects, selectedProjectId],
  );

  const resolvedWorkspace = React.useMemo(
    () => selectedProject?.workspace?.trim() || "",
    [selectedProject],
  );

  const hasProjectFileSystem = selectedProjectId !== null;

  const syncRoute = React.useCallback(
    (projectId: string | null, contextIdValue: string | null = null) => {
      const nextHref = buildChatHref(routeBasePath, {
        projectId,
        contextId: contextIdValue,
      });

      if (
        typeof window === "undefined" ||
        `${window.location.pathname}${window.location.search}${window.location.hash}` !== nextHref
      ) {
        router.replace(nextHref, { scroll: false });
      }
    },
    [routeBasePath, router],
  );

  const refreshConversationList = React.useCallback(() => {
    setConversationListRefreshSignal((signal) => signal + 1);
  }, []);

  const replaceChatSession = React.useCallback((nextSession: Omit<ChatSessionSeed, "revision">) => {
    setChatSessionSeed((current) => ({
      ...nextSession,
      revision: Number(current.revision) + 1,
    }));
  }, []);

  const clearFilePreview = React.useCallback(() => {
    setSelectedFile(null);
    setFileContent("");
    setContentError(null);
    setDiffContentData(null);
    setComments([]);
  }, []);

  const getProjectSettingsDraft = React.useCallback(
    (projectId: string | null): ChatSettingsDraft => {
      const storedSettings = projectId ? chatSettingsStorage.get(projectId) : {};

      return {
        envVars: storedSettings.envVars ?? [],
      };
    },
    [],
  );

  const getActiveSettingsDraft = React.useCallback(
    (projectId: string | null): ChatSettingsDraft => {
      if (projectId && projectId === selectedProjectId) {
        return {
          envVars,
        };
      }

      return getProjectSettingsDraft(projectId);
    },
    [envVars, getProjectSettingsDraft, selectedProjectId],
  );

  const environmentVariables = React.useMemo(() => {
    const environmentVariables: Record<string, string> = {};

    normalizeEnvVars(envVars).forEach((envVar) => {
      if (envVar.key) {
        environmentVariables[envVar.key] = envVar.value;
      }
    });

    return environmentVariables;
  }, [envVars]);

  const loadFileContent = React.useCallback(
    async (filePath: string) => {
      setIsLoadingContent(true);
      setContentError(null);
      setDiffContentData(null);

      try {
        if (onlyDiff) {
          if (!selectedProjectId) {
            throw new Error("Select a project before loading files");
          }
          const diff = await getFileDiff(selectedProjectId, filePath);
          setDiffContentData(diff);
          setFileContent("");
          setSelectedFile(filePath);
        } else {
          if (!selectedProjectId) {
            throw new Error("Select a project before loading files");
          }
          const content = await readFile(selectedProjectId, filePath);
          setFileContent(content);
          setDiffContentData(null);
          setSelectedFile(filePath);
        }
      } catch (error) {
        console.error("Error loading file:", error);
        setContentError((error as Error).message);
        setFileContent("");
        setDiffContentData(null);
      } finally {
        setIsLoadingContent(false);
      }
    },
    [onlyDiff, selectedProjectId],
  );

  const handleOnFileDeleted = React.useCallback(
    (filePath: string) => {
      if (filePath === selectedFile) {
        clearFilePreview();
      }
    },
    [clearFilePreview, selectedFile],
  );

  const handleOnLoadFileContent = React.useCallback(
    (filePath: string) => {
      void loadFileContent(filePath);
    },
    [loadFileContent],
  );

  const handleOnFileReseted = React.useCallback(
    (filePath: string | null) => {
      if (selectedFile && selectedFile === filePath) {
        void loadFileContent(selectedFile);
      }
    },
    [loadFileContent, selectedFile],
  );

  const handleOnFileSelected = React.useCallback(
    (filePath: string | null) => {
      if (filePath && filePath !== selectedFile) {
        void loadFileContent(filePath);
        if (isMobile) {
          setIsDrawerOpen(false);
        }
      }
    },
    [isMobile, loadFileContent, selectedFile],
  );

  const clearLocalSessionState = React.useCallback(() => {
    hydratedContextKeyRef.current = null;
    setContextId(null);
    replaceChatSession({
      contextId: null,
      messages: [],
      usage: EMPTY_TOKEN_USAGE,
    });
  }, [replaceChatSession]);

  const resetSession = React.useCallback(() => {
    clearLocalSessionState();
    syncRoute(selectedProjectId, null);
  }, [clearLocalSessionState, selectedProjectId, syncRoute]);

  const startNewConversation = React.useCallback(() => {
    clearLocalSessionState();
    syncRoute(selectedProjectId, null);
  }, [clearLocalSessionState, selectedProjectId, syncRoute]);

  const loadContextHistory = React.useCallback(
    async (projectId: string, nextContextIdValue: string) => {
      const details = await getProjectContextDetails(projectId, nextContextIdValue);
      const restoredTargetValue = getRestoredTargetValue(details.messages ?? []);

      hydratedContextKeyRef.current = getContextHydrationKey(projectId, details.contextId);
      setSelectedProjectId(projectId);
      setContextId(details.contextId);
      replaceChatSession({
        contextId: details.contextId,
        messages: details.messages ?? [],
        usage: details.usage,
      });
      if (restoredTargetValue) {
        setSelectedTargetValue(restoredTargetValue);
      }
      syncRoute(projectId, details.contextId);
      return details;
    },
    [replaceChatSession, syncRoute],
  );

  React.useEffect(() => {
    const mediaQuery = window.matchMedia("(max-width: 768px)");
    const handleMediaChange = (event: MediaQueryListEvent) => {
      setIsMobile(event.matches);
    };

    setIsMobile(mediaQuery.matches);
    mediaQuery.addEventListener("change", handleMediaChange);
    return () => mediaQuery.removeEventListener("change", handleMediaChange);
  }, []);

  React.useEffect(() => {
    if (!selectedProjectId) {
      setEnvVars((current) => (current.length === 0 ? current : []));
      return;
    }

    const draft = getProjectSettingsDraft(selectedProjectId);
    setEnvVars((current) => (areEnvVarsEqual(current, draft.envVars) ? current : draft.envVars));
  }, [getProjectSettingsDraft, selectedProjectId]);

  React.useEffect(() => {
    if (selectedFile) {
      void loadFileContent(selectedFile);
    }
  }, [loadFileContent, onlyDiff, selectedFile]);

  React.useEffect(() => {
    clearFilePreview();
  }, [clearFilePreview, resolvedWorkspace, selectedProjectId]);

  React.useEffect(() => {
    if (projects.length === 0) {
      setSelectedProjectId(null);
      return;
    }

    setSelectedProjectId((current) => {
      if (current && projects.some((project) => project.id === current)) {
        return current;
      }

      if (queryProjectId && projects.some((project) => project.id === queryProjectId)) {
        return queryProjectId;
      }

      const defaultProject = projects.find(
        (project) => project.id === DEFAULT_PROJECT_VALUE || project.name === DEFAULT_PROJECT_VALUE,
      );
      if (defaultProject) {
        return defaultProject.id;
      }

      return projects[0].id;
    });
  }, [projects, queryProjectId]);

  React.useEffect(() => {
    if (targetOptions.length === 0) {
      setSelectedTargetValue(null);
      return;
    }

    setSelectedTargetValue((current) => {
      if (current && targetOptions.some((option) => getTargetValue(option) === current)) {
        return current;
      }

      if (!selectedProjectId) {
        return null;
      }

      const storedTargetValue = chatSettingsStorage.get(selectedProjectId).targetValue;
      if (
        storedTargetValue &&
        targetOptions.some((option) => getTargetValue(option) === storedTargetValue)
      ) {
        return storedTargetValue;
      }

      const defaultAgent = targetOptions.find(
        (option) => option.type === "agent" && option.label === DEFAULT_AGENT_LABEL,
      );
      if (defaultAgent) {
        return getTargetValue(defaultAgent);
      }

      return getTargetValue(targetOptions[0]);
    });
  }, [selectedProjectId, targetOptions]);

  React.useEffect(() => {
    const routeAction = getChatRouteSessionAction({
      queryProjectId,
      queryContextId,
      hydratedRouteKey: hydratedContextKeyRef.current,
    });

    if (routeAction.type === "clearLocal") {
      clearLocalSessionState();
      return;
    }

    if (routeAction.type === "selectProject") {
      clearLocalSessionState();
      setSelectedProjectId(routeAction.projectId);
      syncRoute(routeAction.projectId, null);
      return;
    }

    if (routeAction.type === "ignore") {
      return;
    }

    const hydrationKey = getRouteHydrationKey(routeAction);
    if (hydrationKey) {
      hydratedContextKeyRef.current = hydrationKey;
    }

    let cancelled = false;

    void (async () => {
      try {
        if (routeAction.type === "hydrateContext") {
          const details = await getProjectContextDetails(
            routeAction.projectId,
            routeAction.contextId,
          );
          const restoredTargetValue = getRestoredTargetValue(details.messages ?? []);
          if (cancelled) {
            return;
          }

          hydratedContextKeyRef.current = routeAction.hydrateKey;
          setSelectedProjectId(routeAction.projectId);
          setContextId(details.contextId);
          replaceChatSession({
            contextId: details.contextId,
            messages: details.messages ?? [],
            usage: details.usage,
          });
          if (restoredTargetValue) {
            setSelectedTargetValue(restoredTargetValue);
          }
          syncRoute(routeAction.projectId, details.contextId);
          return;
        }
      } catch (error) {
        if (!cancelled) {
          if (hydrationKey && hydratedContextKeyRef.current === hydrationKey) {
            hydratedContextKeyRef.current = null;
          }
          toast.error(`Failed to load chat history: ${getApiErrorMessage(error)}`);
        }
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [clearLocalSessionState, queryContextId, queryProjectId, replaceChatSession, syncRoute]);

  const handleProjectChange = React.useCallback(
    (nextProjectId: string) => {
      if (nextProjectId === selectedProjectId) {
        return;
      }

      hydratedContextKeyRef.current = null;
      setSelectedProjectId(nextProjectId);
      setSelectedTargetValue(null);
      setContextId(null);
      replaceChatSession({
        contextId: null,
        messages: [],
        usage: EMPTY_TOKEN_USAGE,
      });
      syncRoute(nextProjectId, null);
    },
    [replaceChatSession, selectedProjectId, syncRoute],
  );

  const handleTargetChange = React.useCallback(
    (nextTargetValue: string) => {
      if (nextTargetValue === selectedTargetValue) {
        return;
      }

      setSelectedTargetValue(nextTargetValue);
      if (selectedProjectId) {
        chatSettingsStorage.set(selectedProjectId, { targetValue: nextTargetValue });
      }
    },
    [selectedProjectId, selectedTargetValue],
  );

  const handleAgentSelect = React.useCallback(
    ({ agentType, agentId }: AgentSelection) => {
      handleTargetChange(
        getTargetValue({
          id: agentId,
          type: agentType === 0 ? "agent" : "agentflow",
        }),
      );
    },
    [handleTargetChange],
  );

  const handleChatContextIdChange = React.useCallback(
    (nextContextId: string | null) => {
      hydratedContextKeyRef.current = nextContextId
        ? getContextHydrationKey(selectedProjectId, nextContextId)
        : null;
      setContextId(nextContextId);
      syncRoute(selectedProjectId, nextContextId);
    },
    [selectedProjectId, syncRoute],
  );

  const handleContextSelect = React.useCallback(
    async (context: ContextSummary) => {
      if (!selectedProjectId) {
        toast.error("Please select a project");
        return;
      }

      try {
        await loadContextHistory(selectedProjectId, context.contextId);
        setIsDrawerOpen(false);
      } catch (error) {
        toast.error(`Failed to load context: ${getApiErrorMessage(error)}`);
      }
    },
    [loadContextHistory, selectedProjectId],
  );

  const handleActiveContextResolved = React.useCallback(
    (context: ContextSummary) => {
      if (!selectedProjectId) {
        return;
      }

      hydratedContextKeyRef.current = getContextHydrationKey(selectedProjectId, context.contextId);
      setContextId(context.contextId);
      syncRoute(selectedProjectId, context.contextId);
    },
    [selectedProjectId, syncRoute],
  );

  const handleNewConversation = React.useCallback(() => {
    startNewConversation();
    setIsDrawerOpen(false);
  }, [startNewConversation]);

  const handleAllConversationsDeleted = React.useCallback(() => {
    resetSession();
    setIsDrawerOpen(false);
  }, [resetSession]);

  const openDrawer = React.useCallback((type: "chat" | "files") => {
    setDrawerContent(type);
    setIsDrawerOpen(true);
  }, []);

  const handleSidebarToggle = React.useCallback(() => {
    if (currentTab === "chat") {
      if (isMobile) {
        openDrawer("chat");
        return;
      }

      setShowChatHistory((prev) => !prev);
      return;
    }

    if (!hasProjectFileSystem) {
      return;
    }

    if (isMobile) {
      openDrawer("files");
      return;
    }

    setShowFileExplorer((prev) => !prev);
  }, [currentTab, hasProjectFileSystem, isMobile, openDrawer]);

  const handleTabChange = React.useCallback((value: string) => {
    setCurrentTab(value);
    setIsDrawerOpen(false);
  }, []);

  const handleSaveChatSettings = React.useCallback(
    (draft: ChatSettingsDraft) => {
      if (!selectedProjectId) {
        toast.error("Please select a project.");
        return false;
      }

      const normalizedSettings: ChatProjectSettingsStorageValues = {
        envVars: normalizeEnvVars(draft.envVars),
      };

      chatSettingsStorage.set(selectedProjectId, normalizedSettings);
      const nextDraft = getProjectSettingsDraft(selectedProjectId);
      setEnvVars(nextDraft.envVars);

      toast.success("Chat settings saved");
      return true;
    },
    [getProjectSettingsDraft, selectedProjectId],
  );

  const renderConversationList = React.useCallback(
    () => (
      <ConversationList
        projectId={selectedProjectId ?? ""}
        currentContextId={contextId}
        refreshSignal={conversationListRefreshSignal}
        onContextSelect={(nextContext) => {
          void handleContextSelect(nextContext);
        }}
        onActiveContextResolved={handleActiveContextResolved}
        onNewConversation={handleNewConversation}
        onAllConversationsDeleted={handleAllConversationsDeleted}
        headerActions={
          <ChatSettingsDialog
            selectedProjectId={selectedProjectId}
            getDraft={getActiveSettingsDraft}
            onSave={handleSaveChatSettings}
          />
        }
      />
    ),
    [
      getActiveSettingsDraft,
      contextId,
      conversationListRefreshSignal,
      handleActiveContextResolved,
      handleAllConversationsDeleted,
      handleContextSelect,
      handleNewConversation,
      handleSaveChatSettings,
      selectedProjectId,
    ],
  );

  const projectSelectOptions = React.useMemo<SearchableSelectOption[]>(
    () =>
      projects.map((project) => ({
        value: project.id,
        title: project.name,
        subtitle: project.workspace?.trim() || undefined,
      })),
    [projects],
  );

  const isChatTab = currentTab === "chat";
  const isFilesTab = currentTab === "files";
  const activeSidebarVisible = isChatTab
    ? showChatHistory
    : hasProjectFileSystem && showFileExplorer;
  const activeSidebarTitle = isChatTab ? "chat history" : "file explorer";
  const isSidebarToggleDisabled = isFilesTab && !hasProjectFileSystem;
  const sidebarToggleTitle = isSidebarToggleDisabled
    ? "Select a project to browse files"
    : isMobile
      ? `Open ${activeSidebarTitle}`
      : activeSidebarVisible
        ? `Hide ${activeSidebarTitle}`
        : `Show ${activeSidebarTitle}`;

  return (
    <div className="flex h-full w-full min-w-0 flex-col gap-3 pt-2">
      {projectsQuery.isError || agentsQuery.isError || agentflowsQuery.isError ? (
        <div className="text-sm text-destructive">
          Failed to load chat dependencies:{" "}
          {getApiErrorMessage(projectsQuery.error ?? agentsQuery.error ?? agentflowsQuery.error)}
        </div>
      ) : null}

      <Tabs
        value={currentTab}
        onValueChange={handleTabChange}
        className="flex min-h-0 flex-1 flex-col"
      >
        <div className="flex flex-wrap items-center gap-2">
          <div className="flex flex-wrap items-center gap-2">
            {showProjectSelect ? (
              <div className="w-[220px]">
                <SearchableSelect
                  id="chat-project-select"
                  ariaLabel="Select project"
                  value={selectedProjectId ?? ""}
                  onValueChange={handleProjectChange}
                  options={projectSelectOptions}
                  placeholder="Select project"
                  searchPlaceholder="Search projects..."
                  clearable={false}
                />
              </div>
            ) : null}

            <div className="w-65">
              <AgentSelector
                id="chat-target-select"
                size={compactToolbar ? "sm" : "default"}
                projectId={selectedProjectId}
                value={
                  selectedTarget
                    ? {
                        agentType: selectedTarget.type === "agent" ? 0 : 1,
                        agentId: selectedTarget.id,
                      }
                    : null
                }
                onSelect={handleAgentSelect}
              />
            </div>
          </div>
          <div className="flex-1" />

          <TabsList className={cn("w-fit", compactToolbar && "h-8 p-2")}>
            <TabsTrigger
              value="chat"
              className={cn("cursor-pointer", compactToolbar && "h-6 px-2.5 py-0 text-xs")}
            >
              Chat
            </TabsTrigger>
            <TabsTrigger
              value="files"
              className={cn("cursor-pointer", compactToolbar && "h-6 px-2.5 py-0 text-xs")}
            >
              Files
            </TabsTrigger>
          </TabsList>
          <Button
            variant="ghost"
            className="cursor-pointer"
            size="sm"
            onClick={handleSidebarToggle}
            title={sidebarToggleTitle}
            aria-label={sidebarToggleTitle}
            disabled={isSidebarToggleDisabled}
          >
            {activeSidebarVisible ? (
              <PanelLeftClose className="h-4 w-4" />
            ) : (
              <PanelLeftOpen className="h-4 w-4" />
            )}
          </Button>
        </div>

        <TabsContent
          value="chat"
          forceMount
          className="mt-2 flex min-h-0 flex-1 data-[state=inactive]:hidden"
        >
          <ColResizeSplit>
            {!isMobile && isChatTab && showChatHistory ? (
              <ColResizeSplit.Left minWidth={260} maxWidth={520}>
                {renderConversationList()}
              </ColResizeSplit.Left>
            ) : null}

            <ColResizeSplit.Right>
              <div className="relative flex flex-col min-h-105 flex-1 overflow-hidden">
                {/* <div className="border-b px-4 py-3">
                  <div className="text-xs text-muted-foreground">
                    {selectedProjectId
                      ? `Project: ${projects.find((project) => project.id === selectedProjectId)?.name ?? selectedProjectId}`
                      : "Select a project to begin"}
                    {selectedTarget ? ` · Target: ${selectedTarget.type}` : ""}
                  </div>
                </div> */}

                <div className="relative flex h-[calc(100%-57px)] min-h-0 flex-1 flex-col border-t">
                  <Chat
                    target={selectedTarget}
                    projectId={selectedProjectId}
                    sessionSeed={chatSessionSeed}
                    environmentVariables={environmentVariables}
                    onContextIdChange={handleChatContextIdChange}
                    onConversationChange={refreshConversationList}
                  />
                </div>
              </div>
            </ColResizeSplit.Right>
          </ColResizeSplit>
        </TabsContent>

        <TabsContent value="files" className="mt-2 flex min-h-0 flex-1">
          <ColResizeSplit>
            {!isMobile && hasProjectFileSystem && showFileExplorer ? (
              <ColResizeSplit.Left minWidth={260} maxWidth={520}>
                <Explorer
                  projectId={selectedProjectId!}
                  rootDirectory={resolvedWorkspace || "/"}
                  onlyDiff={onlyDiff}
                  recursiveMode={recursiveMode}
                  onOnlyDiffChange={setOnlyDiff}
                  onFileDeleted={handleOnFileDeleted}
                  onFileSelected={handleOnFileSelected}
                  onFileReseted={handleOnFileReseted}
                  onLoadFileContent={handleOnLoadFileContent}
                />
              </ColResizeSplit.Left>
            ) : null}

            <ColResizeSplit.Right>
              <Card className="flex min-h-[420px] flex-1 overflow-hidden">
                {hasProjectFileSystem ? (
                  <FileContent
                    selectedFile={selectedFile}
                    isLoadingContent={isLoadingContent}
                    contentError={contentError}
                    onlyDiff={onlyDiff}
                    diffContentData={diffContentData}
                    comments={comments}
                    setComments={setComments}
                    fileContent={fileContent}
                  />
                ) : (
                  <ProjectRequiredState />
                )}
              </Card>
            </ColResizeSplit.Right>
          </ColResizeSplit>
        </TabsContent>
      </Tabs>

      <Drawer direction="left" open={isDrawerOpen} onOpenChange={setIsDrawerOpen}>
        <DrawerContent className="h-screen max-h-screen">
          <DrawerHeader>
            <DrawerTitle>
              {drawerContent === "files" ? "File Explorer" : "Chat History"}
            </DrawerTitle>
          </DrawerHeader>
          <div className="h-full min-h-0 overflow-hidden px-4 pb-6">
            {drawerContent === "files" ? (
              hasProjectFileSystem ? (
                <Explorer
                  projectId={selectedProjectId!}
                  rootDirectory={resolvedWorkspace || "/"}
                  onlyDiff={onlyDiff}
                  recursiveMode={recursiveMode}
                  onOnlyDiffChange={setOnlyDiff}
                  onFileDeleted={handleOnFileDeleted}
                  onFileSelected={handleOnFileSelected}
                  onFileReseted={handleOnFileReseted}
                  onLoadFileContent={handleOnLoadFileContent}
                />
              ) : (
                <ProjectRequiredState />
              )
            ) : (
              renderConversationList()
            )}
          </div>
        </DrawerContent>
      </Drawer>
    </div>
  );
}
