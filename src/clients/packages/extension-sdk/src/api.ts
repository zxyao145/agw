// Extension API 类型，只含类型声明；SDK 与宿主用 `import type` 引用，`sdk.d.ts` 由此生成。
// Extension API types (declarations only); the SDK and the host import them with `import type`, and `sdk.d.ts` is generated from them.
import type { ExecutionMessage, InteractionResponse, PermissionMode } from "@agw/execution-core";
import type { ExtensionPermission } from "./extension-permissions";

export type { ExecutionMessage, InteractionResponse, PermissionMode, ExtensionPermission };

/* ---------- manifest ---------- */

export type ExtensionAction =
  | { type: "openMainView"; view: string }
  | { type: "toggleSidebarView"; view: string }
  | { type: "command"; command: string };

export interface ExtensionCommandContribution {
  id: string;
  title: string;
}

export interface ExtensionActionContribution {
  id: string;
  title: string;
  /** lucide-react 导出名或包内 `.svg` / `.png`。A lucide-react export name or a package `.svg` / `.png`. */
  icon: string;
  action: ExtensionAction;
}

export interface ExtensionSidebarViewContribution {
  id: string;
  title: string;
  icon: string;
  entry: string;
}

export interface ExtensionMainViewContribution {
  id: string;
  title: string;
  icon: string;
  entry: string;
  retainContextWhenHidden?: boolean;
}

export interface ExtensionContributes {
  commands?: ExtensionCommandContribution[];
  ribbon?: ExtensionActionContribution[];
  sidebarViews?: ExtensionSidebarViewContribution[];
  mainViews?: ExtensionMainViewContribution[];
  settings?: { entry: string };
  chat?: {
    composerActions?: ExtensionActionContribution[];
    messageActions?: ExtensionActionContribution[];
    conversationActions?: ExtensionActionContribution[];
  };
  explorerActions?: ExtensionActionContribution[];
}

export interface ExtensionManifest {
  $schema?: string;
  manifestVersion: number;
  id: string;
  name: string;
  version: string;
  minAppVersion?: string;
  description?: string;
  author?: string;
  authorUrl?: string;
  icon?: string;
  background?: string;
  contributes?: ExtensionContributes;
  permissions?: ExtensionPermission[];
  hostPermissions?: string[];
}

/* ---------- 通用 Common ---------- */

export interface Disposable {
  dispose(): void;
}

/** Extension API 抛出的错误。Errors thrown by the Extension API. */
export interface ExtensionError extends Error {
  name: "ExtensionError";
  code: string | null;
  syscall: string | null;
  path: string | null;
}

/** 跨端口传递的错误数据。Error data carried across the port. */
export interface ExtensionErrorData {
  name: string;
  message: string;
  code: string | null;
  syscall: string | null;
  path: string | null;
}

export interface ThemeInfo {
  kind: "light" | "dark";
  /** tokens.css 中的颜色与圆角变量。Color and radius variables from tokens.css. */
  tokens: Record<string, string>;
}

export interface ExtensionApi {
  readonly manifest: ExtensionManifest;
  readonly context: ContextApi;
  readonly workspace: WorkspaceApi;
  readonly fs: FileSystemApi;
  readonly dialog: DialogApi;
  readonly shell: {
    /** 只支持本地文件。Local files only. */
    revealInFileManager(uri: string): Promise<void>;
  };
  readonly execution: ExecutionApi;
  readonly a2a: A2AApi;
  readonly http: HttpApi;
  readonly chat: {
    composer: { insertText(text: string): Promise<void> };
  };
  readonly ui: {
    notify(notification: { type: "info" | "success" | "error"; message: string }): void;
    /** URL 必须匹配已批准的域名。The URL must match an approved host permission. */
    openExternal(url: string): Promise<void>;
  };
  readonly theme: {
    get(): ThemeInfo;
    onDidChange(listener: (theme: ThemeInfo) => void): Disposable;
    /** 把主题变量与 dark class 写到 document 上并保持同步。Applies theme variables and the dark class to the document and keeps them in sync. */
    apply(): Disposable;
  };
}

