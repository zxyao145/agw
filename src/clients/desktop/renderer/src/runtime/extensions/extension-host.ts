import { chatHttpClient } from "@agw/chat-runtime";
import type {
  AgwDesktopBridge,
  ExtensionEvent,
  ExtensionFrameBounds,
  ExtensionPortMessage,
  ExtensionRecoveryNotice,
  ExtensionSafeModeState,
  HostConfirmationRequest,
  InstalledExtension,
  ThemeStyleRequest,
  ThemeStyleText,
} from "@desktop/shared/contracts";
import {
  createExtensionError,
  type DocumentSaveStatus,
  type ExecutionCloseReason,
  type ExtensionAction,
  type ExtensionFrameKind,
  type FileViewerFile,
  type FileViewerParams,
  type HostContext,
} from "@agw/extension-sdk";
import { DEFAULT_THEME_ID } from "@desktop/shared/appearance";
import { parseHostPermissions } from "@desktop/shared/extension-host-permissions";
import { DEFAULT_THEME_TITLE } from "../appearance/resolve-theme";
import { A2AService } from "./a2a-service";
import {
  CloudAccessGrants,
  CloudFileSystem,
  type FileWriteLocation,
  parseCloudUri,
} from "./cloud-file-system";
import { ContextService } from "./context-service";
import { extensionRegistration } from "./extension-contributions";
import {
  type BrokerExtension,
  ExtensionBridgeBroker,
  type HostTheme,
  type MessageBoxRequest,
} from "./extension-bridge-broker";
import { ExecutionService, type SessionOwner } from "./execution-service";
import { BUILT_IN_VIEWS, isBuiltInViewId } from "./workspace-ids";
import { WorkspaceLayoutStore } from "./workspace-layout-store";
import { type WorkspaceView, WorkspaceRegistry } from "./workspace-registry";

export const HANDSHAKE_TIMEOUT_MS = 15_000;
export const ACTIVATION_HOOK_TIMEOUT_MS = 15_000;
export const DEACTIVATE_TIMEOUT_MS = 5_000;
export const UNLOAD_TIMEOUT_MS = 1_000;
export const HEARTBEAT_INTERVAL_MS = 5_000;
export const HEARTBEAT_TIMEOUT_MS = 15_000;
const MAX_BLOCKED_REQUESTS = 20;

export type ExtensionRuntimeStatus =
  | "inactive"
  | "activating"
  | "active"
  | "deactivating"
  | "failed"
  | "invalid"
  | "incompatible";

export type ExtensionFailure = { code: string; message: string };

export type BlockedRequest = { url: string; resourceType: string; reason: string; time: string };

export type HostFrameState = {
  frameId: string;
  extensionId: string;
  kind: ExtensionFrameKind;
  viewId: string | null;
  status: "loading" | "ready";
  snapshot: string | null;
};

export type ExtensionState = {
  id: string;
  installed: InstalledExtension;
  status: ExtensionRuntimeStatus;
  error: ExtensionFailure | null;
  blockedRequests: readonly BlockedRequest[];
  frames: readonly HostFrameState[];
};

/** 等待回答的消息框及发出它的页面；页面离开时撤回。A message box awaiting its answer and the page that asked; withdrawn when the page goes away. */
type PendingMessageBox = {
  frameId: string;
  pageId: string;
  reject(error: Error): void;
};

/** 文件视图打开文件的位置。Where the Files view opens a file. */
export type FileOpenLocation = { directoryId: string | null; path: string };

export type ExtensionHostSnapshot = {
  started: boolean;
  safeMode: ExtensionSafeModeState | null;
  recoveryNotices: readonly ExtensionRecoveryNotice[];
  extensions: readonly ExtensionState[];
  /** 握手失败、崩溃或失去响应的视图。Views whose handshake failed, crashed or hung. */
  viewErrors: Readonly<Record<string, ExtensionFailure>>;
  /** 有未保存修改的文件查看器。File viewers with unsaved changes. */
  unsavedViewIds: readonly string[];
  /** 文件查看器报告的保存状态，按视图 id 记录。The save statuses file viewers reported, by view id. */
  saveStatuses: Readonly<Record<string, DocumentSaveStatus>>;
};

type Deferred = {
  promise: Promise<void>;
  resolve(): void;
  reject(error: Error): void;
  settled: boolean;
};

function deferred(): Deferred {
  let resolve!: () => void;
  let reject!: (error: Error) => void;
  const promise = new Promise<void>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  const result: Deferred = {
    promise,
    settled: false,
    resolve: () => {
      result.settled = true;
      resolve();
    },
    reject: (error) => {
      result.settled = true;
      reject(error);
    },
  };
  promise.catch(() => undefined);
  return result;
}

type HostFrame = {
  frameId: string;
  extensionId: string;
  kind: ExtensionFrameKind;
  /** 完整视图 id；background 为 null，设置页为 `settings`。Full view id; null for background, `settings` for settings. */
  viewId: string | null;
  params: unknown;
  pageId: string | null;
  handshake: Deferred;
  handshakeTimer: ReturnType<typeof setTimeout> | null;
  closing: Promise<void> | null;
  lastPong: number;
  terminated: boolean;
  shown: boolean;
  loaded: boolean;
  snapshot: string | null;
  /** 主进程遮挡请求的序号，画出截图后用它隐藏 view。The main-process obscure token used to hide the view once painted. */
  snapshotToken: number;
  wantsObscured: boolean;
  obscureRequest: number;
};

type ExtensionRecord = {
  installed: InstalledExtension;
  status: ExtensionRuntimeStatus;
  error: ExtensionFailure | null;
  blocked: BlockedRequest[];
  activation: AbortController | null;
  storageUri: string | null;
  registered: boolean;
};

export type ExtensionHostOptions = {
  /** 普通浏览器中为 null：布局照常工作，不加载 extension。Null in a browser: the layout works and no extension loads. */
  bridge: AgwDesktopBridge | null;
  app: HostContext["app"];
  notify(
    extensionName: string,
    notification: { type: "info" | "success" | "error"; message: string },
  ): void;
};

/** 本地绝对路径转换为 `file:` URI。Converts a local absolute path into a `file:` URI. */
export function toFileUri(path: string): string {
  const normalized = path.replaceAll("\\", "/");
  const prefixed = /^[A-Za-z]:\//u.test(normalized) ? `/${normalized}` : normalized;
  return `file://${prefixed
    .split("/")
    .map((segment, index) =>
      index === 1 && /^[A-Za-z]:$/u.test(segment) ? segment : encodeURIComponent(segment),
    )
    .join("/")}`;
}

function failureOf(error: unknown, fallbackCode: string): ExtensionFailure {
  const code = (error as { code?: unknown } | null)?.code;
  return {
    code: typeof code === "string" ? code : fallbackCode,
    message: error instanceof Error ? error.message : String(error),
  };
}

function withTimeout<T>(
  promise: Promise<T>,
  timeoutMs: number,
  code: string,
  message: string,
): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  return Promise.race([
    promise,
    new Promise<never>((_, reject) => {
      timer = setTimeout(() => reject(createExtensionError(code, message)), timeoutMs);
    }),
  ]).finally(() => clearTimeout(timer));
}

