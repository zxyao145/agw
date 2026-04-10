"use client";

import * as React from "react";
import { Uuid4 } from "id128";
import Link from "next/link";
import { FileText, PanelLeftClose, PanelLeftOpen, Plus, Settings, Trash2 } from "lucide-react";
import { useQuery } from "@tanstack/react-query";
import { useRouter, useSearchParams } from "next/navigation";
import { toast } from "sonner";

import { getFileDiff, readFile, type GitDiffResponse } from "@/api/files";
import { apiGet } from "@/api/client";
import { getTaskDetails } from "@/api/task-client";
import { Explorer, FileContent } from "@/components/file-explorer";
import type { LineComment } from "@/components/file-explorer";
import { Conversation } from "@/components/message/conversation";
import { type UserInputRef } from "@/components/message/user-input";
import { TaskHistoryList } from "@/components/task/task-list";
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
  Select,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectLabel,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Textarea } from "@/components/ui/textarea";
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
  EMPTY_EXTRA_SETTING_TEXT,
  formatJsonObjectText,
  normalizeExtraSettingTextForStorage,
  tryParseJsonObjectText,
} from "./lib/chat-settings";
import {
  buildChatTargetOptions,
  getTargetValue,
  getTargetValueFromMetadata,
} from "./lib/target-options";
import type { ChatProjectSettingsStorageValues, ChatTargetOption, EnvVar } from "./types";