export interface BackgroundDefinition {
  onInstalled?(
    details: { reason: "install" | "update"; previousVersion: string | null },
    api: ExtensionApi,
  ): void | Promise<void>;
  activate?(api: ExtensionApi): void | Promise<void>;
  deactivate?(): void | Promise<void>;
  commands?: Record<string, (args: unknown, api: ExtensionApi) => void | Promise<void>>;
}

export interface ViewDefinition {
  onLoad?(api: ExtensionApi, params: unknown): void | Promise<void>;
  onShow?(params: unknown): void;
  onHide?(): void;
  onUnload?(): void | Promise<void>;
}

/* ---------- Context 与工作区 Context and workspace ---------- */

export interface HostContext {
  app: { version: string; platform: "darwin" | "win32" | "linux"; architecture: string };
  extension: { id: string; version: string; manifestVersion: number; storageUri: string };
  server: { id: string; name: string; kind: "local" | "remote" };
  /** 需要 context.user。Requires context.user. */
  user?: ContextUser | null;
  /** 需要 context.project。Requires context.project. */
  project?: ContextProject | null;
  /** 需要 context.project。Requires context.project. */
  conversation?: { id: string } | null;
  /** 需要 context.agent。Requires context.agent. */
  target?: ContextTarget | null;
  workspace: WorkspaceState;
}

export interface ContextUser {
  id: string;
  displayName: string | null;
  loginProvider: string | null;
  isAdmin: boolean;
}

export interface ContextProject {
  id: string;
  name: string;
  description: string | null;
  /** 主目录与附加目录。The primary and additional directories. */
  directories: { directoryId: string | null; label: string; uri: string }[];
}

export interface ContextTarget {
  type: "agent" | "agentflow";
  id: string;
  name: string;
  displayName: string | null;
  description: string | null;
  externalAgentKind: "maf" | "claudeCode" | "codex" | "pi" | null;
}

export interface ContextApi {
  /** 同步返回最近一次快照。Returns the latest snapshot synchronously. */
  get(): HostContext;
  onDidChange(listener: (context: HostContext) => void): Disposable;
  /** 需要 context.project。Requires context.project. */
  listProjects(): Promise<ContextProject[]>;
  /** 需要 context.agent。Requires context.agent. */
  listTargets(): Promise<ContextTarget[]>;
}

export interface WorkspaceState {
  sidebarVisible: boolean;
  activeSidebarViewId: string;
  activeMainViewId: string;
}

export interface WorkspaceApi {
  showSidebarView(id: string): Promise<void>;
  toggleSidebarView(id: string): Promise<void>;
  setSidebarVisible(visible: boolean): Promise<void>;
  openMainView(id: string, params?: unknown): Promise<void>;
}

/* ---------- 文件系统 File system ---------- */

export type FileKind = "local" | "cloud";
export type FileType = "file" | "directory" | "symlink";

export interface FileStat {
  type: FileType;
  size: number | null;
  /** RFC 3339。 */
  modifiedTime: string | null;
}

export interface DirectoryEntry {
  name: string;
  uri: string;
  type: FileType;
}

export interface FileChangeEvent {
  type: "created" | "changed" | "deleted";
  uri: string;
}