function abortable<T>(promise: Promise<T>, signal: AbortSignal): Promise<T> {
  return Promise.race([
    promise,
    new Promise<never>((_, reject) => {
      const fail = () =>
        reject(createExtensionError("ERR_ACTIVATION_CANCELED", "The activation was canceled."));
      if (signal.aborted) fail();
      else signal.addEventListener("abort", fail, { once: true });
    }),
  ]);
}

/** 等到下一帧画出之后。Resolves once the next frame has been painted. */
function afterNextPaint(): Promise<void> {
  return new Promise((resolve) =>
    requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
  );
}

/**
 * extension 的生命周期：激活、停用、frame 关闭流程、心跳、页面替换与进程结束。
 * The extension lifecycle: activation, deactivation, the frame close flow, heartbeats, page replacement and process exits.
 */
export class ExtensionHost {
  public readonly hostInstanceId = crypto.randomUUID();
  public readonly registry: WorkspaceRegistry;
  public readonly layout: WorkspaceLayoutStore;
  public readonly context: ContextService;
  public readonly cloudGrants = new CloudAccessGrants();
  public readonly execution: ExecutionService;
  private readonly broker: ExtensionBridgeBroker;
  private readonly desktopBridge: AgwDesktopBridge | null;

  private startPromise: Promise<void> | null = null;
  private started = false;
  private safeMode: ExtensionSafeModeState | null = null;
  private recoveryNotices: ExtensionRecoveryNotice[] = [];
  private readonly records = new Map<string, ExtensionRecord>();
  private readonly frames = new Map<string, HostFrame>();
  private readonly viewFrames = new Map<string, HostFrame>();
  private readonly viewHolds = new Map<string, number>();
  private readonly viewReleaseTimers = new Map<string, ReturnType<typeof setTimeout>>();
  private readonly viewErrors = new Map<string, ExtensionFailure>();
  private readonly pendingLayout = new Map<
    string,
    { bounds: ExtensionFrameBounds; visible: boolean }
  >();
  private layoutFlushScheduled = false;
  private workspaceHolds = 0;
  private workspaceReleaseTimer: ReturnType<typeof setTimeout> | null = null;
  private heartbeatTimer: ReturnType<typeof setInterval> | null = null;
  private pingSequence = 0;
  private theme: HostTheme = {
    theme: { id: DEFAULT_THEME_ID, title: DEFAULT_THEME_TITLE, kind: "light", tokens: {} },
    style: null,
  };
  private themeBackground = "#ffffff";
  private composer: { insertText(text: string): void } | null = null;
  private refreshConversations: ((projectId: string) => void) | null = null;
  private fileOpener: ((location: FileOpenLocation) => Promise<boolean>) | null = null;
  private fileWriteListener: ((location: FileWriteLocation) => void) | null = null;
  /** 文件查看器当前显示的文件，按视图 id 记录。The file each file viewer shows, by view id. */
  private readonly fileViewerFiles = new Map<string, FileViewerFile>();
  /** 报告了未保存修改的 frame。Frames that reported unsaved changes. */
  private readonly unsavedFrames = new Set<string>();
  /** frame 报告的保存状态：frameId → 状态。Save statuses frames reported: frameId → status. */
  private readonly saveStatuses = new Map<string, DocumentSaveStatus>();
  /** 等待回答的消息框：clientId → 发出它的页面。Message boxes awaiting answers: clientId → the page that asked. */
  private readonly messageBoxes = new Map<string, PendingMessageBox>();
  private nextMessageBoxId = 1;
  private readonly listeners = new Set<() => void>();
  private snapshot: ExtensionHostSnapshot = {
    started: false,
    safeMode: null,
    recoveryNotices: [],
    extensions: [],
    viewErrors: {},
    unsavedViewIds: [],
    saveStatuses: {},
  };

  public constructor(private readonly options: ExtensionHostOptions) {
    this.desktopBridge = options.bridge;
    this.registry = new WorkspaceRegistry({
      views: [
        builtInView(BUILT_IN_VIEWS.conversations, "sidebar", "Conversations", "MessagesSquare"),
        builtInView(BUILT_IN_VIEWS.files, "sidebar", "Files", "FolderTree"),
        builtInView(BUILT_IN_VIEWS.chat, "main", "Chat", "MessageSquare"),
        builtInView(BUILT_IN_VIEWS.file, "main", "File", "FileText"),
      ],
      entries: {
        ribbon: [
          {
            id: "agw.ribbon.conversations",
            extensionId: null,
            extensionName: null,
            title: "Conversations",
            icon: { kind: "lucide", name: "MessagesSquare" },
            action: { type: "toggleSidebarView", view: BUILT_IN_VIEWS.conversations },
          },
          {
            id: "agw.ribbon.files",
            extensionId: null,
            extensionName: null,
            title: "Files",
            icon: { kind: "lucide", name: "FolderTree" },
            action: { type: "toggleSidebarView", view: BUILT_IN_VIEWS.files },
          },
        ],
      },
    });
    this.layout = new WorkspaceLayoutStore(this.registry);
    this.context = new ContextService(options.app, this.workspaceState());
    this.layout.subscribe(() => this.context.setWorkspace(this.workspaceState()));
    this.context.onConnectionChanged(() => this.cloudGrants.clear());
    const cloud = new CloudFileSystem(this.context, this.cloudGrants, (location) =>
      this.fileWriteListener?.(location),
    );
    this.execution = new ExecutionService({
      context: this.context,
      deliver: (owner, event, data) => this.broker.deliverExecution(owner, event, data),
      refreshConversations: (projectId) => this.refreshConversations?.(projectId),
      chat: chatHttpClient,
    });
    this.broker = new ExtensionBridgeBroker({
      bridge: () => this.bridge,
      codec: () => this.bridge,
      hostInstanceId: this.hostInstanceId,
      context: this.context,
      layout: this.layout,
      cloud,
      execution: this.execution,
      a2a: new A2AService(this.context),
      chat: chatHttpClient,
      extension: (extensionId) => this.brokerExtension(extensionId),
      theme: () => this.theme,
      isFrameClosing: (frameId) => this.frames.get(frameId)?.closing !== null,
      insertComposerText: (text) => {
        if (!this.composer)
          throw createExtensionError("ERR_UNAVAILABLE", "The chat composer is not available.");
        this.composer.insertText(text);
      },
      notify: (extensionId, notification) =>
        options.notify(
          this.records.get(extensionId)?.installed.manifest?.name ?? extensionId,
          notification,
        ),
      onPong: (frameId, pageId) => {
        const frame = this.frames.get(frameId);
        if (frame && frame.pageId === pageId) frame.lastPong = Date.now();
      },
      openFile: (extensionId, uri) => this.openFile(extensionId, uri),
      setUnsavedChanges: (frameId, pageId, unsaved) => {
        const frame = this.frames.get(frameId);
        if (frame?.kind === "fileViewer" && frame.pageId === pageId && frame.closing === null) {
          this.setUnsaved(frameId, unsaved);
        }
      },
      setSaveStatus: (frameId, pageId, status) => {
        const frame = this.frames.get(frameId);
        if (frame?.kind === "fileViewer" && frame.pageId === pageId && frame.closing === null) {
          this.setSaveStatus(frameId, status);
        }
      },
      confirmOpenExternal: (extensionId, url) =>
        this.confirm({
          kind: "open-link",
          extensionId,
          extensionName: this.records.get(extensionId)?.installed.manifest?.name ?? extensionId,
          url,
        }),
      showMessageBox: (owner, request) => this.showMessageBox(owner, request),
    });
  }