type ProjectDto = {
  id: string;
  name: string;
  workspace?: string | null;
  extraSetting?: string | null;
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

type ChatSettingsDraft = {
  workspace: string;
  envVars: EnvVar[];
  extraSettingText: string;
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
  const [draftWorkspace, setDraftWorkspace] = React.useState("");
  const [draftEnvVars, setDraftEnvVars] = React.useState<EnvVar[]>([]);
  const [draftExtraSettingText, setDraftExtraSettingText] =
    React.useState(EMPTY_EXTRA_SETTING_TEXT);

  React.useEffect(() => {
    if (!open) {
      return;
    }

    const draft = getDraft(selectedProjectId);
    setDraftWorkspace(draft.workspace);
    setDraftEnvVars(draft.envVars);
    setDraftExtraSettingText(draft.extraSettingText);
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
    const normalizedExtraSetting = tryParseJsonObjectText(draftExtraSettingText);
    if (normalizedExtraSetting === null) {
      toast.error("Extra Setting JSON must be a valid JSON object.");
      return;
    }

    const didSave = onSave({
      workspace: draftWorkspace,
      envVars: normalizeEnvVars(draftEnvVars),
      extraSettingText:
        Object.keys(normalizedExtraSetting).length === 0
          ? EMPTY_EXTRA_SETTING_TEXT
          : JSON.stringify(normalizedExtraSetting, null, 2),
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
              <Label htmlFor="chat-settings-workspace">Workspace</Label>
              <Input
                id="chat-settings-workspace"
                value={draftWorkspace}
                onChange={(event) => setDraftWorkspace(event.target.value)}
                placeholder="Leave blank to fall back to the project workspace"
              />
            </div>

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

            <div className="grid gap-2">
              <Label htmlFor="chat-settings-extra-setting">Extra Setting JSON</Label>
              <Textarea
                id="chat-settings-extra-setting"
                value={draftExtraSettingText}
                onChange={(event) => setDraftExtraSettingText(event.target.value)}
                placeholder={EMPTY_EXTRA_SETTING_TEXT}
                className="min-h-40 font-mono text-xs"
              />
              <p className="text-xs text-muted-foreground">
                Leave blank to fall back to the project extra setting. Saved per project in local
                storage and merged into <code>SettingCommand.settingContent</code> at execution
                time.
              </p>
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

  const [currentTab, setCurrentTab] = React.useState("chat");
  const [isMobile, setIsMobile] = React.useState(false);
  const [isDrawerOpen, setIsDrawerOpen] = React.useState(false);
  const [selectedProjectId, setSelectedProjectId] = React.useState<string | null>(queryProjectId);
  const [selectedTargetValue, setSelectedTargetValue] = React.useState<string | null>(null);
  const [showChatHistory, setShowChatHistory] = React.useState(true);
  const [showFileExplorer, setShowFileExplorer] = React.useState(true);
  const [messages, setMessages] = React.useState<AiMessage[]>([]);
  const [taskId, setTaskId] = React.useState<string | null>(queryTaskId);
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
  const [workspaceOverride, setWorkspaceOverride] = React.useState("");
  const [envVars, setEnvVars] = React.useState<EnvVar[]>([]);
  const [extraSettingText, setExtraSettingText] = React.useState(EMPTY_EXTRA_SETTING_TEXT);

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
    () => workspaceOverride.trim() || selectedProject?.workspace?.trim() || "",
    [selectedProject, workspaceOverride],
  );

  const hasWorkspace = resolvedWorkspace.length > 0;

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
    (projectId: string | null, taskIdValue: string | null) => {
      const nextParams = new URLSearchParams();
      if (projectId) {
        nextParams.set("projectId", projectId);
      }
      if (projectId && taskIdValue) {
        nextParams.set("taskId", taskIdValue);
      }

      const nextQuery = nextParams.toString();
      router.replace(nextQuery ? `/chat?${nextQuery}` : "/chat", { scroll: false });
    },
    [router],
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
      const ws = new WebSocket(
        `${protocol}//${window.location.host}/api/executions/${executionId}/ws`,
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
      const project = projectId ? (projects.find((item) => item.id === projectId) ?? null) : null;
      const storedSettings = projectId ? chatSettingsStorage.get(projectId) : {};
      const effectiveWorkspace =
        storedSettings.workspace?.trim() || project?.workspace?.trim() || "";

      return {
        workspace: effectiveWorkspace,
        envVars: storedSettings.envVars ?? [],
        extraSettingText:
          storedSettings.extraSettingText ?? formatJsonObjectText(project?.extraSetting),
      };
    },
    [projects],
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

  const buildSettingContent = React.useCallback(() => {
    const settingContent: Record<string, unknown> = {};

    const parsedExtraSetting = tryParseJsonObjectText(extraSettingText);
    if (parsedExtraSetting) {
      Object.assign(settingContent, parsedExtraSetting);
    }

    if (resolvedWorkspace) {
      settingContent.workspace = resolvedWorkspace;
    }

    const environmentVariables = buildEnvironmentVariables();
    if (Object.keys(environmentVariables).length > 0) {
      settingContent.environmentVariables = environmentVariables;
    }

    return JSON.stringify(settingContent);
  }, [buildEnvironmentVariables, extraSettingText, resolvedWorkspace]);

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
    (nextTaskIdValue: string) => ({
      type: "SettingCommand",
      workspace: resolvedWorkspace ?? "",
      settingContent: buildSettingContent(),
      projectId: selectedProjectId,
      taskId: nextTaskIdValue,
    }),
    [buildSettingContent, selectedProjectId],
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
    setTaskId(nextId);
    syncRoute(selectedProjectId, nextId);
    return nextId;
  }, [selectedProjectId, syncRoute, taskId]);

  const clearActiveSessionState = React.useCallback(() => {
    closeSocket("Session cleared");
    hydratedTaskKeyRef.current = null;
    setIsExecuting(false);
    setMessages([]);
    setTaskId(null);
    userInputRef.current?.setInput("");
  }, [closeSocket]);

  const resetSession = React.useCallback(() => {
    clearActiveSessionState();
    syncRoute(selectedProjectId, null);
  }, [clearActiveSessionState, selectedProjectId, syncRoute]);

  const loadTaskHistory = React.useCallback(
    async (projectId: string, nextTaskIdValue: string) => {
      const details = await getTaskDetails(projectId, nextTaskIdValue);
      const restoredTargetValue = getRestoredTargetValue(details.messages ?? []);

      closeSocket("Session switched");
      hydratedTaskKeyRef.current = `${projectId}:${details.taskId}`;
      setSelectedProjectId(projectId);
      setIsExecuting(false);
      setTaskId(details.taskId);
      setMessages(details.messages ?? []);
      if (restoredTargetValue) {
        setSelectedTargetValue(restoredTargetValue);
      }
      syncRoute(projectId, details.taskId);
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
      setWorkspaceOverride("");
      setEnvVars([]);
      setExtraSettingText(EMPTY_EXTRA_SETTING_TEXT);
      return;
    }

    const draft = getProjectSettingsDraft(selectedProjectId);
    setWorkspaceOverride(draft.workspace);
    setEnvVars(draft.envVars);
    setExtraSettingText(draft.extraSettingText);
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

      return projects[0].id;
    });
  }, [projects, queryProjectId]);

  React.useEffect(() => {
    if (targetOptions.length === 0) {
      setSelectedTargetValue(null);
      return;
    }

    setSelectedTargetValue((current) => {
      if (current) {
        return current;
      }

      if (selectedProjectId) {
        const storedTargetValue = chatSettingsStorage.get(selectedProjectId).targetValue;
        if (
          storedTargetValue &&
          targetOptions.some((option) => getTargetValue(option) === storedTargetValue)
        ) {
          return storedTargetValue;
        }
      }

      return getTargetValue(targetOptions[0]);
    });
  }, [selectedProjectId, targetOptions]);

  React.useEffect(() => {
    if (!queryProjectId) {
      clearActiveSessionState();
      return;
    }

    if (!queryTaskId) {
      clearActiveSessionState();
      setSelectedProjectId(queryProjectId);
      return;
    }

    const hydrateKey = `${queryProjectId}:${queryTaskId}`;
    if (hydratedTaskKeyRef.current === hydrateKey) {
      return;
    }

    let cancelled = false;

    void (async () => {
      try {
        const details = await getTaskDetails(queryProjectId, queryTaskId);
        const restoredTargetValue = getRestoredTargetValue(details.messages ?? []);
        if (cancelled) {
          return;
        }

        closeSocket("History loaded");
        hydratedTaskKeyRef.current = hydrateKey;
        setSelectedProjectId(queryProjectId);
        setIsExecuting(false);
        setTaskId(details.taskId);
        setMessages(details.messages ?? []);
        if (restoredTargetValue) {
          setSelectedTargetValue(restoredTargetValue);
        }
      } catch (error) {
        if (!cancelled) {
          toast.error(`Failed to load task history: ${getApiErrorMessage(error)}`);
        }
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [clearActiveSessionState, closeSocket, queryProjectId, queryTaskId]);

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
      syncRoute(nextProjectId, null);
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

  const handleTaskSelect = React.useCallback(
    async (nextTaskIdValue: string) => {
      if (!selectedProjectId) {
        toast.error("Please select a project");
        return;
      }

      try {
        await loadTaskHistory(selectedProjectId, nextTaskIdValue);
        setIsDrawerOpen(false);
      } catch (error) {
        toast.error(`Failed to load session: ${getApiErrorMessage(error)}`);
      }
    },
    [loadTaskHistory, selectedProjectId],
  );

  const handleTaskDeleted = React.useCallback(
    (deletedTaskId: string) => {
      if (deletedTaskId === taskId) {
        resetSession();
        setIsDrawerOpen(false);
      }
    },
    [resetSession, taskId],
  );

  const handleNewTask = React.useCallback(() => {
    resetSession();
    setIsDrawerOpen(false);
  }, [resetSession]);

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

      const normalizedExtraSetting = tryParseJsonObjectText(draft.extraSettingText);
      if (normalizedExtraSetting === null) {
        toast.error("Extra Setting JSON must be a valid JSON object.");
        return false;
      }

      const projectWorkspace = selectedProject?.workspace?.trim() || "";
      const normalizedWorkspace = draft.workspace.trim();
      const normalizedSettings: ChatProjectSettingsStorageValues = {
        workspace:
          !normalizedWorkspace || normalizedWorkspace === projectWorkspace
            ? undefined
            : normalizedWorkspace,
        envVars: normalizeEnvVars(draft.envVars),
        extraSettingText: normalizeExtraSettingTextForStorage(
          draft.extraSettingText,
          selectedProject?.extraSetting,
        ),
      };

      chatSettingsStorage.set(selectedProjectId, normalizedSettings);
      const nextDraft = getProjectSettingsDraft(selectedProjectId);
      setWorkspaceOverride(nextDraft.workspace);
      setEnvVars(nextDraft.envVars);
      setExtraSettingText(nextDraft.extraSettingText);

      toast.success("Chat settings saved");
      return true;
    },
    [getProjectSettingsDraft, selectedProject, selectedProjectId],
  );

  const renderTaskHistory = React.useCallback(
    () => (
      <TaskHistoryList
        projectId={selectedProjectId ?? ""}
        currentTaskId={taskId}
        onTaskSelect={(nextTaskIdValue) => {
          void handleTaskSelect(nextTaskIdValue);
        }}
        onNewTask={handleNewTask}
        onTaskDeleted={handleTaskDeleted}
        onAllTasksDeleted={handleAllTasksDeleted}
        headerActions={
          <ChatSettingsDialog
            selectedProjectId={selectedProjectId}
            getDraft={getProjectSettingsDraft}
            onSave={handleSaveChatSettings}
          />
        }
      />
    ),
    [
      getProjectSettingsDraft,
      handleAllTasksDeleted,
      handleNewTask,
      handleSaveChatSettings,
      handleTaskDeleted,
      handleTaskSelect,
      selectedProjectId,
      taskId,
    ],
  );

  const renderWorkspaceRequiredState = React.useCallback(
    () => <WorkspaceRequiredState projectName={selectedProject?.name ?? null} />,
    [selectedProject?.name],
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
            <Select value={selectedProjectId ?? undefined} onValueChange={handleProjectChange}>
              <SelectTrigger className="w-[220px]" aria-label="Select project">
                <SelectValue placeholder="Select project" />
              </SelectTrigger>
              <SelectContent position="popper" side="bottom" align="start" sideOffset={4}>
                {projects.map((project) => (
                  <SelectItem key={project.id} value={project.id}>
                    {project.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>

            <Select value={selectedTargetValue ?? undefined} onValueChange={handleTargetChange}>
              <SelectTrigger className="w-[260px]" aria-label="Select target">
                <SelectValue placeholder="Select agent or agentflow" />
              </SelectTrigger>
              <SelectContent position="popper" side="bottom" align="start" sideOffset={4}>
                <SelectGroup>
                  <SelectLabel>Agent</SelectLabel>
                  {targetOptions
                    .filter((option) => option.type === "agent")
                    .map((option) => (
                      <SelectItem key={getTargetValue(option)} value={getTargetValue(option)}>
                        {option.label}
                      </SelectItem>
                    ))}
                </SelectGroup>
                <SelectGroup>
                  <SelectLabel>Agentflow</SelectLabel>
                  {targetOptions
                    .filter((option) => option.type === "agentflow")
                    .map((option) => (
                      <SelectItem key={getTargetValue(option)} value={getTargetValue(option)}>
                        {option.label}
                      </SelectItem>
                    ))}
                </SelectGroup>
              </SelectContent>
            </Select>
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
        </div>

        <TabsContent value="chat" className="mt-2 flex min-h-0 flex-1">
          <ColResizeSplit>
            {!isMobile && isChatTab && showChatHistory ? (
              <ColResizeSplit.Left minWidth={260} maxWidth={520}>
                {renderTaskHistory()}
              </ColResizeSplit.Left>
            ) : null}

            <ColResizeSplit.Right>
              <div className="relative flex flex-col min-h-105 flex-1 overflow-hidden">
                <div className="border-b px-4 py-3">
                  <div className="text-xs text-muted-foreground">
                    {selectedProjectId
                      ? `Project: ${projects.find((project) => project.id === selectedProjectId)?.name ?? selectedProjectId}`
                      : "Select a project to begin"}
                    {selectedTarget ? ` · Target: ${selectedTarget.type}` : ""}
                    {taskId ? ` · Task: ${taskId}` : ""}
                  </div>
                </div>

                <div className="relative flex h-[calc(100%-57px)] min-h-0 flex-1 flex-col">
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
              renderTaskHistory()
            )}
          </div>
        </DrawerContent>
      </Drawer>
    </div>
  );
}
