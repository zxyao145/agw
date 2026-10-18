"use client";

import * as React from "react";
import {
  FileText,
  PanelLeftClose,
  PanelLeftOpen,
  Plus,
  Settings,
  Trash2,
  Info,
} from "lucide-react";
import { useQuery } from "@agw/components/query";
import { useRouter, useSearchParams } from "next/navigation";
import { toast } from "sonner";

import {
  getProjectDirectories,
  getFileDiff,
  readFile,
  type GitDiffResponse,
  type GitDiffScope,
  type ProjectDirectory,
} from "@agw/projects";
import { apiGet } from "@agw/api";
import {
  getProjectConversationDetails,
  getProjectConversationMessages,
  type ConversationResumeState,
  type ConversationSummary,
} from "@agw/projects";
import { AgentSelector, type AgentSelection } from "../../components/agent-selector";
import { Explorer, FileContent, type FileViewerOption } from "@agw/projects";
import type {
  ConversationItemActionArgs,
  ExplorerContextMenuArgs,
  ExtensionMenuItem,
  LineComment,
  ProjectDirectoryOption,
} from "@agw/projects";
import {
  Chat,
  type ChatSessionSeed,
  type ConversationChangeOptions,
} from "../../components/message/chat";
import type {
  ChatComposerHandle,
  ChatMessageActionArgs,
} from "../../components/message/chat-extension-types";
import { ExecutionReconnectingDialog } from "../../components/message/execution-reconnecting-dialog";
import { ConversationList } from "@agw/projects";
import { Button, formatFriendlyLocalDateTime2 } from "@agw/components";
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
import { Switch } from "@agw/components";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@agw/components";
import { EMPTY_TOKEN_USAGE } from "@agw/api";
import { buildChatHref } from "../../../lib/chat-route";
import { conversationComposerStorage } from "../../../lib/chat/conversation-composer-storage";
import { conversationImageDrafts } from "../../../lib/chat/conversation-image-drafts";
import { notifyImageDraftError } from "../../components/message/use-conversation-image-draft";
import { cn } from "@agw/components";
import { chatSettingsStorage } from "./settings-storage";
import ColResizeSplit from "./components/split-layout";
import { ProviderSessionsSection } from "./components/provider-sessions-section";
import {
  CHAT_SETTINGS_DIALOG_BODY_CLASS_NAME,
  CHAT_SETTINGS_DIALOG_CONTENT_CLASS_NAME,
} from "./lib/chat-settings";
import {
  getChatRouteSessionAction,
  getConversationHydrationKey,
  getRouteHydrationKey,
} from "./lib/session-routing";
import {
  buildChatTargetOptions,
  getTargetValue,
  getTargetValueFromMetadata,
} from "./lib/target-options";
import type { ChatProjectSettingsStorageValues, ChatTargetOption, EnvVar } from "./types";
import { getApiErrorMessage } from "@agw/api";
import {
  executionSessionManager,
  getExecutionReconnectProgress,
  migrateDurableExecutionAttachment,
  type ConversationStatus,
  type ExecutionReconnectState,
} from "@agw/chat-runtime";
import { useExecutionPlatform } from "../../execution-platform";
import { useConversationStatuses } from "../../conversation-statuses";

export type ChatWorkspaceProject = {
  id: string;
  name: string;
  workspace?: string | null;
  additionalDirectories?: ProjectDirectory[] | null;
};

type AgentDto = {
  id: string;
  displayName: string;
  name: string;
  enable: boolean;
  resultFormat?: import("@agw/api").components["schemas"]["ResultFormat"];
};

type AgentflowDto = {
  id: string;
  name: string;
  enable: boolean;
};

const DEFAULT_PROJECT_VALUE = "default-built-in";
const DEFAULT_AGENT_LABEL = "Hello";

export type ChatWorkspaceProps = {
  routeBasePath: string;
  showProjectSelect: boolean;
  compactToolbar?: boolean;
  showUserInputNavigation?: boolean;
};

export type ChatWorkspaceProviderProps = {
  routeBasePath: string;
  /**
   * 当前文件改变前的确认（选择其他文件、切换目录或 Project）；返回 false 时保留当前文件。
   * Confirmation before the current file changes (another file, directory or Project); false keeps the current file.
   */
  confirmFileChange?: () => Promise<boolean>;
  children?: React.ReactNode;
};

/** ChatWorkspaceProvider 提供的状态与回调。State and callbacks provided by ChatWorkspaceProvider. */
export type ChatWorkspaceContextValue = {
  /** 执行连接正在可见重连，此时工作区应设置 inert。The execution connection is visibly reconnecting, so the workspace should be inert. */
  isReconnecting: boolean;
  /** 项目、Agent、Agentflow 列表的加载错误；没有错误时为 null。The error loading projects, Agents or Agentflows; null without one. */
  dependencyErrorMessage: string | null;
  projects: readonly ChatWorkspaceProject[];
  selectedProjectId: string | null;
  selectedProject: ChatWorkspaceProject | null;
  /** 切换项目并开始新对话。Switches the project and starts a new conversation. */
  selectProject: (projectId: string) => void;
  conversationId: string | null;
  selectedTarget: ChatTargetOption | null;
  targetOptions: readonly ChatTargetOption[];
  /** 触发对话列表刷新。Triggers a conversation list refresh. */
  refreshConversations: () => void;
  /** 由 ChatConversationPane 传给 Chat 的输入框句柄。The composer handle ChatConversationPane passes to Chat. */
  composerRef: React.RefObject<ChatComposerHandle | null>;

  projectDirectories: readonly ProjectDirectoryOption[];
  /** 文件浏览的当前目录。The directory the file explorer browses. */
  selectedDirectory: ProjectDirectoryOption | undefined;
  selectDirectory: (directoryId: string | null) => void;
  resolvedWorkspace: string;

  conversationListRefreshSignal: number;
  conversationStatuses: ReadonlyMap<string, ConversationStatus>;
  currentConversationTurnId: string | null;
  /** 加载并打开对话；成功时返回 true。Loads and opens a conversation; resolves true on success. */
  openConversation: (conversation: ConversationSummary) => Promise<boolean>;
  startNewConversation: () => void;
  resetSession: () => void;
  handleConversationDeleted: (conversationId: string) => void;
  handleProjectConversationsCleared: () => void;
  getSettingsDraft: (projectId: string | null) => ChatSettingsDraft;
  saveSettings: (draft: ChatSettingsDraft) => boolean;

  onlyDiff: boolean;
  setOnlyDiff: (onlyDiff: boolean) => void;
  recursiveMode: boolean;
  /** 选中文件；文件与 diff scope 都未变化时不重新加载。Selects a file; nothing reloads when neither the file nor the diff scope changes. */
  selectFile: (filePath: string | null, scope?: GitDiffScope) => void;
  /**
   * 确认后选中目录中的文件；用户取消时返回 false。
   * Selects a file in a directory after confirmation; resolves false when the user cancels.
   */
  openFile: (directoryId: string | null, filePath: string) => Promise<boolean>;
  handleFileDeleted: (filePath: string) => void;
  handleFileReset: (filePath: string | null) => void;
  handleFileGitScopeChanged: (path: string, targetScope: GitDiffScope) => void;
  selectedFile: string | null;
  selectedDiffScope: GitDiffScope | undefined;
  fileContent: string;
  isLoadingContent: boolean;
  contentError: string | null;
  diffContentData: GitDiffResponse | null;
  /** 文件行评论，发送时进入 Chat 的 pendingFileComments。File line comments that become Chat's pendingFileComments. */
  comments: LineComment[];
  setComments: React.Dispatch<React.SetStateAction<LineComment[]>>;

  agents: readonly AgentDto[] | undefined;
  selectAgent: (selection: AgentSelection) => void;
  chatSessionSeed: ChatSessionSeed;
  isLoadingConversation: boolean;
  restoreExecution: boolean;
  environmentVariables: Record<string, string>;
  resultOnly: boolean;
  searchDirectoryIds: readonly (string | null)[];
  handleConversationAccepted: (conversationId: string) => void;
  handleConversationChange: (options?: ConversationChangeOptions) => void;
  removePendingFileComments: (commentIds: readonly string[]) => void;
  handleReconnectStateChange: (
    state: ExecutionReconnectState | null,
    retry: () => Promise<void>,
  ) => void;
};