  /** 只有 Desktop 中加载 extension 时才会用到。Only reached while extensions load in Desktop. */
  private get bridge(): AgwDesktopBridge {
    if (!this.desktopBridge) throw new Error("Extensions require Agw Desktop.");
    return this.desktopBridge;
  }

  /* ---------- 订阅 Subscription ---------- */

  public getSnapshot = (): ExtensionHostSnapshot => this.snapshot;

  public subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  /* ---------- 启动 Startup ---------- */

  /** 幂等：StrictMode 下重复调用不会重复激活。Idempotent, so StrictMode never activates twice. */
  public start(): Promise<void> {
    if (!this.desktopBridge) return Promise.resolve();
    this.startPromise ??= this.startOnce();
    return this.startPromise;
  }

  public async resume(): Promise<void> {
    const state = await this.bridge.resumeExtensions();
    this.safeMode = state.extensionSafeMode;
    this.publish();
    await this.activateEnabled();
  }

  private async startOnce(): Promise<void> {
    window.addEventListener("message", this.onWindowMessage);
    window.addEventListener("beforeunload", this.onBeforeUnload);
    this.bridge.onExtensionEvent((event) => this.onMainEvent(event));
    await this.bridge.attachExtensionHost(this.hostInstanceId);
    this.heartbeatTimer = setInterval(() => this.heartbeat(), HEARTBEAT_INTERVAL_MS);
    const state = await this.bridge.getRuntimeState();
    this.safeMode = state.extensionSafeMode;
    this.recoveryNotices = await this.bridge.listExtensionRecoveryNotices();
    await this.refreshList();
    this.started = true;
    this.publish();
    await this.bridge.setExtensionFrameBackground(this.hostInstanceId, this.themeBackground);
    await this.activateEnabled();
  }

  private async activateEnabled(): Promise<void> {
    if (this.safeMode) return;
    await Promise.all(
      [...this.records.values()]
        .filter((record) => record.installed.enabled && record.installed.validation === "valid")
        .map((record) => this.activate(record.installed.id)),
    );
  }

  /* ---------- Extensions 页面操作 Extensions page actions ---------- */

  public async rescan(): Promise<void> {
    await this.refreshList();
  }

  /**
   * 启用：主进程在确认窗口中批准权限；用户取消时不激活。返回是否已经启用。
   * Enables: main approves the permissions in the confirmation window; nothing activates when the user cancels.
   * Returns whether the extension is enabled.
   */
  public async enable(extensionId: string): Promise<boolean> {
    const enabled = await this.bridge.setExtensionEnabled(extensionId, true);
    await this.refreshList();
    if (enabled) await this.activate(extensionId);
    return enabled;
  }

  public async disable(extensionId: string): Promise<void> {
    await this.deactivate(extensionId);
    await this.bridge.setExtensionEnabled(extensionId, false);
    await this.refreshList();
  }

  /** 停用 → 主进程重新校验 → 权限没有新增时重新激活。Deactivate, revalidate in main, reactivate unless grants grew. */
  public async reload(extensionId: string): Promise<void> {
    await this.deactivate(extensionId);
    for (const viewId of this.viewErrors.keys()) {
      if (
        this.registry.view(viewId)?.extensionId === extensionId ||
        viewId.startsWith(`${extensionId}:`)
      ) {
        this.viewErrors.delete(viewId);
      }
    }
    await this.bridge.reloadExtension(extensionId);
    await this.refreshList();
    await this.activate(extensionId);
  }

  public async uninstall(extensionId: string, deleteData: boolean): Promise<void> {
    await this.deactivate(extensionId);
    await this.bridge.uninstallExtension(extensionId, { deleteData });
    await this.refreshList();
  }

  /** 替换安装前停用已安装版本。Deactivates the installed version before a replacement install. */
  public async prepareReplace(extensionId: string): Promise<void> {
    await this.deactivate(extensionId);
  }

  /** 提交安装后，仍为启用状态时激活新版本。Activates the new version after commit when still enabled. */
  public async afterInstall(extensionId: string): Promise<void> {
    await this.refreshList();
    await this.activate(extensionId);
  }

  /* ---------- 工作区 Workspace ---------- */

  /** 当前选中的 CSS 主题的完整样式文本。The complete style text of the selected CSS theme. */
  public getThemeStyle(request: ThemeStyleRequest): Promise<ThemeStyleText> {
    return this.bridge.getThemeStyle(request);
  }

  /** 宿主的主题与页面背景色；CSS 主题带有样式文本。The host theme and page background; CSS themes carry their style text. */
  public setTheme(theme: HostTheme, background: string): void {
    this.theme = theme;
    this.themeBackground = background;
    this.broker.broadcastTheme(theme);
    if (this.started) void this.bridge.setExtensionFrameBackground(this.hostInstanceId, background);
  }

  public setComposer(composer: { insertText(text: string): void } | null): void {
    this.composer = composer;
  }

  public setConversationRefresher(refresh: ((projectId: string) => void) | null): void {
    this.refreshConversations = refresh;
  }

  /** extension 经云端文件系统写入文件成功后调用。Called after an extension wrote a file through the cloud file system. */
  public setFileWriteListener(listener: ((location: FileWriteLocation) => void) | null): void {
    this.fileWriteListener = listener;
  }

  /** 贡献点被点击：打开视图或在 background 中执行命令。Runs a contribution: open a view or execute a background command. */
  public runAction(extensionId: string | null, action: ExtensionAction, args: unknown): void {
    if (action.type === "openMainView") {
      this.layout.openMainView(action.view, args);
      return;
    }
    if (action.type === "toggleSidebarView") {
      this.layout.toggleSidebarView(action.view);
      return;
    }
    const background = [...this.frames.values()].find(
      (frame) => frame.extensionId === extensionId && frame.kind === "background",
    );
    const name =
      (extensionId && this.records.get(extensionId)?.installed.manifest?.name) ?? extensionId ?? "";
    const run = background
      ? this.broker.call(background.frameId, "commands.execute", { command: action.command, args })
      : Promise.reject(
          createExtensionError("ERR_FRAME_GONE", "The extension has no running background."),
        );
    run.catch((error: unknown) => {
      console.error(`[extension ${extensionId}] Command ${action.command} failed.`, error);
      this.options.notify(name, { type: "error", message: "Command failed" });
    });
  }

  /** 文件浏览器右键菜单授予读取授权。Grants read access from the file explorer context menu. */
  public grantCloudRead(extensionId: string, uri: string, directory: boolean): void {
    const key = this.context.connectionKey;
    if (key !== null && !this.cloudGrants.allows(extensionId, uri, key)) {
      this.cloudGrants.grant(extensionId, uri, directory, key);
    }
  }

  /** Desktop 工作区提供的打开文件函数；返回 false 表示用户取消。The workspace's file opener; false means the user canceled. */
  public setFileOpener(opener: ((location: FileOpenLocation) => Promise<boolean>) | null): void {
    this.fileOpener = opener;
  }

