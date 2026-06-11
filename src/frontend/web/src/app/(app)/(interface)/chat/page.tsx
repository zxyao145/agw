"use client";

import * as React from "react";
import { Uuid4 } from "id128";
import Link from "next/link";
import {
  FileText,
  PanelLeftClose,
  PanelLeftOpen,
  Plus,
  Settings,
  Share2,
  Trash2,
} from "lucide-react";
import { useQuery } from "@tanstack/react-query";
import { useRouter, useSearchParams } from "next/navigation";
import { toast } from "sonner";

import { getFileDiff, readFile, type GitDiffResponse } from "@/api/files";
import { apiGet, getApiKey } from "@/api/client";
import { buildSettingCommandPayload } from "@/api/execution-ws";
import {
  clearProjectContextRecords,
  getProjectContextDetails,
  getProjectContextDetailsByTaskId,
  type ContextSummary,
} from "@/api/task-client";
import { Explorer, FileContent } from "@/components/file-explorer";
import type { LineComment } from "@/components/file-explorer";
import { Conversation } from "@/components/message/conversation";
import { type UserInputRef } from "@/components/message/user-input";
import { ConversationList } from "@/components/task/conversation-list";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog";
import { Drawer, DrawerContent, DrawerHeader, DrawerTitle } from "@/components/ui/drawer";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  SearchableSelect,
  type SearchableSelectOption,
} from "@/components/SearchableSelect/searchable-select";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import {
  createUserTextMessage,
  mergeStreamingMessagesById,
  toExecutionWsUserInput,
} from "@/lib/execution-stream";
import type { AiMessage } from "@/types";
import { chatSettingsStorage } from "./settings-storage";
import ColResizeSplit from "./components/split-layout";
import { InputArea } from "./components/user-input/input-area";
import { handleAiMessage, type AiMessageAction } from "./lib/ai-message-handlers";
import {
  CHAT_SETTINGS_DIALOG_BODY_CLASS_NAME,
  CHAT_SETTINGS_DIALOG_CONTENT_CLASS_NAME,
} from "./lib/chat-settings";
import {
  getChatRouteSessionAction,
  getContextHydrationKey,
  getTaskHydrationKey,
} from "./lib/session-routing";
import { copyCurrentUrlToClipboard } from "./lib/share-url";
import {
  buildChatTargetOptions,
  getTargetValue,
  getTargetValueFromMetadata,
} from "./lib/target-options";
import {
  buildChatUrlSettings,
  decodeChatUrlSettings,
  encodeChatUrlSettings,
  getChatSettingsHash,
  getChatSettingsHashValue,
  getTargetValueFromChatUrlSettings,
} from "./lib/url-settings";
import type { ChatProjectSettingsStorageValues, ChatTargetOption, EnvVar } from "./types";
import { getApiErrorMessage } from "@/api/utils";

type ProjectDto = {
  id: string;
  name: string;
  workspace?: string | null;
  enable: boolean;
};

type AgentDto = {
  id: string;
  displayName: string;
  name: string;
};

type AgentflowDto = {
  id: string;
  name: string;
  enable?: boolean;
};

const DEFAULT_PROJECT_VALUE = "default-built-in";
const DEFAULT_AGENT_LABEL = "Hello";

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

function nextTaskId(): string {
  return Uuid4.generate().toCanonical();
}

function getChatRouteHref({
  projectId,
  taskId,
  contextId,
  settingsHash,
}: {
  projectId: string | null;
  taskId: string | null;
  contextId: string | null;
  settingsHash: string;
}): string {
  const nextParams = new URLSearchParams();
  if (projectId) {
    nextParams.set("projectId", projectId);
  }
  if (projectId && contextId) {
    nextParams.set("contextId", contextId);
  }
  if (projectId && taskId) {
    nextParams.set("taskId", taskId);
  }

  const nextQuery = nextParams.toString();
  return `${nextQuery ? `/chat?${nextQuery}` : "/chat"}${settingsHash}`;
}

function getCurrentChatSettingsHashValue(): string | null {
  if (typeof window === "undefined") {
    return null;
  }

  return getChatSettingsHashValue(window.location.hash);
}