export interface FileSystemApi {
  kind(uri: string): FileKind;
  readFile(uri: string, options?: { signal?: AbortSignal }): Promise<Uint8Array>;
  readTextFile(uri: string, options?: { signal?: AbortSignal }): Promise<string>;
  writeFile(
    uri: string,
    data: Uint8Array | string,
    options?: { overwrite?: boolean; createParents?: boolean; signal?: AbortSignal },
  ): Promise<void>;
  readDirectory(uri: string): Promise<DirectoryEntry[]>;
  stat(uri: string): Promise<FileStat>;
  exists(uri: string): Promise<boolean>;
  createDirectory(uri: string, options?: { recursive?: boolean }): Promise<void>;
  delete(uri: string, options?: { recursive?: boolean }): Promise<void>;
  rename(from: string, to: string, options?: { overwrite?: boolean }): Promise<void>;
  /** 只支持本地文件。Local files only. */
  watch(
    uri: string,
    options: { recursive?: boolean },
    listener: (event: FileChangeEvent) => void,
  ): Promise<Disposable>;
}

export interface DialogFilter {
  name: string;
  extensions: string[];
}

export interface DialogApi {
  /** 返回本地文件 URI；取消时为空数组。Returns local file URIs, or an empty array when canceled. */
  showOpenDialog(options: {
    title?: string;
    directory?: boolean;
    multiple?: boolean;
    filters?: DialogFilter[];
  }): Promise<string[]>;
  /** 取消时为 null。Null when canceled. */
  showSaveDialog(options: {
    title?: string;
    defaultName?: string;
    filters?: DialogFilter[];
  }): Promise<string | null>;
}

/* ---------- 执行 Execution ---------- */

export type ExecutionCloseReason =
  | "disposed"
  | "frame-unloaded"
  | "deactivated"
  | "connection-changed"
  | "connection-lost";

export interface ExecutionApi {
  /** 需要 execution。Requires execution. */
  createSession(options: {
    projectId: string;
    /** 省略时新建对话。Omit to start a new conversation. */
    conversationId?: string;
    environmentVariables?: Record<string, string>;
    permissionMode?: PermissionMode;
  }): Promise<ExecutionSessionApi>;
}

export interface ExecutionSessionApi {
  readonly conversationId: string;
  send(input: {
    target: { type: "agent" | "agentflow"; id: string };
    text: string;
  }): Promise<{ turnId: string }>;
  interrupt(): Promise<void>;
  respond(response: InteractionResponse): Promise<void>;
  setPermissionMode(mode: PermissionMode): Promise<void>;
  onMessage(listener: (message: ExecutionMessage) => void): Disposable;
  onClose(listener: (reason: ExecutionCloseReason) => void): Disposable;
  dispose(): Promise<void>;
}

/* ---------- A2A 与外部 HTTP A2A and external HTTP ---------- */

export type A2AStreamMethod = "SendStreamingMessage" | "SubscribeToTask";

export interface A2AApi {
  listAgents(options?: { signal?: AbortSignal }): Promise<unknown[]>;
  getAgentCard(agentName: string, options?: { signal?: AbortSignal }): Promise<unknown>;
  request(
    agentName: string,
    request: { method: string; params?: unknown },
    options?: { signal?: AbortSignal },
  ): Promise<unknown>;
  stream(
    agentName: string,
    request: { method: A2AStreamMethod; params?: unknown },
    listener: (event: unknown) => void,
    options?: { signal?: AbortSignal },
  ): Promise<void>;
}

export interface HttpApi {
  request(request: {
    url: string;
    method?: string;
    headers?: Record<string, string>;
    body?: string | Uint8Array;
    signal?: AbortSignal;
  }): Promise<HttpResponse>;
}

export interface HttpResponse {
  status: number;
  headers: Record<string, string>;
  body: Uint8Array;
  text(): string;
  json<T>(): T;
}

/* ---------- bridge 协议 Bridge protocol ---------- */

export type ExtensionFrameKind = "background" | "sidebarView" | "mainView" | "settings";

/** `welcome` 事件的数据。Data of the `welcome` event. */
export interface ExtensionWelcome {
  frame: {
    frameId: string;
    kind: ExtensionFrameKind;
    viewId: string | null;
    params: unknown;
  };
  manifest: ExtensionManifest;
  context: HostContext;
  theme: ThemeInfo;
}