  /**
   * 文件视图把文件交给文件查看器：授予读取权限；frame 已加载且文件变化时发送 `view.onShow`。
   * The Files view hands a file to a viewer: grants read access, and sends `view.onShow` when a loaded frame gets
   * another file.
   */
  public setFileViewerFile(viewId: string, file: FileViewerFile): void {
    const view = this.registry.view(viewId);
    if (view?.location !== "file" || view.extensionId === null) return;
    this.grantCloudRead(view.extensionId, file.uri, false);
    if (this.fileViewerFiles.get(viewId)?.uri === file.uri) return;
    this.fileViewerFiles.set(viewId, file);
    const frame = this.viewFrames.get(viewId);
    if (!frame || frame.closing !== null) return;
    const params: FileViewerParams = { file };
    frame.params = params;
    this.setUnsaved(frame.frameId, false);
    this.setSaveStatus(frame.frameId, null);
    if (frame.loaded) {
      void this.broker.call(frame.frameId, "view.onShow", { params }).catch((error: unknown) => {
        console.error(`[extension ${frame.extensionId}/${viewId}] onShow failed.`, error);
      });
    }
  }

  /** 只保留匹配当前文件的文件查看器，其余关闭。Keeps the file viewers matching the current file and closes the rest. */
  public retainFileViewers(viewIds: readonly string[]): void {
    for (const [viewId, frame] of this.viewFrames) {
      if (frame.kind === "fileViewer" && !viewIds.includes(viewId)) {
        void this.closeFrame(frame, "frame-unloaded", true);
      }
    }
    for (const viewId of this.fileViewerFiles.keys()) {
      if (!viewIds.includes(viewId)) this.fileViewerFiles.delete(viewId);
    }
  }

  public hasUnsavedChanges(): boolean {
    return this.unsavedFrames.size > 0;
  }

  /**
   * 存在未保存修改时由用户确认放弃；传入 `viewIds` 时只考虑这些视图。
   * Asks the user to discard unsaved changes when there are any; with `viewIds`, only those views count.
   */
  public confirmDiscardUnsaved(viewIds?: readonly string[]): Promise<boolean> {
    const items = [...this.unsavedFrames].flatMap((frameId) => {
      const frame = this.frames.get(frameId);
      const view = frame?.viewId ? this.registry.view(frame.viewId) : undefined;
      if (!frame || !view || (viewIds !== undefined && !viewIds.includes(view.id))) return [];
      return [
        {
          fileName: (frame.params as FileViewerParams).file.name,
          viewerTitle: view.title,
          extensionId: frame.extensionId,
          extensionName: view.extensionName ?? frame.extensionId,
        },
      ];
    });
    return items.length === 0
      ? Promise.resolve(true)
      : this.confirm({ kind: "discard-unsaved", items });
  }

  /** 外壳挂载；卸载时关闭全部视图 frame（下一轮执行，StrictMode 重新挂载会取消）。Shell mount; unmount closes every view frame. */
  public attachWorkspace(): () => void {
    this.workspaceHolds += 1;
    if (this.workspaceReleaseTimer !== null) {
      clearTimeout(this.workspaceReleaseTimer);
      this.workspaceReleaseTimer = null;
    }
    return () => {
      this.workspaceHolds -= 1;
      if (this.workspaceHolds > 0) return;
      this.workspaceReleaseTimer = setTimeout(() => {
        this.workspaceReleaseTimer = null;
        for (const frame of this.viewFrames.values()) {
          if (frame.kind !== "settings") void this.closeFrame(frame, "frame-unloaded", true);
        }
      }, 0);
    };
  }

  /** 视图占位组件挂载。A view slot mounted. */
  public acquireView(viewId: string): () => void {
    this.viewHolds.set(viewId, (this.viewHolds.get(viewId) ?? 0) + 1);
    const timer = this.viewReleaseTimers.get(viewId);
    if (timer !== undefined) {
      clearTimeout(timer);
      this.viewReleaseTimers.delete(viewId);
    }
    if (this.viewHolds.get(viewId) === 1) void this.showView(viewId);
    return () => {
      const holds = (this.viewHolds.get(viewId) ?? 1) - 1;
      if (holds > 0) {
        this.viewHolds.set(viewId, holds);
        return;
      }
      this.viewHolds.delete(viewId);
      this.viewReleaseTimers.set(
        viewId,
        setTimeout(() => {
          this.viewReleaseTimers.delete(viewId);
          void this.hideView(viewId);
        }, 0),
      );
    };
  }

  /** 视图已经显示时再次打开。The view is opened again while shown. */
  public reopenView(viewId: string, params: unknown): void {
    const frame = this.viewFrames.get(viewId);
    if (!frame || !frame.loaded || frame.closing) return;
    frame.params = params;
    void this.broker.call(frame.frameId, "view.onShow", { params }).catch((error: unknown) => {
      console.error(`[extension ${frame.extensionId}/${viewId}] onShow failed.`, error);
    });
  }

  /** 错误面板的 "Reload view"。The error panel's "Reload view". */
  public async reloadView(viewId: string): Promise<void> {
    this.viewErrors.delete(viewId);
    const frame = this.viewFrames.get(viewId);
    if (frame) await this.closeFrame(frame, "frame-unloaded", false);
    this.publish();
    if ((this.viewHolds.get(viewId) ?? 0) > 0) await this.showView(viewId);
  }

  /**
   * 文件名后 "Changed on disk" 的 Reload：文件查看器重新读取磁盘内容已变化的文档，有未保存修改时先确认放弃。
   * The Reload after "Changed on disk" next to the file name: the file viewer reads its documents changed on disk
   * again, confirming discarding unsaved changes first.
   */
  public reloadFileViewerDocument(viewId: string): void {
    const frame = this.viewFrames.get(viewId);
    if (frame?.kind !== "fileViewer" || !frame.loaded || frame.closing !== null) return;
    this.broker.emit(frame.frameId, frame.pageId, "documents.reloadRequested", null);
  }

  public frameIdOfView(viewId: string): string | null {
    return this.viewFrames.get(viewId)?.frameId ?? null;
  }

  public reportLayout(frameId: string, bounds: ExtensionFrameBounds, visible: boolean): void {
    if (!this.desktopBridge) return;
    this.pendingLayout.set(frameId, { bounds, visible });
    if (this.layoutFlushScheduled) return;
    this.layoutFlushScheduled = true;
    requestAnimationFrame(() => {
      this.layoutFlushScheduled = false;
      const updates = [...this.pendingLayout].map(([id, layout]) => ({ frameId: id, ...layout }));
      this.pendingLayout.clear();
      if (updates.length > 0)
        void this.bridge.updateExtensionFrameLayout(this.hostInstanceId, updates);
    });
  }

  /**
   * 弹出层遮挡的 frame 换成截图。截图在占位组件中画出后才隐藏 view（`snapshotShown`），恢复时 view 显示后才移除截图，
   * 切换期间不出现空白。
   * Frames under host overlays are swapped for snapshots. The view hides only after its slot painted the snapshot
   * (`snapshotShown`), and on reveal the snapshot goes only after the view shows, so no blank shows in between.
   */
  public setObscuredFrames(frameIds: ReadonlySet<string>): void {
    for (const frame of this.frames.values()) {
      if (frame.kind === "background") continue;
      const wanted = frameIds.has(frame.frameId);
      if (wanted && !frame.wantsObscured) {
        frame.wantsObscured = true;
        const request = ++frame.obscureRequest;
        void this.bridge
          .obscureExtensionFrames(this.hostInstanceId, [frame.frameId])
          .then(([result]) => {
            if (!frame.wantsObscured || frame.obscureRequest !== request || !result?.snapshot)
              return;
            frame.snapshot = result.snapshot;
            frame.snapshotToken = result.token;
            this.publish();
          })
          .catch((error: unknown) => console.error("Unable to obscure an extension view.", error));
      } else if (!wanted && frame.wantsObscured) {
        frame.wantsObscured = false;
        const request = ++frame.obscureRequest;
        void this.bridge
          .revealExtensionFrames(this.hostInstanceId, [frame.frameId])
          .then(afterNextPaint)
          .then(() => {
            if (frame.obscureRequest !== request || frame.snapshot === null) return;
            frame.snapshot = null;
            this.publish();
          })
          .catch((error: unknown) => console.error("Unable to reveal an extension view.", error));
      }
    }
  }