function replaceCurrentChatSettingsHash(settingsParam: string | null): void {
  if (typeof window === "undefined") {
    return;
  }

  const searchParams = new URLSearchParams(window.location.search);
  searchParams.delete("settings");
  const nextSearch = searchParams.toString();
  const nextUrl = `${window.location.pathname}${nextSearch ? `?${nextSearch}` : ""}${getChatSettingsHash(settingsParam)}`;
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

function WorkspaceRequiredState({ projectName }: { projectName: string | null }) {
  return (
    <div className="flex h-full min-h-[320px] items-center justify-center px-6">
      <div className="flex max-w-md flex-col items-center gap-3 text-center">
        <FileText className="h-10 w-10 text-muted-foreground" />
        <div className="space-y-1">
          <div className="text-sm font-medium">Workspace is not configured</div>
          <p className="text-sm text-muted-foreground">
            {projectName
              ? `Project "${projectName}" does not have a workspace yet.`
              : "The selected project does not have a workspace yet."}{" "}
            Configure it on the Projects page to enable file browsing.
          </p>
        </div>
        <Button asChild variant="outline" size="sm">
          <Link href="/projects">Open Projects</Link>
        </Button>
      </div>
    </div>
  );
}

export default function ChatPage() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const queryProjectId = searchParams.get("projectId");
  const queryTaskId = searchParams.get("taskId");
  const queryContextId = searchParams.get("contextId");
  const [hashSettingsValue, setHashSettingsValue] = React.useState<string | null>(null);

  React.useEffect(() => {
    setHashSettingsValue(getCurrentChatSettingsHashValue());
  }, []);
  const routeSettings = React.useMemo(
    () => decodeChatUrlSettings(hashSettingsValue),
    [hashSettingsValue],
  );
  const initialRouteTargetValue = React.useMemo(
    () => getTargetValueFromChatUrlSettings(routeSettings),
    [routeSettings],
  );

  const [currentTab, setCurrentTab] = React.useState("chat");
  const [isMobile, setIsMobile] = React.useState(false);
  const [isDrawerOpen, setIsDrawerOpen] = React.useState(false);
  const [selectedProjectId, setSelectedProjectId] = React.useState<string | null>(queryProjectId);
  const [selectedTargetValue, setSelectedTargetValue] = React.useState<string | null>(
    initialRouteTargetValue,
  );
  const [showChatHistory, setShowChatHistory] = React.useState(true);
  const [showFileExplorer, setShowFileExplorer] = React.useState(true);
  const [messages, setMessages] = React.useState<AiMessage[]>([]);
  const [taskId, setTaskId] = React.useState<string | null>(queryTaskId);
  const [contextId, setContextId] = React.useState<string | null>(queryContextId);
  const [isExecuting, setIsExecuting] = React.useState(false);
  const [drawerContent, setDrawerContent] = React.useState<"chat" | "files" | null>(null);
  const [selectedFile, setSelectedFile] = React.useState<string | null>(null);
  const [fileContent, setFileContent] = React.useState("");
  const [isLoadingContent, setIsLoadingContent] = React.useState(false);
  const [contentError, setContentError] = React.useState<string | null>(null);
  const [onlyDiff, setOnlyDiff] = React.useState(true);
  const [recursiveMode] = React.useState(true);
  const [diffContentData, setDiffContentData] = React.useState<GitDiffResponse | null>(null);
  const [comments, setComments] = React.useState<LineComment[]>([]);
  const [envVars, setEnvVars] = React.useState<EnvVar[]>(
    routeSettings?.chatSettings?.envVars ?? [],
  );

  const wsRef = React.useRef<WebSocket | null>(null);
  const messagesStartRef = React.useRef<HTMLDivElement>(null!);
  const messagesEndRef = React.useRef<HTMLDivElement>(null!);
  const userInputRef = React.useRef<UserInputRef | null>(null);
  const hydratedTaskKeyRef = React.useRef<string | null>(null);

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

  const projects = React.useMemo(
    () => (projectsQuery.data ?? []).filter((project) => project.enable),
    [projectsQuery.data],
  );

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

  const hasWorkspace = resolvedWorkspace.length > 0;

  const activeRouteSettings = React.useMemo(
    () =>
      routeSettings && (!queryProjectId || queryProjectId === selectedProjectId)
        ? routeSettings
        : null,
    [queryProjectId, routeSettings, selectedProjectId],
  );

  const routeTargetValue = React.useMemo(
    () => getTargetValueFromChatUrlSettings(activeRouteSettings),
    [activeRouteSettings],
  );

  const routeSettingsParam = React.useMemo(() => {
    if (!selectedTarget) {
      return null;
    }

    return encodeChatUrlSettings(
      buildChatUrlSettings({
        target: selectedTarget,
        envVars: normalizeEnvVars(envVars),
      }),
    );
  }, [envVars, selectedTarget]);

  const closeSocket = React.useCallback((reason: string) => {
    const ws = wsRef.current;
    if (!ws) {
      return;
    }

    wsRef.current = null;
    if (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING) {
      ws.close(1000, reason);
    }
  }, []);

  const syncRoute = React.useCallback(
    (
      projectId: string | null,
      taskIdValue: string | null,
      contextIdValue: string | null = null,
      settingsParamValue?: string | null,
    ) => {
      const nextSettingsParam =
        settingsParamValue === undefined ? routeSettingsParam : settingsParamValue;
      const settingsHash = getChatSettingsHash(nextSettingsParam);
      const nextHref = getChatRouteHref({
        projectId,
        taskId: taskIdValue,
        contextId: contextIdValue,
        settingsHash,
      });
      router.replace(nextHref, { scroll: false });
      setHashSettingsValue(nextSettingsParam);
    },
    [router, routeSettingsParam],
  );

  const applyAiMessageActions = React.useCallback((actions: AiMessageAction[]) => {
    const pendingMessages: AiMessage[] = [];

    actions.forEach((action) => {
      switch (action.type) {
        case "append":
          pendingMessages.push(action.message);
          break;
        case "setIsExecuting":
          setIsExecuting(action.value);
          break;
        default:
          break;
      }
    });

    if (pendingMessages.length > 0) {
      setMessages((prev) => mergeStreamingMessagesById([...prev, ...pendingMessages]));
    }
  }, []);

  const isTurnFinishedMessage = React.useCallback((message: AiMessage): boolean => {
    if (message.role?.toLowerCase() !== "system") {
      return false;
    }

    if (message.author !== "$agw-server") {
      return false;
    }

    return message.contents.some(
      (content) => content.additionalProperties?.type === "turn-finished",
    );
  }, []);

  const waitForWebSocketOpen = React.useCallback((ws: WebSocket): Promise<void> => {
    if (ws.readyState === WebSocket.OPEN) {
      return Promise.resolve();
    }

    return new Promise<void>((resolve, reject) => {
      const onOpen = () => {
        ws.removeEventListener("open", onOpen);
        ws.removeEventListener("error", onError);
        resolve();
      };
      const onError = () => {
        ws.removeEventListener("open", onOpen);
        ws.removeEventListener("error", onError);
        reject(new Error("Failed to connect"));
      };

      ws.addEventListener("open", onOpen);
      ws.addEventListener("error", onError);
    });
  }, []);

  const setupWebSocket = React.useCallback(
    (executionId: string) => {
      const protocol = window.location.protocol === "https:" ? "wss:" : "ws:";
      const apiKey = getApiKey();
      const apiKeyParam = apiKey ? `?X-API-Key=${encodeURIComponent(apiKey)}` : "";
      const ws = new WebSocket(
        `${protocol}//${window.location.host}/api/executions/${executionId}/ws${apiKeyParam}`,
      );
      wsRef.current = ws;

      ws.onmessage = (event) => {
        try {
          const message = JSON.parse(event.data as string) as AiMessage;
          if (isTurnFinishedMessage(message)) {
            setIsExecuting(false);
            return;
          }

          applyAiMessageActions(handleAiMessage(message));
        } catch (error) {
          console.error("Parse error:", error);
        }
      };

      ws.onerror = () => {
        toast.error("WebSocket connection error");
        setIsExecuting(false);
      };

      ws.onclose = (event) => {
        wsRef.current = null;
        setIsExecuting(false);

        if (event.code !== 1000) {
          if (event.code === 1003) {
            toast.error("Invalid request data");
          } else if (event.code === 1007) {
            toast.error(event.reason || "Invalid request payload");
          } else if (event.code === 1011) {
            toast.error("Server error during execution");
          }
        }
      };

      return ws;
    },
    [applyAiMessageActions, isTurnFinishedMessage],
  );

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

  const buildEnvironmentVariables = React.useCallback(() => {
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
          const diff = await getFileDiff(filePath);
          setDiffContentData(diff);
          setFileContent("");
          setSelectedFile(filePath);
        } else {
          const content = await readFile(filePath);
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
    [onlyDiff],
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

  const buildSettingCommand = React.useCallback(
    (nextTaskIdValue: string) =>
      buildSettingCommandPayload({
        environmentVariables: buildEnvironmentVariables(),
        projectId: selectedProjectId ?? "",
        taskId: nextTaskIdValue,
      }),
    [buildEnvironmentVariables, selectedProjectId],
  );

  const buildExecRequest = React.useCallback(
    (message: AiMessage) => ({
      type: "ExecCommand",
      agentType: selectedTarget?.type === "agent" ? 0 : 1,
      input: toExecutionWsUserInput(message),
    }),
    [selectedTarget],
  );

  const ensureTaskId = React.useCallback(() => {
    if (taskId) {
      return taskId;
    }

    const nextId = nextTaskId();
    hydratedTaskKeyRef.current = getTaskHydrationKey(selectedProjectId, nextId);
    setTaskId(nextId);
    syncRoute(selectedProjectId, nextId, contextId);
    return nextId;
  }, [contextId, selectedProjectId, syncRoute, taskId]);

  const clearLocalSessionState = React.useCallback(() => {
    closeSocket("Session cleared");
    hydratedTaskKeyRef.current = null;
    setIsExecuting(false);
    setMessages([]);
    setTaskId(null);
    setContextId(null);
    userInputRef.current?.setInput("");
  }, [closeSocket]);

  const clearActiveSessionState = React.useCallback(() => {
    if (selectedProjectId && contextId) {
      void clearProjectContextRecords(selectedProjectId, contextId);
    }
    clearLocalSessionState();
  }, [clearLocalSessionState, contextId, selectedProjectId]);

  const resetSession = React.useCallback(() => {
    clearActiveSessionState();
    syncRoute(selectedProjectId, null, null);
  }, [clearActiveSessionState, selectedProjectId, syncRoute]);

  const startNewTask = React.useCallback(() => {
    clearLocalSessionState();
    syncRoute(selectedProjectId, null, null);
  }, [clearLocalSessionState, selectedProjectId, syncRoute]);


  const loadContextHistory = React.useCallback(
    async (projectId: string, nextContextIdValue: string) => {
      const details = await getProjectContextDetails(projectId, nextContextIdValue);
      const restoredTargetValue = getRestoredTargetValue(details.messages ?? []);

      closeSocket("Session switched");
      hydratedTaskKeyRef.current = getContextHydrationKey(projectId, details.contextId);
      setSelectedProjectId(projectId);
      setIsExecuting(false);
      setTaskId(details.latestTaskId ?? null);
      setContextId(details.contextId);
      setMessages(details.messages ?? []);
      if (restoredTargetValue) {
        setSelectedTargetValue(restoredTargetValue);
      }
      syncRoute(projectId, details.latestTaskId ?? null, details.contextId, null);
    },
    [closeSocket, syncRoute],
  );

  React.useEffect(() => {
    return () => {
      closeSocket("Component unmounted");
    };
  }, [closeSocket]);

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
      setEnvVars([]);
      return;
    }

    if (activeRouteSettings) {
      setEnvVars(activeRouteSettings.chatSettings?.envVars ?? []);
      return;
    }

    const draft = getProjectSettingsDraft(selectedProjectId);
    setEnvVars(draft.envVars);
  }, [activeRouteSettings, getProjectSettingsDraft, selectedProjectId]);

  React.useEffect(() => {
    if (selectedFile) {
      void loadFileContent(selectedFile);
    }
  }, [loadFileContent, onlyDiff, selectedFile]);

  React.useEffect(() => {
    clearFilePreview();
  }, [clearFilePreview, resolvedWorkspace, selectedProjectId]);

  React.useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages]);

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
      if (
        routeTargetValue &&
        targetOptions.some((option) => getTargetValue(option) === routeTargetValue)
      ) {
        return routeTargetValue;
      }

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
  }, [routeTargetValue, selectedProjectId, targetOptions]);

  React.useEffect(() => {
    const handleHashChange = () => {
      setHashSettingsValue(getCurrentChatSettingsHashValue());
    };

    window.addEventListener("hashchange", handleHashChange);
    return () => window.removeEventListener("hashchange", handleHashChange);
  }, []);

  React.useEffect(() => {
    if (!selectedProjectId || !routeSettingsParam) {
      return;
    }

    if (routeSettingsParam === hashSettingsValue) {
      return;
    }

    replaceCurrentChatSettingsHash(routeSettingsParam);
    setHashSettingsValue(routeSettingsParam);
  }, [hashSettingsValue, routeSettingsParam, selectedProjectId]);

  React.useEffect(() => {
    const routeAction = getChatRouteSessionAction({
      queryProjectId,
      queryTaskId,
      queryContextId,
      hydratedTaskKey: hydratedTaskKeyRef.current,
    });

    if (routeAction.type === "clearLocal") {
      clearLocalSessionState();
      return;
    }

    if (routeAction.type === "selectProject") {
      clearLocalSessionState();
      setSelectedProjectId(routeAction.projectId);
      return;
    }

    if (routeAction.type === "ignore") {
      return;
    }

    let cancelled = false;

    void (async () => {
      try {
        if (routeAction.type === "hydrateContext") {
          const details = await getProjectContextDetails(routeAction.projectId, routeAction.contextId);
          const restoredTargetValue = getRestoredTargetValue(details.messages ?? []);
          if (cancelled) {
            return;
          }

          closeSocket("History loaded");
          hydratedTaskKeyRef.current = routeAction.hydrateKey;
          setSelectedProjectId(routeAction.projectId);
          setIsExecuting(false);
          setTaskId(details.latestTaskId ?? null);
          setContextId(details.contextId);
          setMessages(details.messages ?? []);
          const nextTargetValue = routeTargetValue ?? restoredTargetValue;
          if (nextTargetValue) {
            setSelectedTargetValue(nextTargetValue);
          }
          return;
        }

        const details = await getProjectContextDetailsByTaskId(routeAction.projectId, routeAction.taskId);
        const restoredTargetValue = getRestoredTargetValue(details.messages ?? []);
        if (cancelled) {
          return;
        }

        closeSocket("History loaded");
        hydratedTaskKeyRef.current = routeAction.hydrateKey;
        setSelectedProjectId(routeAction.projectId);
        setIsExecuting(false);
        setTaskId(details.latestTaskId ?? routeAction.taskId);
        setContextId(details.contextId);
        setMessages(details.messages ?? []);
        const nextTargetValue = routeTargetValue ?? restoredTargetValue;
        if (nextTargetValue) {
          setSelectedTargetValue(nextTargetValue);
        }
      } catch (error) {
        if (!cancelled) {
          toast.error(`Failed to load chat history: ${getApiErrorMessage(error)}`);
        }
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [clearLocalSessionState, closeSocket, queryContextId, queryProjectId, queryTaskId, routeTargetValue]);

  const handleProjectChange = React.useCallback(
    (nextProjectId: string) => {
      if (nextProjectId === selectedProjectId) {
        return;
      }

      closeSocket("Project switched");
      hydratedTaskKeyRef.current = null;
      setIsExecuting(false);
      setSelectedProjectId(nextProjectId);
      setSelectedTargetValue(null);
      setMessages([]);
      setTaskId(null);
      setContextId(null);
      syncRoute(nextProjectId, null, null, null);
    },
    [closeSocket, selectedProjectId, syncRoute],
  );

  const handleTargetChange = React.useCallback(
    (nextTargetValue: string) => {
      if (nextTargetValue === selectedTargetValue) {
        return;
      }

      closeSocket("Target switched");
      setIsExecuting(false);
      setSelectedTargetValue(nextTargetValue);
      if (selectedProjectId) {
        chatSettingsStorage.set(selectedProjectId, { targetValue: nextTargetValue });
      }
    },
    [closeSocket, selectedProjectId, selectedTargetValue],
  );

  const handleExecute = React.useCallback(
    async (value: string) => {
      const trimmedValue = value.trim();
      if (!trimmedValue) {
        toast.error("Please enter a prompt");
        return;
      }

      if (!selectedProjectId) {
        toast.error("Please select a project");
        return;
      }

      if (!selectedTarget) {
        toast.error("Please select a target");
        return;
      }

      setIsExecuting(true);

      try {
        let ws = wsRef.current;
        if (!ws || ws.readyState === WebSocket.CLOSED || ws.readyState === WebSocket.CLOSING) {
          ws = setupWebSocket(selectedTarget.id);
          await waitForWebSocketOpen(ws);
        } else if (ws.readyState === WebSocket.CONNECTING) {
          await waitForWebSocketOpen(ws);
        }

        if (ws.readyState !== WebSocket.OPEN) {
          throw new Error("WebSocket is not open");
        }

        const userMessage = createUserTextMessage(trimmedValue);
        const firstContent = userMessage.contents[0];
        if (firstContent) {
          firstContent.additionalProperties = {
            ...firstContent.additionalProperties,
            targetType: selectedTarget.type,
            targetId: selectedTarget.id,
          };
        }

        setMessages((prev) => [...prev, userMessage]);

        const nextTaskIdValue = ensureTaskId();
        ws.send(JSON.stringify(buildSettingCommand(nextTaskIdValue)));
        ws.send(JSON.stringify(buildExecRequest(userMessage)));
      } catch (error) {
        toast.error(`Execute failed: ${getApiErrorMessage(error)}`);
        setIsExecuting(false);
      }
    },
    [
      buildExecRequest,
      buildSettingCommand,
      ensureTaskId,
      selectedProjectId,
      selectedTarget,
      setupWebSocket,
      waitForWebSocketOpen,
    ],
  );

  const handleInterrupt = React.useCallback(() => {
    const ws = wsRef.current;
    if (!ws || ws.readyState !== WebSocket.OPEN) {
      toast.error("No active session to interrupt");
      setIsExecuting(false);
      return;
    }

    ws.send(
      JSON.stringify({
        type: "InterruptCommand",
        reason: "Stop requested by user.",
      }),
    );
    closeSocket("Stop requested by user.");
    setIsExecuting(false);
  }, [closeSocket]);

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


  const handleNewTask = React.useCallback(() => {
    startNewTask();
    setIsDrawerOpen(false);
  }, [startNewTask]);

  const handleAllTasksDeleted = React.useCallback(() => {
    resetSession();
    setIsDrawerOpen(false);
  }, [resetSession]);

  const handleScrollToTop = React.useCallback(() => {
    messagesStartRef.current?.scrollIntoView({
      behavior: "smooth",
      block: "start",
    });
  }, []);

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

    if (!hasWorkspace) {
      return;
    }

    if (isMobile) {
      openDrawer("files");
      return;
    }

    setShowFileExplorer((prev) => !prev);
  }, [currentTab, hasWorkspace, isMobile, openDrawer]);

  const handleShareCurrentUrl = React.useCallback(async () => {
    try {
      await copyCurrentUrlToClipboard(
        window.location.href,
        navigator.clipboard.writeText.bind(navigator.clipboard),
      );
      toast.success("Current URL copied");
    } catch {
      toast.error("Failed to copy current URL");
    }
  }, []);

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
        onContextSelect={(nextContext) => {
          void handleContextSelect(nextContext);
        }}
        onNewTask={handleNewTask}
        onAllTasksDeleted={handleAllTasksDeleted}
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
      handleAllTasksDeleted,
      handleContextSelect,
      handleNewTask,
      handleSaveChatSettings,
      selectedProjectId,
    ],
  );

  const renderWorkspaceRequiredState = React.useCallback(
    () => <WorkspaceRequiredState projectName={selectedProject?.name ?? null} />,
    [selectedProject?.name],
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
  const targetSelectOptions = React.useMemo<SearchableSelectOption[]>(
    () =>
      targetOptions.map((option) => ({
        value: getTargetValue(option),
        title: option.label,
        subtitle: option.type,
        group: option.type === "agent" ? "Agent" : "Agentflow",
      })),
    [targetOptions],
  );

  const isChatTab = currentTab === "chat";
  const isFilesTab = currentTab === "files";
  const activeSidebarVisible = isChatTab ? showChatHistory : hasWorkspace && showFileExplorer;
  const activeSidebarTitle = isChatTab ? "chat history" : "file explorer";
  const isSidebarToggleDisabled = isFilesTab && !hasWorkspace;
  const sidebarToggleTitle = isSidebarToggleDisabled
    ? "Set a workspace on the Projects page to browse files"
    : isMobile
      ? `Open ${activeSidebarTitle}`
      : activeSidebarVisible
        ? `Hide ${activeSidebarTitle}`
        : `Show ${activeSidebarTitle}`;

  return (
    <div className="flex h-[calc(100vh-58px)] w-full min-w-0 flex-col gap-4 px-2 md:px-0 md:pr-2">
      <div className="flex flex-col gap-3 md:flex-row md:items-center md:justify-between">
        <div className="space-y-1">
          <h1 className="text-xl font-semibold">Chat</h1>
          <p className="text-sm text-muted-foreground">
            Select a project and target, then continue an existing task or start a new session.
          </p>
        </div>
      </div>

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

            <div className="w-[260px]">
              <SearchableSelect
                id="chat-target-select"
                ariaLabel="Select target"
                value={selectedTargetValue ?? ""}
                onValueChange={handleTargetChange}
                options={targetSelectOptions}
                placeholder="Select agent or agentflow"
                searchPlaceholder="Search agents or agentflows..."
                clearable={false}
              />
            </div>
          </div>
          <div className="flex-1" />

          <TabsList className="w-fit">
            <TabsTrigger value="chat">Chat</TabsTrigger>
            <TabsTrigger value="files">Files</TabsTrigger>
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
          <Button
            variant="ghost"
            className="cursor-pointer"
            size="sm"
            onClick={handleShareCurrentUrl}
            title="Share current URL"
            aria-label="Share current URL"
          >
            <Share2 className="h-4 w-4" />
          </Button>
        </div>

        <TabsContent value="chat" className="mt-2 flex min-h-0 flex-1">
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
                    {taskId ? ` · Task: ${taskId}` : ""}
                  </div>
                </div> */}

                <div className="relative flex h-[calc(100%-57px)] min-h-0 flex-1 flex-col border-t">
                  <Conversation
                    taskId={taskId}
                    messages={messages}
                    messagesStartRef={messagesStartRef}
                    messagesEndRef={messagesEndRef}
                  />

                  <div className="pointer-events-none absolute bottom-0 left-0 right-0 z-10 h-30 bg-linear-to-t from-bg-000 from-50% via-bg-000/80 via-70% to-transparent px-2">
                    <InputArea
                      isExecuting={isExecuting}
                      hasMessages={messages.length > 0}
                      onExecute={(value) => {
                        void handleExecute(value);
                      }}
                      onInterrupt={handleInterrupt}
                      onClearSession={resetSession}
                      onScrollToTop={handleScrollToTop}
                      userInputRef={userInputRef}
                    />
                  </div>
                </div>
              </div>
            </ColResizeSplit.Right>
          </ColResizeSplit>
        </TabsContent>

        <TabsContent value="files" className="mt-2 flex min-h-0 flex-1">
          <ColResizeSplit>
            {!isMobile && hasWorkspace && showFileExplorer ? (
              <ColResizeSplit.Left minWidth={260} maxWidth={520}>
                <Explorer
                  rootDirectory={resolvedWorkspace}
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
                {hasWorkspace ? (
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
                  renderWorkspaceRequiredState()
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
              hasWorkspace ? (
                <Explorer
                  rootDirectory={resolvedWorkspace}
                  onlyDiff={onlyDiff}
                  recursiveMode={recursiveMode}
                  onOnlyDiffChange={setOnlyDiff}
                  onFileDeleted={handleOnFileDeleted}
                  onFileSelected={handleOnFileSelected}
                  onFileReseted={handleOnFileReseted}
                  onLoadFileContent={handleOnLoadFileContent}
                />
              ) : (
                renderWorkspaceRequiredState()
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