const ChatWorkspaceContext = React.createContext<ChatWorkspaceContextValue | null>(null);

/** 读取 ChatWorkspaceProvider 的状态。Reads the state of ChatWorkspaceProvider. */
export function useChatWorkspace(): ChatWorkspaceContextValue {
  const value = React.useContext(ChatWorkspaceContext);
  if (!value) throw new Error("useChatWorkspace must be used inside ChatWorkspaceProvider.");
  return value;
}

function getResumeTargetValue(resumeState: ConversationResumeState | null): string | null {
  return getTargetValueFromMetadata(resumeState?.targetType, resumeState?.targetId);
}

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === "AbortError";
}

type ChatSettingsDraft = {
  envVars: EnvVar[];
  resultOnly: boolean;
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
  serverId: string;
  selectedProjectId: string | null;
  conversationId: string | null;
  currentConversation: ConversationSummary | null;
  conversationRunning: boolean;
  getDraft: (projectId: string | null) => ChatSettingsDraft;
  onSave: (draft: ChatSettingsDraft) => boolean;
};

function ChatSettingsDialog({
  serverId,
  selectedProjectId,
  conversationId,
  currentConversation,
  conversationRunning,
  getDraft,
  onSave,
}: ChatSettingsDialogProps) {
  const [open, setOpen] = React.useState(false);
  const [draftEnvVars, setDraftEnvVars] = React.useState<EnvVar[]>([]);
  const [draftResultOnly, setDraftResultOnly] = React.useState(false);

  React.useEffect(() => {
    if (!open) {
      return;
    }

    const draft = getDraft(selectedProjectId);
    setDraftEnvVars(draft.envVars);
    setDraftResultOnly(draft.resultOnly);
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
      resultOnly: draftResultOnly,
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
          <Info className="h-4 w-4" />
        </Button>
      </DialogTrigger>
      <DialogContent size="lg" className={CHAT_SETTINGS_DIALOG_CONTENT_CLASS_NAME}>
        <DialogHeader>
          <DialogTitle>Conversation Settings</DialogTitle>
        </DialogHeader>

        <div className={CHAT_SETTINGS_DIALOG_BODY_CLASS_NAME}>
          <div className="grid gap-4 py-2">
            <div className="grid gap-2">
              <Label>Conversation Information</Label>
              {conversationId === null ? (
                <div className="rounded-lg bg-muted/50 px-4 py-4 text-sm text-muted-foreground">
                  No active conversation.
                </div>
              ) : (
                <div className="rounded-md border text-xs">
                  <div className="flex items-start justify-between gap-3 border-b p-2">
                    <span className="text-muted-foreground">ID</span>
                    <span className="font-mono break-all text-right">{conversationId}</span>
                  </div>
                  <div className="flex items-start justify-between gap-3 border-b p-2">
                    <span className="text-muted-foreground">Messages</span>
                    <span className="text-right">{currentConversation?.messageCount ?? "—"}</span>
                  </div>
                  <div className="flex items-start justify-between gap-3 border-b p-2">
                    <span className="text-muted-foreground">Created</span>
                    <span className="text-right">
                      {currentConversation
                        ? formatFriendlyLocalDateTime2(currentConversation.createTime)
                        : "—"}
                    </span>
                  </div>
                  <div className="flex items-start justify-between gap-3 p-2">
                    <span className="text-muted-foreground">Updated</span>
                    <span className="text-right">
                      {currentConversation?.updateTime
                        ? formatFriendlyLocalDateTime2(currentConversation.updateTime)
                        : "—"}
                    </span>
                  </div>
                </div>
              )}
            </div>

            {selectedProjectId && conversationId && (
              <ProviderSessionsSection
                serverId={serverId}
                projectId={selectedProjectId}
                conversationId={conversationId}
                conversationRunning={conversationRunning}
              />
            )}

            <div className="flex items-start justify-between gap-4 rounded-lg bg-muted/50 px-4 py-3">
              <div className="space-y-1">
                <Label htmlFor="chat-settings-result-only" className="cursor-pointer">
                  Only Stream Turn Result
                </Label>
                <p className="text-xs text-muted-foreground">
                  Stream only the result message and decline questions and tool approvals. Applies
                  to external Agents and to system Agents with Generate Turn Summary enabled.
                </p>
              </div>
              <Switch
                id="chat-settings-result-only"
                checked={draftResultOnly}
                onCheckedChange={setDraftResultOnly}
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
                <div className="rounded-lg bg-muted/50 px-4 py-4 text-sm text-muted-foreground">
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

/**
 * 对话工作区中与布局无关的状态、effect 与回调，并渲染重连对话框。对话框按绝对定位覆盖最近的定位祖先，
 * 因此 Provider 应放在定位容器中。
 * Layout-independent state, effects and callbacks of the chat workspace; also renders the reconnect
 * dialog. The dialog is absolutely positioned over the nearest positioned ancestor, so place the
 * Provider inside a positioned container.
 */
export function ChatWorkspaceProvider({
  routeBasePath,
  confirmFileChange,
  children,
}: ChatWorkspaceProviderProps) {
  const router = useRouter();
  const confirmFileChangeRef = React.useRef(confirmFileChange);
  confirmFileChangeRef.current = confirmFileChange;
  const guardFileChange = React.useCallback(
    async () => (confirmFileChangeRef.current ? confirmFileChangeRef.current() : true),
    [],
  );
  // 没有确认函数时同步执行，Web 的行为不变。Without a confirmation the action runs synchronously, as before on Web.
  const afterFileChangeConfirmed = React.useCallback((action: () => void) => {
    const confirm = confirmFileChangeRef.current;
    if (!confirm) {
      action();
      return;
    }
    void confirm().then((confirmed) => {
      if (confirmed) action();
    });
  }, []);
  const searchParams = useSearchParams();
  const executionServerId = useExecutionPlatform().serverId;
  const queryProjectId = searchParams.get("projectId");
  const queryConversationId = searchParams.get("conversationId");

  const [executionReconnectState, setExecutionReconnectState] =
    React.useState<ExecutionReconnectState | null>(null);
  const reconnectRetryRef = React.useRef<(() => Promise<void>) | null>(null);
  const [selectedProjectId, setSelectedProjectId] = React.useState<string | null>(queryProjectId);
  const [selectedTargetValue, setSelectedTargetValue] = React.useState<string | null>(null);
  // 清空目标后要求重新确定目标；同一批更新中先选中再清空时目标值不变，靠它让选择默认目标的 effect 再次运行。
  // Requests target resolution after a clear; when a selection and a clear land in one batch the value is unchanged,
  // so this makes the default-target effect run again.
  const [targetResolution, setTargetResolution] = React.useState(0);
  const [conversationId, setConversationId] = React.useState<string | null>(queryConversationId);
  const [chatSessionSeed, setChatSessionSeed] = React.useState<ChatSessionSeed>({
    revision: 0,
    conversationId: null,
    messages: [],
    historyTurns: [],
    usage: EMPTY_TOKEN_USAGE,
    olderMessagesCursor: null,
    hasOlderMessages: false,
    agentMode: null,
  });
  const [isLoadingConversation, setIsLoadingConversation] = React.useState(false);
  const [conversationListRefreshSignal, setConversationListRefreshSignal] = React.useState(0);
  const [selectedDirectoryId, setSelectedDirectoryId] = React.useState<string | null>(null);
  const [selectedFile, setSelectedFile] = React.useState<string | null>(null);
  const [selectedDiffScope, setSelectedDiffScope] = React.useState<GitDiffScope | undefined>();
  const [fileContent, setFileContent] = React.useState("");
  const [isLoadingContent, setIsLoadingContent] = React.useState(false);
  const [contentError, setContentError] = React.useState<string | null>(null);
  const [onlyDiff, setOnlyDiff] = React.useState(false);
  const [recursiveMode] = React.useState(true);
  const [diffContentData, setDiffContentData] = React.useState<GitDiffResponse | null>(null);
  const [comments, setComments] = React.useState<LineComment[]>([]);
  const [envVars, setEnvVars] = React.useState<EnvVar[]>([]);
  const [resultOnly, setResultOnly] = React.useState(false);
  const composerRef = React.useRef<ChatComposerHandle | null>(null);

  const hydratedConversationKeyRef = React.useRef<string | null>(null);
  const conversationLoadAbortRef = React.useRef<AbortController | null>(null);
  const fileLoadGenerationRef = React.useRef(0);

  React.useEffect(
    () => () => {
      conversationLoadAbortRef.current?.abort();
    },
    [],
  );

  const projectsQuery = useQuery({
    queryKey: ["projects"],
    queryFn: async () => (await apiGet("/api/projects")) as ChatWorkspaceProject[],
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

  const projectDirectories = React.useMemo(
    () => (selectedProject ? getProjectDirectories(selectedProject) : []),
    [selectedProject],
  );
  const searchDirectoryIds = React.useMemo(
    () => projectDirectories.map((directory) => directory.id),
    [projectDirectories],
  );
  const selectedDirectory =
    projectDirectories.find((directory) => directory.id === selectedDirectoryId) ??
    projectDirectories[0];
  const resolvedWorkspace = selectedDirectory?.path ?? "";
  React.useEffect(() => {
    setSelectedDirectoryId(null);
  }, [selectedProjectId]);
  React.useEffect(() => {
    if (
      selectedDirectoryId &&
      !projectDirectories.some((directory) => directory.id === selectedDirectoryId)
    ) {
      setSelectedDirectoryId(null);
    }
  }, [projectDirectories, selectedDirectoryId]);

  const syncRoute = React.useCallback(
    (projectId: string | null, conversationIdValue: string | null = null) => {
      const nextHref = buildChatHref(routeBasePath, {
        projectId,
        conversationId: conversationIdValue,
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

  const refreshConversationList = React.useCallback((options?: ConversationChangeOptions) => {
    if (options?.cancelConversationLoad) {
      conversationLoadAbortRef.current?.abort();
      conversationLoadAbortRef.current = null;
      setIsLoadingConversation(false);
    }
    setConversationListRefreshSignal((signal) => signal + 1);
  }, []);

  const replaceChatSession = React.useCallback((nextSession: Omit<ChatSessionSeed, "revision">) => {
    setChatSessionSeed((current) => ({
      ...nextSession,
      revision: Number(current.revision) + 1,
    }));
  }, []);

  const clearFilePreview = React.useCallback((clearComments = true) => {
    fileLoadGenerationRef.current += 1;
    setIsLoadingContent(false);
    setSelectedFile(null);
    setSelectedDiffScope(undefined);
    setFileContent("");
    setContentError(null);
    setDiffContentData(null);
    if (clearComments) setComments([]);
  }, []);

  const handlePendingFileCommentsRemove = React.useCallback((commentIds: readonly string[]) => {
    if (commentIds.length === 0) return;

    const commentIdSet = new Set(commentIds);
    setComments((current) => current.filter((comment) => !commentIdSet.has(comment.id)));
  }, []);

  const getProjectSettingsDraft = React.useCallback(
    (projectId: string | null): ChatSettingsDraft => {
      const storedSettings = projectId ? chatSettingsStorage.get(projectId) : {};

      return {
        envVars: storedSettings.envVars ?? [],
        resultOnly: storedSettings.resultOnly ?? false,
      };
    },
    [],
  );

  const getActiveSettingsDraft = React.useCallback(
    (projectId: string | null): ChatSettingsDraft => {
      if (projectId && projectId === selectedProjectId) {
        return {
          envVars,
          resultOnly,
        };
      }

      return getProjectSettingsDraft(projectId);
    },
    [envVars, getProjectSettingsDraft, resultOnly, selectedProjectId],
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
    async (filePath: string, diffScope?: GitDiffScope) => {
      const generation = ++fileLoadGenerationRef.current;
      setIsLoadingContent(true);
      setContentError(null);
      setDiffContentData(null);

      try {
        if (onlyDiff) {
          if (!selectedProjectId) {
            throw new Error("Select a project before loading files");
          }
          const diff = await getFileDiff(
            selectedProjectId,
            filePath,
            diffScope,
            undefined,
            selectedDirectory?.id,
          );
          if (generation !== fileLoadGenerationRef.current) {
            return;
          }
          setDiffContentData(diff);
          setFileContent("");
          setSelectedFile(filePath);
          setSelectedDiffScope(diffScope);
        } else {
          if (!selectedProjectId) {
            throw new Error("Select a project before loading files");
          }
          const content = await readFile(
            selectedProjectId,
            filePath,
            undefined,
            selectedDirectory?.id,
          );
          if (generation !== fileLoadGenerationRef.current) {
            return;
          }
          setFileContent(content);
          setDiffContentData(null);
          setSelectedFile(filePath);
          setSelectedDiffScope(diffScope);
        }
      } catch (error) {
        if (generation !== fileLoadGenerationRef.current) {
          return;
        }
        console.error("Error loading file:", error);
        setContentError((error as Error).message);
        setFileContent("");
        setDiffContentData(null);
      } finally {
        if (generation === fileLoadGenerationRef.current) {
          setIsLoadingContent(false);
        }
      }
    },
    [onlyDiff, selectedProjectId, selectedDirectory?.id],
  );

  const handleOnFileDeleted = React.useCallback(
    (filePath: string) => {
      if (filePath === selectedFile) {
        clearFilePreview();
      }
    },
    [clearFilePreview, selectedFile],
  );

  const handleOnFileReseted = React.useCallback(
    (filePath: string | null) => {
      if (selectedFile && selectedFile === filePath) {
        void loadFileContent(selectedFile, selectedDiffScope);
      }
    },
    [loadFileContent, selectedDiffScope, selectedFile],
  );

  const handleOnFileGitScopeChanged = React.useCallback(
    (path: string, targetScope: GitDiffScope) => {
      const directoryPrefix = `${path.replace(/\/+$/u, "")}/`;
      if (selectedFile === path || selectedFile?.startsWith(directoryPrefix)) {
        setSelectedDiffScope(targetScope);
      }
    },
    [selectedFile],
  );

  const handleOnFileSelected = React.useCallback(
    (filePath: string | null, scope?: GitDiffScope) => {
      if (!filePath || (filePath === selectedFile && scope === selectedDiffScope)) return;
      if (filePath === selectedFile) {
        void loadFileContent(filePath, scope);
        return;
      }
      afterFileChangeConfirmed(() => void loadFileContent(filePath, scope));
    },
    [afterFileChangeConfirmed, loadFileContent, selectedDiffScope, selectedFile],
  );

  // 其他目录中的文件在目录切换并清空预览之后加载。A file in another directory loads after the switch clears the preview.
  const pendingFileRef = React.useRef<{ directoryId: string | null; path: string } | null>(null);
  const openFile = React.useCallback(
    async (directoryId: string | null, filePath: string) => {
      const currentDirectoryId = selectedDirectory?.id ?? null;
      if (directoryId === currentDirectoryId && filePath === selectedFile) return true;
      if (!(await guardFileChange())) return false;
      if (directoryId === currentDirectoryId) {
        await loadFileContent(filePath);
      } else {
        pendingFileRef.current = { directoryId, path: filePath };
        setSelectedDirectoryId(directoryId);
      }
      return true;
    },
    [guardFileChange, loadFileContent, selectedDirectory?.id, selectedFile],
  );

  const selectDirectory = React.useCallback(
    (directoryId: string | null) => {
      if (directoryId === selectedDirectoryId) return;
      afterFileChangeConfirmed(() => setSelectedDirectoryId(directoryId));
    },
    [afterFileChangeConfirmed, selectedDirectoryId],
  );

  const clearLocalSessionState = React.useCallback(() => {
    conversationLoadAbortRef.current?.abort();
    conversationLoadAbortRef.current = null;
    hydratedConversationKeyRef.current = null;
    setIsLoadingConversation(false);
    setConversationId(null);
    // 新对话重新确定执行目标，不沿用上一个对话的选择。
    // A new conversation resolves its own target instead of keeping the previous conversation's one.
    setSelectedTargetValue(null);
    setTargetResolution((value) => value + 1);
    replaceChatSession({
      conversationId: null,
      messages: [],
      historyTurns: [],
      usage: EMPTY_TOKEN_USAGE,
      olderMessagesCursor: null,
      hasOlderMessages: false,
      agentMode: null,
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

  const loadConversationHistory = React.useCallback(
    async (projectId: string, nextConversationId: string, signal?: AbortSignal) => {
      conversationLoadAbortRef.current?.abort();
      const abortController = new AbortController();
      conversationLoadAbortRef.current = abortController;
      setIsLoadingConversation(true);
      const abortFromCaller = () => abortController.abort();
      signal?.addEventListener("abort", abortFromCaller, { once: true });

      try {
        const [details, messagePage] = await Promise.all([
          getProjectConversationDetails(
            projectId,
            nextConversationId,
            undefined,
            abortController.signal,
          ),
          getProjectConversationMessages(projectId, nextConversationId, {
            direction: "older",
            pageSize: 50,
            signal: abortController.signal,
          }),
        ]);
        abortController.signal.throwIfAborted();
        // 本地为该对话选择过的目标优先，其次是服务端记录的最近目标。
        // The target chosen locally for this conversation wins over the server's latest target.
        const restoredTargetValue =
          conversationComposerStorage.get(
            { serverId: executionServerId, projectId },
            details.conversationId,
          ).targetValue ?? getResumeTargetValue(details.resumeState);

        // 按执行上下文保存的旧 durable attachment 必须在 Chat 恢复配置之前移到对话键下。
        // An old durable attachment saved by execution context must move to the conversation key before Chat restores its configuration.
        migrateDurableExecutionAttachment({
          projectId,
          conversationId: details.conversationId,
          contextId: details.contextId,
        });
        hydratedConversationKeyRef.current = getConversationHydrationKey(
          projectId,
          details.conversationId,
        );
        setSelectedProjectId(projectId);
        setConversationId(details.conversationId);
        replaceChatSession({
          conversationId: details.conversationId,
          messages: messagePage.items,
          historyTurns: messagePage.turns,
          usage: details.usage,
          olderMessagesCursor: messagePage.nextCursor,
          hasOlderMessages: messagePage.hasMore,
          agentMode:
            details.resumeState?.agentMode === "plan" ||
            details.resumeState?.agentMode === "execute"
              ? details.resumeState.agentMode
              : null,
        });
        setSelectedTargetValue(restoredTargetValue);
        syncRoute(projectId, details.conversationId);
        return details;
      } finally {
        signal?.removeEventListener("abort", abortFromCaller);
        if (conversationLoadAbortRef.current === abortController) {
          conversationLoadAbortRef.current = null;
          setIsLoadingConversation(false);
        }
      }
    },
    [executionServerId, replaceChatSession, syncRoute],
  );

  React.useEffect(() => {
    if (!selectedProjectId) {
      setEnvVars((current) => (current.length === 0 ? current : []));
      setResultOnly(false);
      return;
    }

    const draft = getProjectSettingsDraft(selectedProjectId);
    setEnvVars((current) => (areEnvVarsEqual(current, draft.envVars) ? current : draft.envVars));
    setResultOnly(draft.resultOnly);
  }, [getProjectSettingsDraft, selectedProjectId]);

  React.useEffect(() => {
    if (selectedFile) {
      void loadFileContent(selectedFile, selectedDiffScope);
    }
  }, [loadFileContent, onlyDiff, selectedDiffScope, selectedFile]);

  React.useEffect(() => {
    clearFilePreview(false);
  }, [clearFilePreview, resolvedWorkspace, selectedProjectId, selectedDirectory?.id]);
  React.useEffect(() => {
    const pending = pendingFileRef.current;
    if (!pending || (selectedDirectory?.id ?? null) !== pending.directoryId) return;
    pendingFileRef.current = null;
    void loadFileContent(pending.path);
  }, [loadFileContent, selectedDirectory?.id]);
  React.useEffect(() => {
    setComments([]);
  }, [selectedProjectId]);

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

  // 当前目标为空或不可用时，依次采用对话的本地选择、项目最近的选择、默认 Agent、第一个目标。
  // 目标列表加载前保留当前值，避免清掉对话恢复出的目标。
  // When the current target is empty or unavailable, use the conversation's local choice, the project's
  // latest choice, the default Agent, then the first target. Keep the value until the target list loads.
  React.useEffect(() => {
    if (targetOptions.length === 0) {
      return;
    }

    const isAvailable = (value: string | null | undefined): value is string =>
      Boolean(value) && targetOptions.some((option) => getTargetValue(option) === value);
    if (isAvailable(selectedTargetValue)) {
      return;
    }

    if (!selectedProjectId) {
      setSelectedTargetValue(null);
      return;
    }

    const storedTargetValue = [
      conversationComposerStorage.get(
        { serverId: executionServerId, projectId: selectedProjectId },
        conversationId,
      ).targetValue,
      chatSettingsStorage.get(selectedProjectId).targetValue,
    ].find(isAvailable);
    if (storedTargetValue) {
      setSelectedTargetValue(storedTargetValue);
      return;
    }

    const defaultAgent = targetOptions.find(
      (option) => option.type === "agent" && option.label === DEFAULT_AGENT_LABEL,
    );
    setSelectedTargetValue(getTargetValue(defaultAgent ?? targetOptions[0]));
  }, [
    conversationId,
    executionServerId,
    selectedProjectId,
    selectedTargetValue,
    targetOptions,
    targetResolution,
  ]);

  React.useEffect(() => {
    const routeAction = getChatRouteSessionAction({
      queryProjectId,
      queryConversationId,
      hydratedRouteKey: hydratedConversationKeyRef.current,
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

    let cancelled = false;
    const abortController = new AbortController();

    void (async () => {
      try {
        if (routeAction.type === "hydrateConversation") {
          const details = await loadConversationHistory(
            routeAction.projectId,
            routeAction.conversationId,
            abortController.signal,
          );
          if (cancelled) {
            return;
          }

          hydratedConversationKeyRef.current = routeAction.hydrateKey;
          syncRoute(routeAction.projectId, details.conversationId);
          return;
        }
      } catch (error) {
        if (!cancelled && !isAbortError(error)) {
          if (hydrationKey && hydratedConversationKeyRef.current === hydrationKey) {
            hydratedConversationKeyRef.current = null;
          }
          toast.error(`Failed to load chat history: ${getApiErrorMessage(error)}`);
        }
      }
    })();

    return () => {
      cancelled = true;
      abortController.abort();
    };
  }, [
    clearLocalSessionState,
    loadConversationHistory,
    queryConversationId,
    queryProjectId,
    syncRoute,
  ]);

  const handleProjectChange = React.useCallback(
    (nextProjectId: string) => {
      if (nextProjectId === selectedProjectId) {
        return;
      }

      conversationLoadAbortRef.current?.abort();
      conversationLoadAbortRef.current = null;
      hydratedConversationKeyRef.current = null;
      setSelectedProjectId(nextProjectId);
      setSelectedTargetValue(null);
      setConversationId(null);
      replaceChatSession({
        conversationId: null,
        messages: [],
        historyTurns: [],
        usage: EMPTY_TOKEN_USAGE,
        olderMessagesCursor: null,
        hasOlderMessages: false,
        agentMode: null,
      });
      syncRoute(nextProjectId, null);
    },
    [replaceChatSession, selectedProjectId, syncRoute],
  );

  const selectProject = React.useCallback(
    (nextProjectId: string) => {
      if (nextProjectId === selectedProjectId) return;
      afterFileChangeConfirmed(() => handleProjectChange(nextProjectId));
    },
    [afterFileChangeConfirmed, handleProjectChange, selectedProjectId],
  );

  const handleTargetChange = React.useCallback(
    (nextTargetValue: string) => {
      if (nextTargetValue === selectedTargetValue) {
        return;
      }

      setSelectedTargetValue(nextTargetValue);
      if (selectedProjectId) {
        chatSettingsStorage.set(selectedProjectId, { targetValue: nextTargetValue });
        conversationComposerStorage.set(
          { serverId: executionServerId, projectId: selectedProjectId },
          conversationId,
          { targetValue: nextTargetValue },
        );
      }
    },
    [conversationId, executionServerId, selectedProjectId, selectedTargetValue],
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

  const handleConversationAccepted = React.useCallback(
    (acceptedConversationId: string) => {
      if (!selectedProjectId) {
        return;
      }

      // 新对话被受理后才有服务端记录：新对话记录中的目标与之后输入的草稿都交给这个 ID。
      // A new conversation has a server record only once accepted: the target and any draft typed since then move to this ID.
      if (conversationId === null) {
        const scope = { serverId: executionServerId, projectId: selectedProjectId };
        const values = conversationComposerStorage.get(scope, null);
        conversationComposerStorage.remove(scope, null);
        conversationComposerStorage.set(scope, acceptedConversationId, values);
        void conversationImageDrafts
          .accept(scope, acceptedConversationId)
          .catch(notifyImageDraftError);
      }
      hydratedConversationKeyRef.current = getConversationHydrationKey(
        selectedProjectId,
        acceptedConversationId,
      );
      setConversationId(acceptedConversationId);
      syncRoute(selectedProjectId, acceptedConversationId);
    },
    [conversationId, executionServerId, selectedProjectId, syncRoute],
  );

  const openConversation = React.useCallback(
    async (conversation: ConversationSummary): Promise<boolean> => {
      if (!selectedProjectId) {
        toast.error("Please select a project");
        return false;
      }

      try {
        await loadConversationHistory(selectedProjectId, conversation.conversationId);
        return true;
      } catch (error) {
        if (isAbortError(error)) {
          return false;
        }
        toast.error(`Failed to load conversation: ${getApiErrorMessage(error)}`);
        return false;
      }
    },
    [loadConversationHistory, selectedProjectId],
  );

  const handleSaveChatSettings = React.useCallback(
    (draft: ChatSettingsDraft) => {
      if (!selectedProjectId) {
        toast.error("Please select a project.");
        return false;
      }

      const normalizedSettings: ChatProjectSettingsStorageValues = {
        envVars: normalizeEnvVars(draft.envVars),
        resultOnly: draft.resultOnly,
      };

      chatSettingsStorage.set(selectedProjectId, normalizedSettings);
      const nextDraft = getProjectSettingsDraft(selectedProjectId);
      setEnvVars(nextDraft.envVars);
      setResultOnly(nextDraft.resultOnly);

      toast.success("Chat settings saved");
      return true;
    },
    [getProjectSettingsDraft, selectedProjectId],
  );

  const conversationStatuses = useConversationStatuses({
    serverId: executionServerId,
    projectId: selectedProjectId,
    conversationId,
  });
  // 状态映射在范围内每次写入后更换实例，这里读取的 turnId 与它同步。
  // The status map changes instance on every write in the scope, so this turnId stays in step with it.
  const currentConversationTurnId =
    selectedProjectId && conversationId
      ? (executionSessionManager.conversationStatuses.get(
          { serverId: executionServerId, projectId: selectedProjectId },
          conversationId,
        )?.turnId ?? null)
      : null;

  const handleConversationDeleted = React.useCallback(
    (deletedConversationId: string) => {
      if (!selectedProjectId) return;
      const scope = { serverId: executionServerId, projectId: selectedProjectId };
      executionSessionManager.conversationStatuses.remove(scope, deletedConversationId);
      conversationComposerStorage.remove(scope, deletedConversationId);
      void conversationImageDrafts
        .remove(scope, deletedConversationId)
        .catch(notifyImageDraftError);
      // 删除对话时释放它的执行连接与队列。Deleting a conversation releases its execution connection and queue.
      void executionSessionManager.discard({ ...scope, conversationId: deletedConversationId });
    },
    [executionServerId, selectedProjectId],
  );

  const handleProjectConversationsCleared = React.useCallback(() => {
    if (!selectedProjectId) return;
    const scope = { serverId: executionServerId, projectId: selectedProjectId };
    executionSessionManager.conversationStatuses.removeScope(scope);
    conversationComposerStorage.removeConversations(scope);
    void conversationImageDrafts.removeConversations(scope).catch(notifyImageDraftError);
    void executionSessionManager.discardProject(scope);
  }, [executionServerId, selectedProjectId]);

  const isReconnecting = getExecutionReconnectProgress(executionReconnectState) !== null;

  /** 立即执行当前 Server 的本次重连，并继续恢复对应的 execution 会话。 */
  const handleReconnectRetry = React.useCallback(() => {
    const retry = reconnectRetryRef.current;
    if (!retry) return;
    void retry().catch((error: unknown) => {
      toast.error(error instanceof Error ? error.message : "Failed to retry connection");
    });
  }, []);

  // Chat 给出当前对话（含草稿对话）的重连状态与重试入口。
  // Chat reports the reconnect state and retry action of the current conversation, draft conversations included.
  const handleReconnectStateChange = React.useCallback(
    (state: ExecutionReconnectState | null, retry: () => Promise<void>) => {
      reconnectRetryRef.current = retry;
      setExecutionReconnectState(state);
    },
    [],
  );

  const refreshConversations = React.useCallback(
    () => refreshConversationList(),
    [refreshConversationList],
  );

  const dependencyErrorMessage =
    projectsQuery.isError || agentsQuery.isError || agentflowsQuery.isError
      ? getApiErrorMessage(projectsQuery.error ?? agentsQuery.error ?? agentflowsQuery.error)
      : null;
  const isChatLoadingConversation =
    isLoadingConversation ||
    Boolean(
      queryConversationId &&
      (queryProjectId !== selectedProjectId || queryConversationId !== conversationId),
    );
  const restoreExecution =
    Number(chatSessionSeed.revision) > 0 &&
    queryProjectId === selectedProjectId &&
    queryConversationId === conversationId &&
    chatSessionSeed.conversationId === conversationId;

  const value = React.useMemo<ChatWorkspaceContextValue>(
    () => ({
      isReconnecting,
      dependencyErrorMessage,
      projects,
      selectedProjectId,
      selectedProject,
      selectProject,
      conversationId,
      selectedTarget,
      targetOptions,
      refreshConversations,
      composerRef,
      projectDirectories,
      selectedDirectory,
      selectDirectory,
      resolvedWorkspace,
      conversationListRefreshSignal,
      conversationStatuses,
      currentConversationTurnId,
      openConversation,
      startNewConversation,
      resetSession,
      handleConversationDeleted,
      handleProjectConversationsCleared,
      getSettingsDraft: getActiveSettingsDraft,
      saveSettings: handleSaveChatSettings,
      onlyDiff,
      setOnlyDiff,
      recursiveMode,
      selectFile: handleOnFileSelected,
      openFile,
      handleFileDeleted: handleOnFileDeleted,
      handleFileReset: handleOnFileReseted,
      handleFileGitScopeChanged: handleOnFileGitScopeChanged,
      selectedFile,
      selectedDiffScope,
      fileContent,
      isLoadingContent,
      contentError,
      diffContentData,
      comments,
      setComments,
      agents: agentsQuery.data,
      selectAgent: handleAgentSelect,
      chatSessionSeed,
      isLoadingConversation: isChatLoadingConversation,
      restoreExecution,
      environmentVariables,
      resultOnly,
      searchDirectoryIds,
      handleConversationAccepted,
      handleConversationChange: refreshConversationList,
      removePendingFileComments: handlePendingFileCommentsRemove,
      handleReconnectStateChange,
    }),
    [
      agentsQuery.data,
      chatSessionSeed,
      comments,
      contentError,
      conversationId,
      conversationListRefreshSignal,
      conversationStatuses,
      currentConversationTurnId,
      dependencyErrorMessage,
      diffContentData,
      environmentVariables,
      fileContent,
      getActiveSettingsDraft,
      handleAgentSelect,
      handleConversationAccepted,
      handleConversationDeleted,
      handleOnFileDeleted,
      handleOnFileGitScopeChanged,
      handleOnFileReseted,
      handleOnFileSelected,
      handlePendingFileCommentsRemove,
      handleProjectConversationsCleared,
      handleReconnectStateChange,
      handleSaveChatSettings,
      isChatLoadingConversation,
      isLoadingContent,
      isReconnecting,
      onlyDiff,
      openConversation,
      openFile,
      projectDirectories,
      projects,
      recursiveMode,
      refreshConversationList,
      refreshConversations,
      resetSession,
      resolvedWorkspace,
      restoreExecution,
      resultOnly,
      searchDirectoryIds,
      selectDirectory,
      selectProject,
      selectedDiffScope,
      selectedDirectory,
      selectedFile,
      selectedProject,
      selectedProjectId,
      selectedTarget,
      startNewConversation,
      targetOptions,
    ],
  );

  return (
    <ChatWorkspaceContext value={value}>
      {children}
      {isReconnecting && executionReconnectState ? (
        <ExecutionReconnectingDialog
          state={executionReconnectState}
          onRetry={handleReconnectRetry}
        />
      ) : null}
    </ChatWorkspaceContext>
  );
}

export type ChatConversationSidebarProps = {
  /** 选中对话、新建对话、删除全部对话后调用。Called after selecting a conversation, starting a new one or deleting them all. */
  onConversationOpened?: () => void;
  itemActions?: readonly ExtensionMenuItem<ConversationItemActionArgs>[];
};

/** 对话列表与对话设置。The conversation list with conversation settings. */
export function ChatConversationSidebar({
  onConversationOpened,
  itemActions,
}: ChatConversationSidebarProps) {
  const workspace = useChatWorkspace();
  const executionServerId = useExecutionPlatform().serverId;
  const { conversationId, conversationStatuses, selectedProjectId } = workspace;

  return (
    <ConversationList
      projectId={selectedProjectId ?? ""}
      currentConversationId={conversationId}
      refreshSignal={workspace.conversationListRefreshSignal}
      conversationStatuses={conversationStatuses}
      currentConversationTurnId={workspace.currentConversationTurnId}
      onConversationSelect={(nextConversation) => {
        void workspace.openConversation(nextConversation).then((opened) => {
          if (opened) onConversationOpened?.();
        });
      }}
      onNewConversation={() => {
        workspace.startNewConversation();
        onConversationOpened?.();
      }}
      onAllConversationsDeleted={() => {
        workspace.resetSession();
        onConversationOpened?.();
      }}
      onConversationDeleted={workspace.handleConversationDeleted}
      onProjectConversationsCleared={workspace.handleProjectConversationsCleared}
      headerActions={(currentConversation) => (
        <ChatSettingsDialog
          serverId={executionServerId}
          selectedProjectId={selectedProjectId}
          conversationId={conversationId}
          currentConversation={currentConversation}
          conversationRunning={
            conversationId !== null && conversationStatuses.get(conversationId) === "running"
          }
          getDraft={workspace.getSettingsDraft}
          onSave={workspace.saveSettings}
        />
      )}
      itemActions={itemActions}
    />
  );
}

export type ChatFilesSidebarProps = {
  /** 每次选中非空文件后调用，包括再次选中当前文件。Called whenever a file is selected, the current file included. */
  onFileOpened?: () => void;
  contextMenuItems?: readonly ExtensionMenuItem<ExplorerContextMenuArgs>[];
};

/** 当前项目的文件浏览器；未选择项目时显示提示。The project's file explorer, or a prompt without a project. */
export function ChatFilesSidebar({ onFileOpened, contextMenuItems }: ChatFilesSidebarProps) {
  const workspace = useChatWorkspace();
  const { selectFile, selectedDirectory, selectedProjectId, resolvedWorkspace } = workspace;
  const handleFileSelected = React.useCallback(
    (filePath: string | null, scope?: GitDiffScope) => {
      selectFile(filePath, scope);
      if (filePath) onFileOpened?.();
    },
    [onFileOpened, selectFile],
  );

  if (selectedProjectId === null) {
    return <ProjectRequiredState />;
  }

  return (
    <Explorer
      key={`${selectedProjectId}:${selectedDirectory?.id ?? "primary"}:${resolvedWorkspace}`}
      projectId={selectedProjectId}
      directoryId={selectedDirectory?.id}
      directories={workspace.projectDirectories}
      onDirectoryChange={workspace.selectDirectory}
      rootDirectory={resolvedWorkspace || "/"}
      onlyDiff={workspace.onlyDiff}
      recursiveMode={workspace.recursiveMode}
      onOnlyDiffChange={workspace.setOnlyDiff}
      onFileDeleted={workspace.handleFileDeleted}
      onFileSelected={handleFileSelected}
      onFileReseted={workspace.handleFileReset}
      onFileGitScopeChanged={workspace.handleFileGitScopeChanged}
      contextMenuItems={contextMenuItems}
    />
  );
}

export type ChatConversationPaneProps = {
  /** 使用紧凑尺寸的 Agent 选择器。Uses the compact Agent selector. */
  compact?: boolean;
  showUserInputNavigation?: boolean;
  composerActions?: readonly ExtensionMenuItem<Record<string, never>>[];
  messageActions?: readonly ExtensionMenuItem<ChatMessageActionArgs>[];
};

/** 当前对话与 Agent 选择器。The current conversation with the Agent selector. */
export function ChatConversationPane({
  compact = false,
  showUserInputNavigation = false,
  composerActions,
  messageActions,
}: ChatConversationPaneProps) {
  const workspace = useChatWorkspace();
  const { selectedProjectId, selectedTarget } = workspace;

  return (
    <Chat
      target={selectedTarget}
      agentResultFormats={workspace.agents}
      projectId={selectedProjectId}
      inputTopLeft={
        <div className="w-44">
          <AgentSelector
            id="chat-target-select"
            size={compact ? "sm" : "default"}
            height={34}
            projectId={selectedProjectId}
            value={
              selectedTarget
                ? {
                    agentType: selectedTarget.type === "agent" ? 0 : 1,
                    agentId: selectedTarget.id,
                  }
                : null
            }
            onSelect={workspace.selectAgent}
          />
        </div>
      }
      persistInputDraft
      conversationId={workspace.conversationId}
      sessionSeed={workspace.chatSessionSeed}
      isLoadingConversation={workspace.isLoadingConversation}
      showUserInputNavigation={showUserInputNavigation}
      restoreExecution={workspace.restoreExecution}
      environmentVariables={workspace.environmentVariables}
      resultOnly={workspace.resultOnly}
      onConversationAccepted={workspace.handleConversationAccepted}
      onConversationChange={workspace.handleConversationChange}
      directoryId={workspace.selectedDirectory?.id}
      searchDirectoryIds={workspace.searchDirectoryIds}
      directories={workspace.projectDirectories}
      pendingFileComments={workspace.comments}
      onPendingFileCommentsRemove={workspace.removePendingFileComments}
      onReconnectStateChange={workspace.handleReconnectStateChange}
      composerRef={workspace.composerRef}
      composerActions={composerActions}
      messageActions={messageActions}
    />
  );
}

export type ChatFilePaneProps = {
  /** 匹配当前文件的查看器（Desktop extension）。Viewers matching the current file (Desktop extensions). */
  fileViewers?: readonly FileViewerOption[];
  renderFileViewer?: (viewerId: string) => React.ReactNode;
  /** 重新加载查看器。Reloads a viewer. */
  onReloadFileViewer?: (viewerId: string) => void;
  /** 显示在刷新按钮右侧的查看器说明，选中 Source 时也显示。The viewer info right of the reload button, also with Source. */
  renderFileViewerInfo?: (viewerId: string) => React.ReactNode;
};

/** 选中文件的内容或 diff；未选择项目时显示提示。The selected file's content or diff, or a prompt without a project. */
export function ChatFilePane({
  fileViewers,
  renderFileViewer,
  onReloadFileViewer,
  renderFileViewerInfo,
}: ChatFilePaneProps = {}) {
  const workspace = useChatWorkspace();
  const { projectDirectories, selectedDirectory, selectedProjectId } = workspace;

  if (selectedProjectId === null) {
    return <ProjectRequiredState />;
  }

  return (
    <FileContent
      projectId={selectedProjectId}
      directoryId={selectedDirectory?.id}
      directoryName={projectDirectories.length > 1 ? selectedDirectory?.path : undefined}
      selectedFile={workspace.selectedFile}
      isLoadingContent={workspace.isLoadingContent}
      contentError={workspace.contentError}
      onlyDiff={workspace.onlyDiff}
      diffContentData={workspace.diffContentData}
      comments={workspace.comments}
      setComments={workspace.setComments}
      fileContent={workspace.fileContent}
      diffScope={workspace.selectedDiffScope}
      fileViewers={fileViewers}
      renderFileViewer={renderFileViewer}
      onReloadFileViewer={onReloadFileViewer}
      onReloadSource={() => workspace.handleFileReset(workspace.selectedFile)}
      renderFileViewerInfo={renderFileViewerInfo}
    />
  );
}

type ChatWorkspaceLayoutProps = Omit<ChatWorkspaceProps, "routeBasePath">;

/** Web 与 Desktop 现有的 Chat/Files 标签布局。The existing Chat/Files tab layout of Web and Desktop. */
function ChatWorkspaceLayout({
  showProjectSelect,
  compactToolbar = false,
  showUserInputNavigation = false,
}: ChatWorkspaceLayoutProps) {
  const { dependencyErrorMessage, isReconnecting, projects, selectedProjectId, selectProject } =
    useChatWorkspace();
  const [currentTab, setCurrentTab] = React.useState("chat");
  const [isNarrowViewport, setIsNarrowViewport] = React.useState(false);
  const isMobile = isNarrowViewport;
  const [isDrawerOpen, setIsDrawerOpen] = React.useState(false);
  const [showChatHistory, setShowChatHistory] = React.useState(true);
  const [showFileExplorer, setShowFileExplorer] = React.useState(true);
  const [drawerContent, setDrawerContent] = React.useState<"chat" | "files" | null>(null);
  const hasProjectFileSystem = selectedProjectId !== null;

  React.useEffect(() => {
    const mediaQuery = window.matchMedia("(max-width: 768px)");
    const handleMediaChange = (event: MediaQueryListEvent) => {
      setIsNarrowViewport(event.matches);
    };

    setIsNarrowViewport(mediaQuery.matches);
    mediaQuery.addEventListener("change", handleMediaChange);
    return () => mediaQuery.removeEventListener("change", handleMediaChange);
  }, []);

  const handleConversationOpened = React.useCallback(() => {
    setIsDrawerOpen(false);
  }, []);

  const handleFileOpened = React.useCallback(() => {
    if (isMobile) {
      setIsDrawerOpen(false);
    }
  }, [isMobile]);

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

  // Chat/Files 切换栏与侧栏开关。Chat/Files toolbar with the sidebar toggle.
  const sidebarControls = (
    <div className="flex shrink-0 flex-wrap items-center gap-2 mb-2 ">
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
  );

  /** 桌面端折叠左列时收起项目选择器，移动端的工具条始终保留它。 */
  const showToolbarProjectSelect = showProjectSelect && (isMobile || activeSidebarVisible);

  /** Web 左列顶部多一个项目选择器，默认宽度比 Desktop 宽 40px。 */
  const sidebarDefaultWidth = showProjectSelect ? 360 : 320;

  /** 项目选择器与 Chat/Files 工具条，桌面端位于左列顶部，移动端位于主区域顶部。 */
  const workspaceToolbar = (
    <div className="flex shrink-0 flex-wrap items-center pt-2">
      {showToolbarProjectSelect ? (
        <div className="w-50 mr-2 mb-2">
          <SearchableSelect
            id="chat-project-select"
            ariaLabel="Select project"
            value={selectedProjectId ?? ""}
            onValueChange={selectProject}
            options={projectSelectOptions}
            placeholder="Select project"
            searchPlaceholder="Search projects..."
            clearable={false}
          />
        </div>
      ) : null}

      {sidebarControls}
    </div>
  );

  return (
    <>
      {dependencyErrorMessage !== null ? (
        <div className="text-sm text-destructive">
          Failed to load chat dependencies: {dependencyErrorMessage}
        </div>
      ) : null}

      <Tabs
        value={currentTab}
        onValueChange={handleTabChange}
        inert={isReconnecting}
        aria-hidden={isReconnecting}
        className="flex min-h-0 flex-1 flex-col"
      >
        <ColResizeSplit>
          {isMobile ? null : (
            <ColResizeSplit.Left
              defaultPanelWidth={sidebarDefaultWidth}
              minWidth={268}
              maxWidth={420}
              collapsed={!activeSidebarVisible}
            >
              <div className="flex h-full min-h-0 flex-col">
                {workspaceToolbar}

                {activeSidebarVisible ? (
                  <div className="min-h-0 flex-1 overflow-hidden">
                    {isChatTab ? (
                      <ChatConversationSidebar onConversationOpened={handleConversationOpened} />
                    ) : null}
                    {isFilesTab && hasProjectFileSystem ? (
                      <ChatFilesSidebar onFileOpened={handleFileOpened} />
                    ) : null}
                  </div>
                ) : null}
              </div>
            </ColResizeSplit.Left>
          )}

          <ColResizeSplit.Right>
            <div className="flex h-full min-h-0 w-full flex-col">
              {isMobile ? workspaceToolbar : null}

              <TabsContent
                value="chat"
                forceMount
                className="flex min-h-0 flex-1 data-[state=inactive]:hidden"
              >
                <div className="relative flex flex-col min-h-105 flex-1 overflow-hidden">
                  <div className="relative flex min-h-0 flex-1 flex-col">
                    <ChatConversationPane
                      compact={compactToolbar}
                      showUserInputNavigation={showUserInputNavigation}
                    />
                  </div>
                </div>
              </TabsContent>

              <TabsContent value="files" className="flex min-h-0 flex-1">
                <div className="flex justify-center flex-1 overflow-hidden px-2">
                  <ChatFilePane />
                </div>
              </TabsContent>
            </div>
          </ColResizeSplit.Right>
        </ColResizeSplit>
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
              <ChatFilesSidebar onFileOpened={handleFileOpened} />
            ) : (
              <ChatConversationSidebar onConversationOpened={handleConversationOpened} />
            )}
          </div>
        </DrawerContent>
      </Drawer>
    </>
  );
}

export function ChatWorkspace({
  routeBasePath,
  showProjectSelect,
  compactToolbar = false,
  showUserInputNavigation = false,
}: ChatWorkspaceProps) {
  return (
    <div className="relative flex h-full w-full min-w-0 flex-col gap-3">
      <ChatWorkspaceProvider routeBasePath={routeBasePath}>
        <ChatWorkspaceLayout
          showProjectSelect={showProjectSelect}
          compactToolbar={compactToolbar}
          showUserInputNavigation={showUserInputNavigation}
        />
      </ChatWorkspaceProvider>
    </div>
  );
}