  /**
   * 占位组件已加载截图：截图画出后隐藏 view。截图已过期（遮挡已结束或有更新的截图）时不处理。
   * The slot loaded the snapshot: hide the view once it is painted. Stale snapshots (the overlay ended or a newer
   * snapshot arrived) are ignored.
   */
  public snapshotShown(frameId: string, snapshot: string): void {
    const frame = this.frames.get(frameId);
    if (!frame || frame.snapshot !== snapshot) return;
    const request = frame.obscureRequest;
    void afterNextPaint()
      .then(() => {
        if (!frame.wantsObscured || frame.obscureRequest !== request || frame.snapshot !== snapshot)
          return;
        return this.bridge.hideObscuredExtensionFrames(this.hostInstanceId, [
          { frameId, token: frame.snapshotToken },
        ]);
      })
      .catch((error: unknown) =>
        console.error("Unable to hide an obscured extension view.", error),
      );
  }

  public visibleFrameIds(): string[] {
    return [...this.frames.values()]
      .filter((frame) => frame.kind !== "background" && frame.shown && frame.closing === null)
      .map((frame) => frame.frameId);
  }

  public openDevTools(frameId: string): void {
    void this.bridge.openExtensionFrameDevTools(this.hostInstanceId, frameId);
  }

  /** Extensions 设置页显示的设置页 frame。The settings frame shown on the extension settings page. */
  public acquireSettings(extensionId: string): () => void {
    return this.acquireView(settingsViewId(extensionId));
  }

  /* ---------- 生命周期 Lifecycle ---------- */

  private async activate(extensionId: string): Promise<void> {
    const record = this.records.get(extensionId);
    if (!record || this.safeMode) return;
    if (record.status !== "inactive" && record.status !== "failed") return;
    const { installed } = record;
    const manifest = installed.manifest;
    if (!installed.enabled || installed.validation !== "valid" || !manifest) return;

    if (record.status === "failed") this.unregister(record);
    record.status = "activating";
    record.error = null;
    const abort = new AbortController();
    record.activation = abort;
    this.publish();
    try {
      const { storageDirectory } = await abortable(
        this.bridge.beginExtensionActivation(this.hostInstanceId, extensionId),
        abort.signal,
      );
      record.storageUri = toFileUri(storageDirectory);
      if (manifest.background !== undefined) {
        const frame = await this.createFrame(extensionId, "background", null, null, {});
        await abortable(frame.handshake.promise, abort.signal);
        const previous = installed.lastActivatedVersion;
        const reason =
          previous === null ? "install" : previous !== manifest.version ? "update" : null;
        if (reason !== null) {
          await this.activationHook(
            frame,
            "lifecycle.onInstalled",
            {
              reason,
              previousVersion: reason === "update" ? previous : null,
            },
            abort.signal,
          );
        }
        await this.activationHook(frame, "lifecycle.activate", {}, abort.signal);
      }
      if (abort.signal.aborted) return;
      this.registry.registerExtension(extensionId, extensionRegistration(installed));
      record.registered = true;
      record.status = "active";
      this.publish();
      await this.bridge.recordExtensionActivation(
        this.hostInstanceId,
        extensionId,
        manifest.version,
      );
      record.installed = { ...record.installed, lastActivatedVersion: manifest.version };
    } catch (error) {
      if (!abort.signal.aborted)
        await this.fail(extensionId, failureOf(error, "ACTIVATION_FAILED"));
    } finally {
      if (abort.signal.aborted) {
        // 取消时主进程可能刚完成 beginExtensionActivation，再释放一次出站代理与授权。
        // Main may have just finished beginExtensionActivation when canceled, so release once more.
        await this.releaseExtension(extensionId).catch(() => undefined);
      }
      if (record.activation === abort) record.activation = null;
      await this.bridge
        .endExtensionActivation(this.hostInstanceId, extensionId)
        .catch(() => undefined);
      this.publish();
    }
  }

  private async activationHook(
    frame: HostFrame,
    method: string,
    params: unknown,
    signal: AbortSignal,
  ): Promise<void> {
    try {
      await abortable(
        withTimeout(
          this.broker.call(frame.frameId, method, params),
          ACTIVATION_HOOK_TIMEOUT_MS,
          "ACTIVATION_TIMEOUT",
          `${method} did not finish within ${ACTIVATION_HOOK_TIMEOUT_MS / 1000} seconds.`,
        ),
        signal,
      );
    } catch (error) {
      const code = (error as { code?: unknown }).code;
      if (code === "ACTIVATION_TIMEOUT" || code === "ERR_ACTIVATION_CANCELED") throw error;
      throw createExtensionError(
        "ACTIVATION_FAILED",
        error instanceof Error ? error.message : String(error),
      );
    }
  }

  /** disable、reload、uninstall、替换安装。Disable, reload, uninstall or replacement install. */
  private async deactivate(extensionId: string): Promise<void> {
    const record = this.records.get(extensionId);
    if (!record) return;
    if (record.status === "activating") {
      record.activation?.abort();
      await this.cleanUpWithoutHooks(record);
      record.status = "inactive";
      this.publish();
      return;
    }
    if (record.status === "failed") {
      this.unregister(record);
      record.status = "inactive";
      record.error = null;
      this.publish();
      return;
    }
    if (record.status !== "active") return;

    record.status = "deactivating";
    this.unregister(record);
    this.publish();
    const frames = [...this.frames.values()].filter((frame) => frame.extensionId === extensionId);
    for (const frame of frames.filter((item) => item.kind !== "background")) {
      await this.closeFrame(frame, "deactivated", true);
    }
    for (const frame of frames.filter((item) => item.kind === "background")) {
      await this.closeFrame(frame, "deactivated", true);
    }
    await this.releaseExtension(extensionId);
    record.status = "inactive";
    this.publish();
  }

  /** 进入 failed：不调用钩子、不发送 `execution.closed`。Enter failed without hooks or `execution.closed`. */
  private async fail(extensionId: string, failure: ExtensionFailure): Promise<void> {
    const record = this.records.get(extensionId);
    if (!record || record.status === "failed") return;
    const wasRegistered = record.registered;
    record.activation?.abort();
    record.status = "failed";
    record.error = failure;
    if (wasRegistered) {
      const views = this.registry.getSnapshot().views;
      this.registry.markExtensionFailed(
        extensionId,
        failure,
        [...views.values()].filter((view) => view.extensionId === extensionId),
      );
    }
    console.error(`[extension ${extensionId}] ${failure.code}: ${failure.message}`);
    this.publish();
    await this.cleanUpWithoutHooks(record);
    this.publish();
  }

