"use client";

import * as React from "react";
import { FileText, PanelLeftClose, PanelLeftOpen, Plus, Trash2, Info } from "lucide-react";
import { useRouter, useSearchParams } from "next/navigation";
import { toast } from "sonner";

import { type GitDiffScope, type ConversationSummary } from "@agw/projects";
import { AgentSelector } from "../../components/agent-selector";
import { type EditableSource, Explorer, FileContent, type FileViewerOption } from "@agw/projects";
import type {
  ConversationItemActionArgs,
  ExplorerContextMenuArgs,
  ExtensionMenuItem,
} from "@agw/projects";
import { Chat } from "../../components/message/chat";
import type { ChatMessageActionArgs } from "../../components/message/chat-extension-types";
import { ConversationList } from "@agw/projects";
import { Button, formatFriendlyLocalDateTime2 } from "@agw/components";
import {
  Dialog,
  DialogClose,
  DialogContent,
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
import { buildChatHref } from "../../../lib/chat-route";
import { cn } from "@agw/components";
import ColResizeSplit from "./components/split-layout";
import { ProviderSessionsSection } from "./components/provider-sessions-section";
import {
  CHAT_SETTINGS_DIALOG_BODY_CLASS_NAME,
  CHAT_SETTINGS_DIALOG_CONTENT_CLASS_NAME,
} from "./lib/chat-settings";
import { getChatRouteSessionAction, getConversationHydrationKey } from "./lib/session-routing";
import type { EnvVar } from "./types";
import { executionSessionManager } from "@agw/chat-runtime";
import { getApiErrorMessage } from "@agw/api";
import { useExecutionPlatform } from "../../execution-platform";
import {
  ChatProjectProvider,
  useChatProject,
  type ChatSettingsDraft,
} from "./chat-project-provider";
import { ChatSessionProvider, readSessionHistory, useChatSession } from "./chat-session-provider";
import { FileDocumentProvider, useFileDocument } from "./file-document-provider";

export type ChatWorkspaceProps = {
  routeBasePath: string;
  showProjectSelect: boolean;
  compactToolbar?: boolean;
  showUserInputNavigation?: boolean;
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
                  <div className="grid grid-cols-12 gap-2 border-b bg-muted/50 p-2 text-sm font-medium text-muted-foreground">
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
                        className="col-span-5 h-8"
                      />
                      <Input
                        value={envVar.value}
                        onChange={(event) => handleUpdateEnvVar(index, "value", event.target.value)}
                        placeholder="value"
                        className="col-span-6 h-8"
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

export type ChatConversationSidebarProps = {
  keepDraftOnClear?: { draftId: string };
  currentConversationId: string | null;
  onConversationSelect(conversation: ConversationSummary, options: { pinned: boolean }): void;
  onNewConversation(): void;
  onAllConversationsDeleted(): void;
  onConversationDeleted?(conversationId: string): void;
  onConversationsLoaded?(items: readonly ConversationSummary[]): void;
  itemActions?: readonly ExtensionMenuItem<ConversationItemActionArgs>[];
};

/** 对话列表与对话设置。The conversation list with conversation settings. */
export function ChatConversationSidebar({
  keepDraftOnClear,
  currentConversationId: conversationId,
  onConversationSelect,
  onNewConversation,
  onAllConversationsDeleted,
  onConversationDeleted,
  onConversationsLoaded,
  itemActions,
}: ChatConversationSidebarProps) {
  const workspace = useChatProject();
  const executionServerId = useExecutionPlatform().serverId;
  const { conversationStatuses, selectedProjectId } = workspace;

  return (
    <ConversationList
      projectId={selectedProjectId ?? ""}
      currentConversationId={conversationId}
      refreshSignal={workspace.conversationListRefreshSignal}
      conversationStatuses={conversationStatuses}
      currentConversationTurnId={
        selectedProjectId && conversationId
          ? (executionSessionManager.conversationStatuses.get(
              { serverId: executionServerId, projectId: selectedProjectId },
              conversationId,
            )?.turnId ?? null)
          : null
      }
      onConversationSelect={onConversationSelect}
      onNewConversation={onNewConversation}
      onAllConversationsDeleted={onAllConversationsDeleted}
      onConversationsLoaded={onConversationsLoaded}
      onConversationDeleted={(id) => {
        workspace.handleConversationDeleted(id);
        onConversationDeleted?.(id);
      }}
      onProjectConversationsCleared={() =>
        workspace.handleProjectConversationsCleared(keepDraftOnClear)
      }
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
  selectedFile?: { directoryId: string | null; path: string } | null;
  onFileSelected(path: string, scope: GitDiffScope | undefined, options: { pinned: boolean }): void;
  onFileDeleted?(path: string): void;
  onFileGitScopeChanged?(path: string, scope: GitDiffScope): void;
  contextMenuItems?: readonly ExtensionMenuItem<ExplorerContextMenuArgs>[];
};

/** 当前项目的文件浏览器；未选择项目时显示提示。The project's file explorer, or a prompt without a project. */
export function ChatFilesSidebar({
  selectedFile,
  onFileSelected,
  onFileDeleted,
  onFileGitScopeChanged,
  contextMenuItems,
}: ChatFilesSidebarProps) {
  const workspace = useChatProject();
  const { selectedDirectory, selectedProjectId, resolvedWorkspace } = workspace;

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
      selectedFilePath={
        selectedFile && selectedFile.directoryId === (selectedDirectory?.id ?? null)
          ? selectedFile.path
          : selectedFile === undefined
            ? undefined
            : null
      }
      onFileDeleted={(path) => onFileDeleted?.(path)}
      onFileSelected={onFileSelected}
      onFileReseted={(path) => {
        if (path) workspace.notifyFileReset(path);
      }}
      onFileGitScopeChanged={(path, scope) => onFileGitScopeChanged?.(path, scope)}
      contextMenuItems={contextMenuItems}
      refreshSignal={workspace.fileTreeRefreshSignal}
    />
  );
}

export type ChatConversationPaneProps = {
  /** 使用紧凑尺寸的 Agent 选择器。Uses the compact Agent selector. */
  compact?: boolean;
  showUserInputNavigation?: boolean;
  composerActions?: readonly ExtensionMenuItem<Record<string, never>>[];
  messageActions?: readonly ExtensionMenuItem<ChatMessageActionArgs>[];
  context?: Readonly<Record<string, string>>;
};

/** 当前对话与 Agent 选择器。The current conversation with the Agent selector. */
export function ChatConversationPane({
  compact = false,
  showUserInputNavigation = false,
  composerActions,
  messageActions,
  context,
  loadingSelection = false,
}: ChatConversationPaneProps & { loadingSelection?: boolean }) {
  const workspace = useChatProject();
  const { selectedProjectId } = workspace;
  const session = useChatSession();
  const { selectedTarget } = session;
  const targetSelectorId = React.useId();

  return (
    <Chat
      target={selectedTarget}
      agentResultFormats={workspace.agents}
      projectId={selectedProjectId}
      inputTopLeft={
        <div className="w-44">
          <AgentSelector
            id={`chat-target-select-${targetSelectorId}`}
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
            onSelect={session.selectAgent}
          />
        </div>
      }
      persistInputDraft
      conversationId={session.conversationId}
      draftId={session.draftId}
      newConversationId={session.newConversationId}
      onSubmitted={session.onSubmitted}
      sessionSeed={session.chatSessionSeed}
      isLoadingConversation={session.isLoadingConversation}
      isOpeningConversation={loadingSelection}
      keepLiveDuringRecovery={session.keepLive}
      showUserInputNavigation={showUserInputNavigation}
      restoreExecution={session.restoreExecution}
      environmentVariables={workspace.environmentVariables}
      resultOnly={workspace.resultOnly}
      onConversationAccepted={session.handleConversationAccepted}
      onConversationChange={session.handleConversationChange}
      directoryId={workspace.selectedDirectory?.id}
      searchDirectoryIds={workspace.searchDirectoryIds}
      directories={workspace.projectDirectories}
      pendingFileComments={workspace.comments}
      onPendingFileCommentsRemove={workspace.removePendingFileComments}
      context={context}
      onReconnectStateChange={session.handleReconnectStateChange}
      composerRef={session.composerRef}
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
  /** 查看器重新读取磁盘内容已变化的文件。The viewer reads its file changed on disk again. */
  onReloadFileViewerDocument?: (viewerId: string) => void;
  /**
   * "More" 菜单顶部的查看器说明，选中 Source 时也显示。The viewer info at the top of the "More" menu, also with Source.
   */
  renderFileViewerInfo?: (viewerId: string) => React.ReactNode;
  renderSourceAccessory?: () => React.ReactNode;
  /** 用户选择了查看器在 "More" 菜单中的条目。The user chose one of the viewer's items in the "More" menu. */
  onSelectFileViewerItem?: (viewerId: string, itemId: string, entryId: string | null) => void;
  /** 用户为当前文件类型设置的默认查看器 id，或 "source"。The user's default viewer id for the file type, or "source". */
  defaultFileViewerId?: string | null;
  /** 把当前查看器设为默认，null 表示删除设置。Sets the current viewer as the default; null removes the setting. */
  onSetDefaultFileViewer?: (viewerId: string | null) => void;
  /** Desktop 的可编辑 Source；没有时 Source 只读。Desktop's editable Source; read-only without it. */
  editableSource?: EditableSource;
};

/** 选中文件的内容或 diff；未选择项目时显示提示。The selected file's content or diff, or a prompt without a project. */
export function ChatFilePane({
  fileViewers,
  renderFileViewer,
  onReloadFileViewer,
  onReloadFileViewerDocument,
  renderFileViewerInfo,
  renderSourceAccessory,
  onSelectFileViewerItem,
  defaultFileViewerId,
  onSetDefaultFileViewer,
  editableSource,
}: ChatFilePaneProps = {}) {
  const workspace = useChatProject();
  const { projectDirectories, selectedProjectId } = workspace;
  const document = useFileDocument();
  const selectedDirectory = document.directory;

  if (selectedProjectId === null) {
    return <ProjectRequiredState />;
  }

  return (
    <FileContent
      projectId={selectedProjectId}
      directoryId={selectedDirectory?.id}
      directoryName={projectDirectories.length > 1 ? selectedDirectory?.path : undefined}
      selectedFile={document.path}
      isLoadingContent={document.isLoadingContent}
      contentError={document.contentError}
      onlyDiff={workspace.onlyDiff}
      diffFileView={document.diffFileView}
      onDiffFileViewChange={document.setDiffFileView}
      diffContentData={document.diffContentData}
      comments={workspace.comments}
      setComments={workspace.setComments}
      fileContent={document.fileContent}
      diffScope={document.diffScope ?? undefined}
      fileViewers={fileViewers}
      renderFileViewer={renderFileViewer}
      onReloadFileViewer={onReloadFileViewer}
      onReloadFileViewerDocument={onReloadFileViewerDocument}
      onReloadSource={document.reload}
      editableSource={editableSource}
      renderFileViewerInfo={renderFileViewerInfo}
      renderSourceAccessory={renderSourceAccessory}
      onSelectFileViewerItem={onSelectFileViewerItem}
      defaultFileViewerId={defaultFileViewerId}
      onSetDefaultFileViewer={onSetDefaultFileViewer}
    />
  );
}

function WebConversationPane({
  onLoaded,
  ...props
}: ChatConversationPaneProps & { onLoaded(id: string): void; loadingSelection: boolean }) {
  const { chatSessionSeed, conversationId, loadError } = useChatSession();
  React.useEffect(() => {
    if (conversationId && chatSessionSeed.conversationId === conversationId)
      onLoaded(conversationId);
  }, [conversationId, chatSessionSeed, onLoaded]);
  React.useEffect(() => {
    if (loadError && conversationId) {
      toast.error(`Failed to load chat history: ${loadError}`);
    }
  }, [conversationId, loadError]);
  return <ChatConversationPane {...props} />;
}

type WebSession = {
  conversationId: string | null;
  instance: number;
  initialHistory?: Awaited<ReturnType<typeof readSessionHistory>>;
};

function useSidebarNavigation(
  projectId: string | null,
  instance: number,
  setSession: React.Dispatch<React.SetStateAction<WebSession>>,
) {
  const { serverId } = useExecutionPlatform();
  const request = React.useRef<AbortController | null>(null);
  const [pending, setPending] = React.useState(false);
  const cancel = React.useCallback(() => {
    request.current?.abort();
    request.current = null;
    setPending(false);
  }, []);
  React.useEffect(() => {
    setPending(false);
    return () => request.current?.abort();
  }, [serverId, projectId, instance]);
  async function open(conversation: ConversationSummary, options?: { pinned: boolean }) {
    if (!projectId || options?.pinned) return;
    request.current?.abort();
    const controller = new AbortController();
    request.current = controller;
    setPending(true);
    const { conversationId } = conversation;
    try {
      const initialHistory = await readSessionHistory(
        { serverId, projectId, conversationId },
        controller.signal,
      );
      if (!controller.signal.aborted)
        setSession((current) => ({
          conversationId,
          instance: current.instance + 1,
          initialHistory,
        }));
    } catch (error) {
      if (controller.signal.aborted) return;
      console.error("Failed to open conversation.", error);
      toast.error(`Failed to load conversation: ${getApiErrorMessage(error)}`);
    } finally {
      if (!controller.signal.aborted) {
        request.current = null;
        setPending(false);
      }
    }
  }
  return { pending, open, cancel };
}

/** Web 的 Chat/Files 标签布局。The Chat/Files layout on Web. */
function ChatWorkspaceLayout({
  routeBasePath,
  showProjectSelect,
  compactToolbar = false,
  showUserInputNavigation = false,
}: ChatWorkspaceProps) {
  const project = useChatProject();
  const { dependencyErrorMessage, isReconnecting, projects, selectedProjectId, selectProject } =
    project;
  const router = useRouter();
  const search = useSearchParams();
  const queryProjectId = search.get("projectId");
  const queryConversationId = search.get("conversationId");
  const routeKey = getConversationHydrationKey(queryProjectId, queryConversationId);
  const initialId = queryProjectId === selectedProjectId ? queryConversationId : null;
  const [session, setSession] = React.useState<WebSession>({
    conversationId: initialId,
    instance: 0,
  });
  const sidebarNavigation = useSidebarNavigation(selectedProjectId, session.instance, setSession);
  const { conversationId } = session;
  const [previousRoute, setPreviousRoute] = React.useState({
    projectId: selectedProjectId,
    routeKey,
  });
  if (previousRoute.projectId !== selectedProjectId || previousRoute.routeKey !== routeKey) {
    setPreviousRoute({ projectId: selectedProjectId, routeKey });
    const action = getChatRouteSessionAction({
      queryProjectId,
      queryConversationId,
      hydratedRouteKey: getConversationHydrationKey(selectedProjectId, conversationId),
    });
    if (
      previousRoute.projectId !== selectedProjectId ||
      (action.type !== "ignore" && initialId !== conversationId)
    ) {
      setSession({ conversationId: initialId, instance: session.instance + 1 });
    }
  }
  const syncRoute = React.useCallback(
    (id: string | null) => {
      const href = buildChatHref(routeBasePath, {
        projectId: selectedProjectId,
        conversationId: id,
      });
      if (window.location.pathname + window.location.search + window.location.hash !== href)
        router.replace(href, { scroll: false });
    },
    [routeBasePath, router, selectedProjectId],
  );
  const accept = React.useCallback(
    (id: string) => {
      setSession((current) => ({ ...current, conversationId: id }));
      syncRoute(id);
    },
    [syncRoute],
  );
  const directoryId = project.selectedDirectory?.id ?? null;
  const [file, setFile] = React.useState<{
    path: string;
    diffScope: GitDiffScope | null;
    directoryId: string | null;
    diffFileView: boolean;
  } | null>(null);
  const [fileLocation, setFileLocation] = React.useState({
    projectId: selectedProjectId,
    directoryId,
    onlyDiff: project.onlyDiff,
  });
  if (
    fileLocation.projectId !== selectedProjectId ||
    fileLocation.directoryId !== directoryId ||
    fileLocation.onlyDiff !== project.onlyDiff
  ) {
    setFileLocation({ projectId: selectedProjectId, directoryId, onlyDiff: project.onlyDiff });
    setFile(
      fileLocation.projectId === selectedProjectId &&
        fileLocation.directoryId === directoryId &&
        file
        ? { ...file, diffFileView: false }
        : null,
    );
  }
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

  const handleConversationOpened = React.useCallback(
    (id: string) => {
      syncRoute(id);
      setIsDrawerOpen(false);
    },
    [syncRoute],
  );

  function newConversation() {
    sidebarNavigation.cancel();
    setSession((current) => ({ conversationId: null, instance: current.instance + 1 }));
    syncRoute(null);
    setIsDrawerOpen(false);
  }
  const conversationSidebar = (
    <ChatConversationSidebar
      keepDraftOnClear={{ draftId: "web" }}
      currentConversationId={conversationId}
      onConversationSelect={(next, options) => {
        void sidebarNavigation.open(next, options);
      }}
      onNewConversation={newConversation}
      onConversationDeleted={(id) => {
        if (id === conversationId) newConversation();
      }}
      onAllConversationsDeleted={newConversation}
    />
  );
  const filesSidebar = (
    <ChatFilesSidebar
      onFileSelected={(path, scope) => {
        const diffScope = scope ?? null;
        setFile((current) =>
          current?.path === path && current.diffScope === diffScope
            ? current
            : { path, directoryId, diffScope, diffFileView: false },
        );
        if (isMobile) setIsDrawerOpen(false);
      }}
      onFileDeleted={(path) => {
        if (file?.path === path) {
          setFile(null);
          project.setComments([]);
        }
      }}
      onFileGitScopeChanged={(path, scope) => {
        setFile((current) =>
          current && (current.path === path || current.path.startsWith(path + "/"))
            ? { ...current, diffScope: scope }
            : current,
        );
      }}
    />
  );

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
          className={cn("cursor-pointer", compactToolbar && "h-6 px-2.5 py-0")}
        >
          Chat
        </TabsTrigger>
        <TabsTrigger
          value="files"
          className={cn("cursor-pointer", compactToolbar && "h-6 px-2.5 py-0")}
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
                    {isChatTab ? conversationSidebar : null}
                    {isFilesTab && hasProjectFileSystem ? filesSidebar : null}
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
                    <ChatSessionProvider
                      key={session.instance}
                      conversationId={conversationId}
                      initialHistory={session.initialHistory}
                      draftId="web"
                      newConversationId={null}
                      draftSubmitted={false}
                      visible
                      onConversationAccepted={accept}
                    >
                      <WebConversationPane
                        onLoaded={handleConversationOpened}
                        loadingSelection={sidebarNavigation.pending}
                        compact={compactToolbar}
                        showUserInputNavigation={showUserInputNavigation}
                      />
                    </ChatSessionProvider>
                  </div>
                </div>
              </TabsContent>

              <TabsContent value="files" className="flex min-h-0 flex-1">
                <div className="flex justify-center flex-1 overflow-hidden px-2">
                  {file ? (
                    <FileDocumentProvider
                      path={file.path}
                      directoryId={file.directoryId}
                      diffScope={file.diffScope}
                      diffFileView={file.diffFileView}
                      onDiffFileViewChange={(diffFileView) => setFile({ ...file, diffFileView })}
                      readSourceContent
                    >
                      <ChatFilePane />
                    </FileDocumentProvider>
                  ) : selectedProjectId ? (
                    <FileContent
                      projectId={selectedProjectId}
                      selectedFile={null}
                      fileContent=""
                      isLoadingContent={false}
                      contentError={null}
                      diffContentData={null}
                      onlyDiff={project.onlyDiff}
                      comments={project.comments}
                      setComments={project.setComments}
                    />
                  ) : (
                    <ProjectRequiredState />
                  )}
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
            {drawerContent === "files" ? filesSidebar : conversationSidebar}
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
      <ChatProjectProvider routeBasePath={routeBasePath}>
        <ChatWorkspaceLayout
          routeBasePath={routeBasePath}
          showProjectSelect={showProjectSelect}
          compactToolbar={compactToolbar}
          showUserInputNavigation={showUserInputNavigation}
        />
      </ChatProjectProvider>
    </div>
  );
}
