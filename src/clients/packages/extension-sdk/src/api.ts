// Extension API 类型，只含类型声明；SDK 与宿主用 `import type` 引用，`sdk.d.ts` 由此生成。
// Extension API types (declarations only); the SDK and the host import them with `import type`, and `sdk.d.ts` is generated from them.
import type { ExecutionMessage, InteractionResponse, PermissionMode } from "@agw/execution-core";
import type { ExtensionPermission } from "./permissions";

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

/**
 * 文件查看器：Files 的文件视图显示文件名匹配 `patterns` 的文件时使用。
 * A file viewer, used by the Files view for files whose names match `patterns`.
 */
export interface ExtensionFileViewerContribution {
  id: string;
  title: string;
  icon: string;
  /** 文件基本名的模式，不区分大小写；`*` 匹配任意个字符，`?` 匹配一个字符。Case-insensitive base-name patterns; `*` matches any run, `?` one character. */
  patterns: string[];
  entry: string;
  /**
   * 多个查看器匹配同一文件时的排序：`default`（缺省）排在 `option` 之前；匹配全部文件的通用查看器应声明为 `option`。
   * Ordering when several viewers match a file: `default` (the default) comes before `option`; catch-all viewers
   * should declare `option`.
   */
  priority?: "default" | "option";
}

/**
 * 主题：Appearance 页面的 Theme 下拉框列出启用且合法的 extension 提供的主题。
 * A theme; the Theme select on the Appearance page lists the themes of enabled, valid extensions.
 */
export interface ExtensionThemeContribution {
  id: string;
  title: string;
  /**
   * 包内的 JSON 文件：`{ common?, light?, dark? }`，每项是主题变量名 → 值，`light`、`dark` 中的同名变量覆盖 `common`。
   * A package JSON file `{ common?, light?, dark? }`, each mapping theme variable names to values; `light` and `dark`
   * override `common`.
   */
  path: string;
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
  fileViewers?: ExtensionFileViewerContribution[];
  themes?: ExtensionThemeContribution[];
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
  /**
   * 错误码。文本文档与写入前提的错误码如下。Error code. The codes for text documents and write preconditions:
   * - `ERR_FILE_CHANGED`：文件在读取之后被修改或删除，写入前提不成立。The file changed or was deleted after it was
   *   read, so the write precondition failed.
   * - `ERR_NOT_TEXT`：文件含有 NUL 字节，不能按任何受支持的编码无损读取。The file contains NUL bytes and no supported
   *   encoding reads it losslessly.
   * - `ERR_UNKNOWN_ENCODING`：不能识别文件的编码，或指定的编码不能无损读取文件。The encoding cannot be detected, or
   *   the given encoding cannot read the file losslessly.
   * - `ERR_UNSUPPORTED_ENCODING`：编码不在 `fs.listTextEncodings` 的列表中。The encoding is not in the
   *   `fs.listTextEncodings` list.
   * - `ERR_UNENCODABLE`：文字中有目标编码不能表示的字符，信息给出字符与行号、列号。The text has a character the
   *   target encoding cannot represent; the message names it with its line and column.
   * - `ERR_INVALID_ARGUMENT`：参数不符合要求。An argument is invalid.
   */
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
  /**
   * 宿主当前主题的变量，值为计算后的字符串，引用其他变量的值已经替换。使用这些变量是可选的：需要与宿主一致的 extension 调用
   * `theme.apply()`，然后在样式中使用 `var(--background)`、`var(--primary)`、`var(--radius)`、`var(--font-sans)` 等；不调用的
   * extension 不受主题影响，宿主不向 extension 页面注入任何样式。
   * The variables of the host's current theme as computed strings, with references to other variables substituted.
   * Using them is optional: an extension that wants to match the host calls `theme.apply()` and then uses
   * `var(--background)`, `var(--primary)`, `var(--radius)`, `var(--font-sans)` and so on in its styles; an extension
   * that does not call it is not affected by themes, and the host injects no styles into extension pages.
   *
   * - 界面颜色 Interface colors：`--background`、`--foreground`、`--card`、`--card-foreground`、`--popover`、
   *   `--popover-foreground`、`--primary`、`--primary-foreground`、`--secondary`、`--secondary-foreground`、`--muted`、
   *   `--muted-foreground`、`--accent`、`--accent-foreground`、`--destructive`、`--border`、`--input`、`--ring`、
   *   `--chart-1` … `--chart-5`、`--sidebar`、`--sidebar-foreground`、`--sidebar-primary`、
   *   `--sidebar-primary-foreground`、`--sidebar-accent`、`--sidebar-accent-foreground`、`--sidebar-border`、
   *   `--sidebar-ring`、`--agw-sidebar-muted-foreground`、`--agw-prose-link`。
   * - 聊天与 Markdown 颜色 Chat and Markdown colors：`--agw-user-message`、`--agw-user-message-foreground`、
   *   `--agw-code-block`、`--agw-code-block-foreground`、`--agw-code-block-muted`、`--agw-code-block-border`、
   *   `--agw-code-block-button`、`--agw-code-block-button-hover`、`--agw-prose-code-background`。
   * - 图片预览颜色 Image preview colors：`--agw-image-backdrop`、`--agw-image-toolbar`、
   *   `--agw-image-toolbar-foreground`、`--agw-image-toolbar-border`、`--agw-image-toolbar-hover`、
   *   `--agw-image-toolbar-focus`、`--agw-image-toolbar-shadow`。
   * - 其他宿主颜色 Other host colors：`--agw-status-running`、`--agw-server-online-halo`、`--agw-agentflow-edge`、
   *   `--agw-agentflow-edge-fan-out`、`--agw-agentflow-edge-fan-in`、`--agw-agentflow-edge-switch-case`、
   *   `--agw-agentflow-edge-switch-default`。
   * - Tailwind 基础色阶 Tailwind palette：`--color-{name}-{step}`（slate 400；zinc 50、200、800、900；red 50、100、200、400、
   *   700、800、900、950；orange 50、100、200、400、600、900、950；amber 50–950；yellow 100、400、700、900；green 50、100、200、
   *   300、400、600、700、800、900、950；emerald 50、200、300、500、600、700、800；teal 50、200、800；sky 50、200、600、800；
   *   blue 50–950；indigo 50、200、900；violet 50、100、200、300、700、900、950；rose 50、200、300、500、800），以及
   *   `--color-black`、`--color-white`。
   * - 圆角 Radius：`--radius`，以及由它计算的 `--radius-sm`、`--radius-md`、`--radius-lg`、`--radius-xl`。
   * - 字体 Fonts：`--agw-font-sans`、`--agw-font-mono`，以及引用它们的 `--font-sans`、`--font-mono`。
   * - 字号 Font size：`--agw-font-size-base`，为计算后的 px 值。The computed px value.
   */
  tokens: Record<string, string>;
}