  private async cleanUpWithoutHooks(record: ExtensionRecord): Promise<void> {
    const extensionId = record.installed.id;
    await this.execution.closeExtension(extensionId, "deactivated", false);
    for (const frame of [...this.frames.values()].filter(
      (item) => item.extensionId === extensionId,
    )) {
      await this.closeFrame(frame, "deactivated", false);
    }
    await this.releaseExtension(extensionId);
  }

  private async releaseExtension(extensionId: string): Promise<void> {
    this.cloudGrants.release(extensionId);
    await this.bridge.releaseExtensionResources(this.hostInstanceId, extensionId);
  }

  private unregister(record: ExtensionRecord): void {
    if (!record.registered && record.status !== "failed") return;
    record.registered = false;
    this.registry.unregisterExtension(record.installed.id);
  }

  /* ---------- frame ---------- */

  private async createFrame(
    extensionId: string,
    kind: ExtensionFrameKind,
    viewId: string | null,
    entry: string | null,
    params: unknown,
  ): Promise<HostFrame> {
    const frame: HostFrame = {
      frameId: crypto.randomUUID(),
      extensionId,
      kind,
      viewId,
      params,
      pageId: null,
      handshake: deferred(),
      handshakeTimer: null,
      closing: null,
      lastPong: Date.now(),
      terminated: false,
      shown: false,
      loaded: false,
      snapshot: null,
      snapshotToken: 0,
      wantsObscured: false,
      obscureRequest: 0,
    };
    this.frames.set(frame.frameId, frame);
    if (viewId !== null) this.viewFrames.set(viewId, frame);
    this.startHandshakeTimer(frame);
    this.publish();
    await this.bridge.createExtensionFrame(this.hostInstanceId, {
      extensionId,
      frameId: frame.frameId,
      kind,
      viewId: viewId === null ? null : localViewId(viewId),
      entry,
      backgroundColor: this.themeBackground,
    });
    return frame;
  }

  private startHandshakeTimer(frame: HostFrame): void {
    if (frame.handshakeTimer !== null) clearTimeout(frame.handshakeTimer);
    frame.handshakeTimer = setTimeout(() => {
      frame.handshakeTimer = null;
      void this.frameFailed(frame, {
        code: "HANDSHAKE_TIMEOUT",
        message: `The page did not connect within ${HANDSHAKE_TIMEOUT_MS / 1000} seconds. Make sure it loads /__agw/sdk.js.`,
      });
    }, HANDSHAKE_TIMEOUT_MS);
  }

  /**
   * frame 关闭流程：标记关闭 → 关闭会话 → 钩子 → 销毁 → 释放资源。没有钩子时跳过第 2、3 步的通知与钩子。
   * The frame close flow: mark closing, close sessions, hooks, destroy, release resources.
   */
  private closeFrame(
    frame: HostFrame,
    reason: ExecutionCloseReason,
    hooks: boolean,
  ): Promise<void> {
    frame.closing ??= (async () => {
      this.publish();
      this.dismissMessageBoxes(frame.frameId);
      if (frame.handshakeTimer !== null) clearTimeout(frame.handshakeTimer);
      if (!frame.handshake.settled)
        frame.handshake.reject(createExtensionError("ERR_FRAME_CLOSING", "The frame is closing."));
      this.execution.abortPendingCreations(
        frame.frameId,
        createExtensionError("ERR_FRAME_CLOSING", "The frame is closing."),
      );
      const connected = this.broker.isConnected(frame.frameId);
      await this.execution.closeFrame(frame.frameId, reason, hooks && connected);
      if (hooks && connected) {
        const [method, timeout] =
          frame.kind === "background"
            ? (["lifecycle.deactivate", DEACTIVATE_TIMEOUT_MS] as const)
            : (["view.onUnload", UNLOAD_TIMEOUT_MS] as const);
        await withTimeout(
          this.broker.call(frame.frameId, method, {}),
          timeout,
          "HOOK_TIMEOUT",
          `${method} timed out.`,
        ).catch((error: unknown) =>
          console.warn(`[extension ${frame.extensionId}] ${method}:`, error),
        );
      }
      await this.bridge
        .destroyExtensionFrame(this.hostInstanceId, frame.frameId)
        .catch(() => undefined);
      this.broker.disconnect(frame.frameId);
      await this.bridge
        .releaseExtensionFrameResources(this.hostInstanceId, frame.extensionId, frame.frameId)
        .catch(() => undefined);
      this.frames.delete(frame.frameId);
      this.unsavedFrames.delete(frame.frameId);
      this.saveStatuses.delete(frame.frameId);
      if (frame.viewId !== null && this.viewFrames.get(frame.viewId) === frame)
        this.viewFrames.delete(frame.viewId);
      this.publish();
    })();
    return frame.closing;
  }

  /** 视图 frame 失败：只影响该视图。A view frame failure affects only that view. */
  private async frameFailed(frame: HostFrame, failure: ExtensionFailure): Promise<void> {
    if (frame.closing) return;
    if (!frame.handshake.settled)
      frame.handshake.reject(createExtensionError(failure.code, failure.message));
    if (frame.kind === "background") {
      const record = this.records.get(frame.extensionId);
      if (record?.status === "activating") {
        await this.closeFrame(frame, "deactivated", false);
        return;
      }
      await this.fail(frame.extensionId, failure);
      return;
    }
    if (frame.viewId !== null) this.viewErrors.set(frame.viewId, failure);
    await this.closeFrame(frame, "frame-unloaded", false);
  }

  private async showView(viewId: string): Promise<void> {
    const view = this.registry.view(viewId) ?? this.settingsView(viewId);
    if (!view || view.extensionId === null || view.failure || this.viewErrors.has(viewId)) return;
    const record = this.records.get(view.extensionId);
    if (!record || record.status !== "active") return;
    const file = this.fileViewerFiles.get(viewId);
    if (view.location === "file" && !file) return;
    const params: unknown =
      view.location === "file"
        ? ({ file } satisfies Partial<FileViewerParams>)
        : (this.layout.getSnapshot().openRequests[viewId]?.params ?? {});
    const existing = this.viewFrames.get(viewId);
    if (existing && existing.closing === null) {
      const wasShown = existing.shown;
      existing.shown = true;
      // 文件查看器从隐藏变为显示时收到 onShow；释放计时器被取消时 frame 一直显示，不重复发送。
      // File viewers get onShow when they go from hidden to shown; after a canceled release timer the frame stayed
      // shown, so nothing is sent again.
      if (existing.loaded && (existing.kind !== "fileViewer" || !wasShown))
        this.reopenView(viewId, params);
      return;
    }
    const kind: ExtensionFrameKind =
      viewId === settingsViewId(view.extensionId)
        ? "settings"
        : view.location === "main"
          ? "mainView"
          : view.location === "file"
            ? "fileViewer"
            : "sidebarView";
    let frame: HostFrame;
    try {
      frame = await this.createFrame(view.extensionId, kind, viewId, view.entry, params);
    } catch (error) {
      this.viewErrors.set(viewId, failureOf(error, "LOAD_FAILED"));
      this.publish();
      return;
    }
    frame.shown = true;
    try {
      await frame.handshake.promise;
    } catch {
      return;
    }
    await this.loadView(frame);
  }

  private async loadView(frame: HostFrame): Promise<void> {
    try {
      const loadedParams = frame.params;
      await this.broker.call(frame.frameId, "view.onLoad", { params: loadedParams });
      frame.loaded = true;
      // 文件查看器在 onLoad 中收到文件；onLoad 期间文件变化时补发 onShow。
      // File viewers get their file in onLoad; a file change during onLoad is sent with onShow.
      if (frame.kind === "fileViewer") {
        if (frame.params !== loadedParams)
          await this.broker.call(frame.frameId, "view.onShow", { params: frame.params });
      } else if (frame.shown) {
        await this.broker.call(frame.frameId, "view.onShow", { params: frame.params });
      }
    } catch (error) {
      console.error(`[extension ${frame.extensionId}/${frame.viewId}] view hook failed.`, error);
    }
  }

  private async hideView(viewId: string): Promise<void> {
    const frame = this.viewFrames.get(viewId);
    if (!frame || frame.closing) return;
    frame.shown = false;
    const view = this.registry.view(viewId);
    // 保留页面的主区视图与文件查看器只隐藏。Retained main views and file viewers are only hidden.
    if (
      (view?.location === "main" && view.retainContextWhenHidden && frame.kind === "mainView") ||
      frame.kind === "fileViewer"
    ) {
      if (frame.loaded) {
        void this.broker.call(frame.frameId, "view.onHide", {}).catch((error: unknown) => {
          console.error(`[extension ${frame.extensionId}/${viewId}] onHide failed.`, error);
        });
      }
      return;
    }
    await this.closeFrame(frame, "frame-unloaded", true);
  }

  /* ---------- 主进程事件与端口 Main events and ports ---------- */

  private readonly onWindowMessage = (event: MessageEvent) => {
    const data = event.data as Partial<ExtensionPortMessage> | null;
    if (event.source !== window || data?.type !== "agw:extension-port" || event.ports.length !== 1)
      return;
    this.attachPort(data as ExtensionPortMessage, event.ports[0]);
  };

  private attachPort(message: ExtensionPortMessage, port: MessagePort): void {
    const frame = this.frames.get(message.frameId);
    if (!frame || frame.closing || frame.extensionId !== message.extensionId) {
      port.close();
      return;
    }
    if (frame.handshakeTimer !== null) {
      clearTimeout(frame.handshakeTimer);
      frame.handshakeTimer = null;
    }
    const reloaded = frame.handshake.settled;
    frame.pageId = message.pageId;
    frame.lastPong = Date.now();
    this.broker.connect(
      {
        extensionId: frame.extensionId,
        frameId: frame.frameId,
        pageId: message.pageId,
        kind: frame.kind,
        viewId: frame.viewId === null ? null : localViewId(frame.viewId),
        params: frame.params,
      },
      port,
    );
    if (!reloaded) frame.handshake.resolve();
    else if (frame.kind !== "background") void this.loadView(frame);
    this.publish();
  }

  private onMainEvent(event: ExtensionEvent): void {
    switch (event.type) {
      case "file-change":
        this.broker.emit(event.frameId, event.pageId, "fs.changed", {
          watchId: event.watchId,
          event: event.event,
        });
        return;
      case "request-blocked": {
        const record = this.records.get(event.extensionId);
        if (!record) return;
        record.blocked = [
          {
            url: event.url,
            resourceType: event.resourceType,
            reason: event.reason,
            time: new Date().toISOString(),
          },
          ...record.blocked,
        ].slice(0, MAX_BLOCKED_REQUESTS);
        this.publish();
        return;
      }
      case "frame-failed": {
        const frame = this.frames.get(event.frameId);
        if (frame) void this.frameFailed(frame, { code: event.code, message: event.message });
        return;
      }
      case "frame-page-replaced": {
        const frame = this.frames.get(event.frameId);
        if (frame) void this.pageReplaced(frame, event.pageId);
        return;
      }
      case "frame-gone": {
        const frame = this.frames.get(event.frameId);
        if (!frame) return;
        void this.frameFailed(frame, {
          code: event.reason === "unresponsive" ? "UNRESPONSIVE" : "CRASHED",
          message:
            event.reason === "unresponsive"
              ? "The page stopped responding and its process was ended."
              : `The page process exited (${event.reason}).`,
        });
        return;
      }
      case "extension-failed":
        void this.fail(event.extensionId, { code: event.code, message: event.message });
        return;
    }
  }

  /**
   * 在主进程的独立确认窗口中确认，得到用户是否同意；主题不会作用于这个窗口。
   * Confirms in the main process's separate confirmation window and resolves with whether the user agreed; themes
   * never reach that window.
   */
  private async confirm(
    request: Exclude<HostConfirmationRequest, { kind: "message" }>,
  ): Promise<boolean> {
    return (await this.bridge.confirmHostAction(request)) === true;
  }

  /** 视图页面的消息框在确认窗口中显示（9.5 节）。A view page's message box shows in the confirmation window (section 9.5). */
  private showMessageBox(owner: SessionOwner, request: MessageBoxRequest): Promise<number> {
    const frame = this.frames.get(owner.frameId);
    if (!frame || frame.closing !== null || frame.pageId !== owner.pageId) {
      return Promise.reject(createExtensionError("ERR_FRAME_GONE", "The frame is gone."));
    }
    const clientId = `${this.hostInstanceId}:${this.nextMessageBoxId++}`;
    return new Promise((resolve, reject) => {
      const pending: PendingMessageBox = {
        frameId: owner.frameId,
        pageId: owner.pageId,
        reject,
      };
      this.messageBoxes.set(clientId, pending);
      this.bridge
        .confirmHostAction({
          kind: "message",
          clientId,
          extensionId: owner.extensionId,
          extensionName:
            this.records.get(owner.extensionId)?.installed.manifest?.name ?? owner.extensionId,
          type: request.type,
          message: request.message,
          detail: request.detail,
          buttons: [...request.buttons],
          defaultId: request.defaultId,
          cancelId: request.cancelId,
        })
        .then(
          (answer) => {
            if (this.messageBoxes.get(clientId) !== pending) return;
            this.messageBoxes.delete(clientId);
            resolve(answer as number);
          },
          (error: unknown) => {
            if (this.messageBoxes.get(clientId) !== pending) return;
            this.messageBoxes.delete(clientId);
            reject(error instanceof Error ? error : new Error(String(error)));
          },
        );
    });
  }

  /**
   * 撤回 frame（指定 `pageId` 时只限该页面）等待回答的消息框，调用以 `ERR_FRAME_GONE` 结束。
   * Withdraws the message boxes of a frame (of one page when `pageId` is given) awaiting answers; the calls fail with
   * `ERR_FRAME_GONE`.
   */
  private dismissMessageBoxes(frameId: string, pageId?: string): void {
    for (const [clientId, pending] of this.messageBoxes) {
      if (pending.frameId !== frameId || (pageId !== undefined && pending.pageId !== pageId))
        continue;
      this.messageBoxes.delete(clientId);
      pending.reject(createExtensionError("ERR_FRAME_GONE", "The frame is gone."));
      this.bridge
        .dismissHostConfirmation(clientId)
        .catch((error: unknown) => console.error("Unable to withdraw a message box.", error));
    }
  }

  private setUnsaved(frameId: string, unsaved: boolean): void {
    if (this.unsavedFrames.has(frameId) === unsaved) return;
    if (unsaved) this.unsavedFrames.add(frameId);
    else this.unsavedFrames.delete(frameId);
    this.publish();
  }