export interface ExtensionApi {
  readonly manifest: ExtensionManifest;
  readonly context: ContextApi;
  readonly workspace: WorkspaceApi;
  readonly fs: FileSystemApi;
  readonly dialog: DialogApi;
  /**
   * 文档会话：组合一个文件的读取、编码、未保存报告、保存、冲突处理与刷新，供文件查看器中的编辑器使用。
   * Document sessions: reading, encoding, unsaved reporting, saving, conflict handling and refreshing of one file,
   * for editors in file viewers.
   */
  readonly documents: {
    openText<Snapshot>(options: {
      file: { uri: string; name: string };
      adapter: TextDocumentAdapter<Snapshot>;
      /** 处理 `Cmd/Ctrl+S`，缺省为 true。Handles `Cmd/Ctrl+S`; true by default. */
      saveShortcut?: boolean;
    }): DocumentSession;
    openBinary<Snapshot>(options: {
      file: { uri: string; name: string };
      adapter: BinaryDocumentAdapter<Snapshot>;
      /** 处理 `Cmd/Ctrl+S`，缺省为 true。Handles `Cmd/Ctrl+S`; true by default. */
      saveShortcut?: boolean;
    }): DocumentSession;
  };
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
    /**
     * 用系统浏览器打开 `http:` 或 `https:` 地址。匹配已批准域名的地址直接打开，其他地址先由用户确认。
     * Opens an `http:` or `https:` URL in the system browser. URLs on approved hosts open directly; others need the user's confirmation.
     */
    openExternal(url: string): Promise<void>;
  };
  readonly theme: {
    get(): ThemeInfo;
    onDidChange(listener: (theme: ThemeInfo) => void): Disposable;
    /**
     * 把主题变量与 dark class 写到 document 上并保持同步，同时加入 `/__agw/typography.css`。元素加上 `agw-prose` 与 `data-density="document"` 或 `"compact"` 即使用排版规范。宿主只推送 base 与 minimum 字号，extension frame 中 `compact` 的字号为 base × 0.875；`document` 密度使用页面设置的 `--agw-font-size-document`，默认值为 base 字号。
     * Applies theme variables and the dark class to the document and keeps them in sync, and also links `/__agw/typography.css`. Elements with `agw-prose` and `data-density="document"` or `"compact"` use the typography. The host pushes only the base and minimum sizes, so in extension frames `compact` uses base × 0.875; the `document` density uses `--agw-font-size-document` set by the page, which defaults to the base size.
     */
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

/** 文件查看器打开的云端文件。The cloud file a file viewer opens. */
export interface FileViewerFile {
  /** `agw-cloud://` 地址。An `agw-cloud://` URI. */
  uri: string;
  name: string;
  projectId: string;
}

/** 文件查看器 `onLoad` 与 `onShow` 的参数。The `onLoad` and `onShow` params of a file viewer. */
export interface FileViewerParams {
  file: FileViewerFile;
}

export interface ViewDefinition {
  /** 文件查看器的 `params` 为 `FileViewerParams`。A file viewer's `params` is `FileViewerParams`. */
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
  /**
   * 在 Files 中选中并在文件视图中打开当前 Project 的云端文件；有未保存修改时先由用户确认。
   * Selects and opens a cloud file of the current Project in the Files view, after confirming unsaved changes.
   */
  openFile(uri: string): Promise<void>;
  /** 文件查看器报告未保存修改。A file viewer reports unsaved changes. */
  setUnsavedChanges(unsaved: boolean): Promise<void>;
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

/** 换行符。A line ending. */
export type Eol = "\n" | "\r\n";

/** `fs.readTextDocument` 读取的文本文件。A text file read by `fs.readTextDocument`. */
export interface TextDocument {
  /** 解码后的文字，换行符保持原样，不含 BOM。The decoded text with its line endings unchanged and without the BOM. */
  text: string;
  /** 编码 id，例如 "utf-8"、"utf-16le"、"gb18030"。An encoding id such as "utf-8", "utf-16le" or "gb18030". */
  encoding: string;
  bom: boolean;
  /** `\r\n` 多于单独的 `\n` 时为 `"\r\n"`。`"\r\n"` when CRLF outnumbers lone LF. */
  eol: Eol;
  /** 文件全部字节的 SHA-256，64 位小写十六进制。The SHA-256 of all bytes of the file, as 64 lowercase hex characters. */
  sha256: string;
}

export interface FileSystemApi {
  kind(uri: string): FileKind;
  readFile(uri: string, options?: { signal?: AbortSignal }): Promise<Uint8Array>;
  readTextFile(uri: string, options?: { signal?: AbortSignal }): Promise<string>;
  writeFile(
    uri: string,
    data: Uint8Array | string,
    options?: {
      overwrite?: boolean;
      createParents?: boolean;
      /**
       * 写入前提：读取时的内容哈希（64 位小写十六进制），只能与 `overwrite: true` 一起使用；文件的内容哈希与它不同时
       * 为 `ERR_FILE_CHANGED`，文件不变。
       * Write precondition: the content hash read earlier (64 lowercase hex characters), only with `overwrite: true`;
       * when the file's hash differs the write fails with `ERR_FILE_CHANGED` and the file is unchanged.
       */
      expectedSha256?: string;
      signal?: AbortSignal;
    },
  ): Promise<void>;
  /**
   * 读取文本文件并识别编码；指定 `encoding` 时按它读取。失败时为 `ERR_NOT_TEXT`、`ERR_UNKNOWN_ENCODING` 或
   * `ERR_UNSUPPORTED_ENCODING`。
   * Reads a text file and detects its encoding, or reads it as `encoding` when given. Fails with `ERR_NOT_TEXT`,
   * `ERR_UNKNOWN_ENCODING` or `ERR_UNSUPPORTED_ENCODING`.
   */
  readTextDocument(
    uri: string,
    options?: { encoding?: string; signal?: AbortSignal },
  ): Promise<TextDocument>;
  /**
   * 把全部换行替换为 `eol` 后按 `encoding` 写入，返回写入内容的哈希。`expectedSha256` 为字符串时覆盖写入并检查写入
   * 前提；为 null 时创建文件，文件已存在为 `EEXIST`。不能表示的字符为 `ERR_UNENCODABLE`。
   * Replaces every line break with `eol`, writes the text as `encoding` and returns the hash of the written content.
   * A string `expectedSha256` overwrites under that precondition; null creates the file and fails with `EEXIST` when
   * it exists. A character the encoding cannot represent fails with `ERR_UNENCODABLE`.
   */
  writeTextDocument(
    uri: string,
    text: string,
    options: {
      encoding: string;
      /** 只有 `utf-8`、`utf-16le`、`utf-16be` 可以带 BOM。Only `utf-8`, `utf-16le` and `utf-16be` can have a BOM. */
      bom: boolean;
      eol: Eol;
      expectedSha256: string | null;
      createParents?: boolean;
      signal?: AbortSignal;
    },
  ): Promise<{ sha256: string }>;
  /** 受支持的编码，`label` 为英文名称。The supported encodings with English labels. */
  listTextEncodings(): Promise<Array<{ id: string; label: string }>>;
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
  /**
   * 宿主绘制的消息框，标题为 extension 名称；返回点击的按钮下标，按 `Esc` 或关闭时返回 `cancelId`（缺省为最后一个
   * 按钮）。`message` 1 到 500 个字符，`detail` 不超过 2000 个字符，`buttons` 1 到 4 个、每个 1 到 40 个字符，
   * `defaultId` 与 `cancelId` 必须是 `buttons` 的下标，否则为 `ERR_INVALID_ARGUMENT`。只有视图可以调用，background
   * 为 `ERR_UNSUPPORTED_OPERATION`。
   * A message box drawn by the host and titled with the extension name; returns the index of the clicked button, or
   * `cancelId` (the last button by default) for `Esc` or closing. `message` has 1 to 500 characters, `detail` at most
   * 2000, `buttons` 1 to 4 entries of 1 to 40 characters each, and `defaultId` and `cancelId` must be indexes into
   * `buttons`, otherwise `ERR_INVALID_ARGUMENT`. Views only; the background gets `ERR_UNSUPPORTED_OPERATION`.
   */
  showMessageBox(options: {
    type?: "info" | "warning" | "error";
    message: string;
    detail?: string;
    buttons: string[];
    /**
     * 获得焦点的按钮下标，缺省时为第一个按钮；超出范围为 `ERR_INVALID_ARGUMENT`。
     * The index of the focused button, the first button by default; out of range is `ERR_INVALID_ARGUMENT`.
     */
    defaultId?: number;
    /**
     * `Esc` 或关闭时返回的下标，缺省为最后一个按钮；超出范围为 `ERR_INVALID_ARGUMENT`。
     * The index returned for `Esc` or closing, the last button by default; out of range is `ERR_INVALID_ARGUMENT`.
     */
    cancelId?: number;
  }): Promise<number>;
}

/* ---------- 文档会话 Document sessions ---------- */

/** 文本编辑器交给文档会话的适配器。The adapter a text editor gives a document session. */
export interface TextDocumentAdapter<Snapshot> {
  /** 用读到的内容替换编辑器内容，并清空撤销历史。Replaces the editor content and clears undo history. */
  load(content: { text: string; eol: Eol }): void;
  /** 当前内容的快照。A snapshot of the current content. */
  snapshot(): Snapshot;
  /**
   * 快照对应的文字。`baseline` 为会话当前的基线快照（最近一次读取或成功写入时的快照）。
   * The text of a snapshot. `baseline` is the session's current baseline snapshot (the snapshot of the last read or
   * successful write).
   */
  serialize(snapshot: Snapshot, baseline: Snapshot): string;
  /** 当前内容是否与快照相同。Whether the current content equals the snapshot. */
  matches(snapshot: Snapshot): boolean;
  /** 每次用户输入后增加的编辑版本。An edit version that increases with every user edit. */
  version(): number;
  /** 编辑器当前的换行符；不提供时使用基线的换行符。The editor's current EOL; the baseline EOL when omitted. */
  eol?(): Eol;
}

/** 二进制格式的编辑器交给文档会话的适配器。The adapter an editor of a binary format gives a document session. */
export interface BinaryDocumentAdapter<Snapshot> {
  /** 用读到的字节替换编辑器内容，并清空撤销历史。Replaces the editor content and clears undo history. */
  load(bytes: Uint8Array): void;
  /** 当前内容的快照。A snapshot of the current content. */
  snapshot(): Snapshot;
  /**
   * 快照对应的字节；写入时字节的 buffer 转移给宿主，之后不能再使用。`baseline` 为会话当前的基线快照（最近一次读取
   * 或成功写入时的快照）。
   * The bytes of a snapshot; writing transfers their buffer to the host, so they cannot be used afterwards.
   * `baseline` is the session's current baseline snapshot (the snapshot of the last read or successful write).
   */
  serialize(snapshot: Snapshot, baseline: Snapshot): Uint8Array;
  /** 当前内容是否与快照相同。Whether the current content equals the snapshot. */
  matches(snapshot: Snapshot): boolean;
  /** 每次用户输入后增加的编辑版本。An edit version that increases with every user edit. */
  version(): number;
}

export type DocumentState = "loading" | "clean" | "dirty" | "saving" | "conflict" | "failed";

/**
 * 一个文件的文档会话。`save`、`show`、`reload`、`reopenWithEncoding`、`saveWithEncoding` 按调用顺序执行；
 * 读取与保存的失败显示在 `state`、`error` 与通知中，返回的 Promise 在操作结束时完成，只有对话框失败（例如 frame
 * 已关闭）时被拒绝。
 * The document session of one file. `save`, `show`, `reload`, `reopenWithEncoding` and `saveWithEncoding` run in
 * call order; read and save failures show in `state`, `error` and notifications, and the returned promises resolve
 * when the operation ends. They reject only when a dialog fails (for example because the frame is gone).
 */
export interface DocumentSession {
  readonly file: { uri: string; name: string };
  readonly state: DocumentState;
  /** 文本文档的编码；二进制文档为 null。The text encoding; null for binary documents. */
  readonly format: { encoding: string; bom: boolean; eol: Eol } | null;
  /** 刷新时发现磁盘内容已变化、但因为有输入没有替换。The disk changed during a refresh that was not applied. */
  readonly changedOnDisk: boolean;
  /** 使会话进入 Failed 的错误。The error that put the session in Failed. */
  readonly error: ExtensionError | null;
  /**
   * 状态、文件（`show` 切换到另一个文件）、编码、`changedOnDisk` 或 `error` 变化后调用。
   * Called after the state, the file (a `show` that switches to another file), format, changedOnDisk or error changes.
   */
  onDidChange(listener: () => void): Disposable;
  /** 编辑器每次内容变化后调用。Called after every content change. */
  edited(): void;
  save(): Promise<void>;
  /** 在 view.onShow 中调用。Called from view.onShow. */
  show(file: { uri: string; name: string }): Promise<void>;
  /** 有未保存修改时先确认放弃。Confirms discarding unsaved changes first. */
  reload(): Promise<void>;
  /**
   * 按指定编码重新读取；有未保存修改时先确认放弃。二进制文档为 `ERR_UNSUPPORTED_OPERATION`。
   * Reads again as `encoding`, confirming discarding unsaved changes first. `ERR_UNSUPPORTED_OPERATION` for binary
   * documents.
   */
  reopenWithEncoding(encoding: string): Promise<void>;
  /**
   * 按指定编码与 BOM 写入，成功后基线的编码随之改变。二进制文档为 `ERR_UNSUPPORTED_OPERATION`。
   * Writes as `encoding` and `bom`; the baseline takes the new encoding. `ERR_UNSUPPORTED_OPERATION` for binary
   * documents.
   */
  saveWithEncoding(encoding: string, bom: boolean): Promise<void>;
  /**
   * 移除保存快捷键，不再处理未完成的结果；未完成的操作随之完成，之后的操作调用被拒绝。
   * Removes the save shortcut and stops handling pending results; pending operations resolve and later calls reject.
   */
  dispose(): void;
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

export type ExtensionFrameKind =
  | "background"
  | "sidebarView"
  | "mainView"
  | "fileViewer"
  | "settings";

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