  private setSaveStatus(frameId: string, status: DocumentSaveStatus | null): void {
    if ((this.saveStatuses.get(frameId) ?? null) === status) return;
    if (status === null) this.saveStatuses.delete(frameId);
    else this.saveStatuses.set(frameId, status);
    this.publish();
  }

  private readonly onBeforeUnload = (event: BeforeUnloadEvent) => {
    // 主进程在 will-prevent-unload 中由用户确认。The main process asks the user in will-prevent-unload.
    if (this.unsavedFrames.size > 0) event.preventDefault();
  };

  private async openFile(extensionId: string, uri: string): Promise<void> {
    const location = parseCloudUri(uri);
    const context = this.context.snapshot();
    if (
      location.serverId !== context.server.id ||
      !context.project ||
      location.projectId !== context.project.id ||
      location.path === ""
    ) {
      throw createExtensionError(
        "ERR_INVALID_URI",
        `${uri} is not a file of the current Project on the current Server.`,
      );
    }
    if (!this.fileOpener) {
      throw createExtensionError("ERR_UNAVAILABLE", "The Files view is not available.");
    }
    if (!(await this.fileOpener({ directoryId: location.directoryId, path: location.path }))) {
      throw createExtensionError("ERR_CANCELED", `Opening the file was canceled (${extensionId}).`);
    }
  }

  /** 已完成握手的页面被新文档替换。A connected page was replaced by a new document. */
  private async pageReplaced(frame: HostFrame, pageId: string): Promise<void> {
    if (frame.pageId !== pageId) return;
    frame.pageId = null;
    frame.loaded = false;
    this.dismissMessageBoxes(frame.frameId, pageId);
    this.setUnsaved(frame.frameId, false);
    this.setSaveStatus(frame.frameId, null);
    await this.execution.closePage(frame.frameId, pageId);
    this.broker.disconnect(frame.frameId, pageId);
    if (frame.kind === "background") {
      await this.fail(frame.extensionId, {
        code: "BACKGROUND_RELOADED",
        message: "The background page was reloaded.",
      });
      return;
    }
    this.startHandshakeTimer(frame);
  }

  private heartbeat(): void {
    const now = Date.now();
    for (const frame of this.frames.values()) {
      if (frame.closing || frame.pageId === null || frame.terminated) continue;
      if (now - frame.lastPong > HEARTBEAT_TIMEOUT_MS) {
        frame.terminated = true;
        void this.bridge.terminateExtensionFrameProcess(this.hostInstanceId, frame.frameId);
        continue;
      }
      this.broker.ping(frame.frameId, ++this.pingSequence);
    }
  }

  /* ---------- 状态 State ---------- */

  private async refreshList(): Promise<void> {
    const list = await this.bridge.listExtensions();
    const ids = new Set(list.map((item) => item.id));
    for (const id of this.records.keys()) {
      if (ids.has(id)) continue;
      await this.deactivate(id);
      this.records.delete(id);
    }
    for (const installed of list) {
      const record = this.records.get(installed.id);
      const fixedStatus: ExtensionRuntimeStatus | null =
        installed.validation === "invalid"
          ? "invalid"
          : installed.validation === "incompatible"
            ? "incompatible"
            : null;
      if (!record) {
        this.records.set(installed.id, {
          installed,
          status: fixedStatus ?? "inactive",
          error: null,
          blocked: [],
          activation: null,
          storageUri: null,
          registered: false,
        });
        continue;
      }
      if (fixedStatus !== null && (record.status === "active" || record.status === "activating")) {
        await this.deactivate(installed.id);
      }
      record.installed = installed;
      if (fixedStatus !== null) record.status = fixedStatus;
      else if (record.status === "invalid" || record.status === "incompatible")
        record.status = "inactive";
    }
    this.publish();
  }

  private brokerExtension(extensionId: string): BrokerExtension | null {
    const record = this.records.get(extensionId);
    if (!record || !record.installed.manifest || record.storageUri === null) return null;
    if (
      record.status !== "active" &&
      record.status !== "activating" &&
      record.status !== "deactivating"
    )
      return null;
    return {
      manifest: record.installed.manifest,
      permissions: record.installed.grantedPermissions,
      hostPermissions: parseHostPermissions(record.installed.grantedHostPermissions),
      storageUri: record.storageUri,
    };
  }

  private settingsView(viewId: string): WorkspaceView | undefined {
    if (!viewId.startsWith("settings:")) return undefined;
    const extensionId = viewId.slice("settings:".length);
    const installed = this.records.get(extensionId)?.installed;
    const entry = installed?.manifest?.contributes?.settings?.entry;
    if (!installed?.manifest || !entry) return undefined;
    return {
      id: viewId,
      location: "main",
      title: "Settings",
      icon: { kind: "lucide", name: "Settings" },
      extensionId,
      extensionName: installed.manifest.name,
      entry,
      retainContextWhenHidden: false,
      failure: null,
      priority: "default",
    };
  }

  private workspaceState() {
    const layout = this.layout.getSnapshot();
    return {
      sidebarVisible: layout.sidebarVisible,
      activeSidebarViewId: layout.activeSidebarViewId,
      activeMainViewId: layout.activeMainViewId,
    };
  }

  private publish(): void {
    this.snapshot = {
      started: this.started,
      safeMode: this.safeMode,
      recoveryNotices: this.recoveryNotices,
      viewErrors: Object.fromEntries(this.viewErrors),
      unsavedViewIds: [...this.unsavedFrames].flatMap((frameId) => {
        const viewId = this.frames.get(frameId)?.viewId;
        return viewId ? [viewId] : [];
      }),
      saveStatuses: Object.fromEntries(
        [...this.saveStatuses].flatMap(([frameId, status]) => {
          const viewId = this.frames.get(frameId)?.viewId;
          return viewId ? [[viewId, status]] : [];
        }),
      ),
      extensions: [...this.records.values()]
        .sort((left, right) => left.installed.id.localeCompare(right.installed.id))
        .map((record) => ({
          id: record.installed.id,
          installed: record.installed,
          status: record.status,
          error: record.error,
          blockedRequests: record.blocked,
          frames: [...this.frames.values()]
            .filter((frame) => frame.extensionId === record.installed.id && frame.closing === null)
            .map((frame) => ({
              frameId: frame.frameId,
              extensionId: frame.extensionId,
              kind: frame.kind,
              viewId: frame.viewId,
              status: frame.pageId === null ? "loading" : "ready",
              snapshot: frame.snapshot,
            })),
        })),
    };
    for (const listener of this.listeners) listener();
  }
}

export function settingsViewId(extensionId: string): string {
  return `settings:${extensionId}`;
}

function localViewId(viewId: string): string {
  return viewId.startsWith("settings:") ? "settings" : viewId.slice(viewId.indexOf(":") + 1);
}

function builtInView(
  id: string,
  location: "sidebar" | "main",
  title: string,
  icon: string,
): WorkspaceView {
  if (!isBuiltInViewId(id)) throw new Error(`${id} is not a built-in view id.`);
  return {
    id,
    location,
    title,
    icon: { kind: "lucide", name: icon },
    extensionId: null,
    extensionName: null,
    entry: null,
    retainContextWhenHidden: false,
    failure: null,
    priority: "default",
  };
}
