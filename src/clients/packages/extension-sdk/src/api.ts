// Extension API 类型，只含类型声明；SDK 与宿主用 `import type` 引用，`sdk.d.ts` 由此生成。
// Extension API types (declarations only); the SDK and the host import them with `import type`, and `sdk.d.ts` is generated from them.
import type {
  ChatChannel,
  ChatEvent,
  ChatEventError,
  ChatEventFilter,
  ChatEventKind,
  ChatEventOrigin,
  ChatExecutionRequest,
  ChatHttpRequest,
  ChatMessageEvent,
  ChatMessageSource,
  ChatRequestEvent,
  ChatResponseEvent,
  ChatScope,
  ExecutionHubMethod,
  ExecutionMessage,
  InteractionResponse,
  PermissionMode,
} from "@agw/execution-core";
import type { ExtensionPermission } from "./permissions";

export type { ExecutionMessage, InteractionResponse, PermissionMode, ExtensionPermission };
export type {
  ChatChannel,
  ChatEvent,
  ChatEventError,
  ChatEventFilter,
  ChatEventKind,
  ChatEventOrigin,
  ChatExecutionRequest,
  ChatHttpRequest,
  ChatMessageEvent,
  ChatMessageSource,
  ChatRequestEvent,
  ChatResponseEvent,
  ChatScope,
  ExecutionHubMethod,
};

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
   * 包内的 JSON 或 CSS 文件。JSON 文件为 `{ common?, light?, dark? }`，每项是主题变量名 → 值，`light`、`dark` 中的同名变量
   * 覆盖 `common`。CSS 文件是标准 CSS，可以定义任意自定义变量、组件样式与布局，用 `:root:not(.dark)`、`:root.dark` 区分
   * 深浅色，可以 `@import` 包内 CSS，并引用包内图片（PNG、JPEG、GIF、WebP、AVIF、SVG、ICO）与字体（WOFF、WOFF2、TTF、
   * OTF）或 data URL；外部资源不合法。单个 CSS 文件最多 1 MiB，单个资源最多 4 MiB，输入合计最多 8 MiB，处理后的文本最多
   * 16 MiB。
   * A package JSON or CSS file. JSON files are `{ common?, light?, dark? }`, each mapping theme variable names to
   * values; `light` and `dark` override `common`. CSS files are standard CSS that may define any custom variables,
   * component styles and layout, tell modes apart with `:root:not(.dark)` and `:root.dark`, `@import` package CSS, and
   * refer to package images (PNG, JPEG, GIF, WebP, AVIF, SVG, ICO), fonts (WOFF, WOFF2, TTF, OTF) or data URLs; external
   * resources are invalid. One CSS file is at most 1 MiB, one resource at most 4 MiB, the inputs at most 8 MiB in
   * total, and the processed text at most 16 MiB.
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
  /**
   * 宿主当前主题的 id：Default 为 `"default"`，extension 提供的主题为 `"{extensionId}:{themeId}"`。extension 可以按 id
   * 为不同的主题使用自己的样式。
   * The id of the host's current theme: `"default"` for Default, `"{extensionId}:{themeId}"` for a theme an extension
   * provides. An extension can use its own styles for particular themes by id.
   */
  id: string;
  /**
   * 宿主当前主题的名称：Default 为 `"Default"`，extension 提供的主题为 manifest 中 `contributes.themes` 的 `title`。
   * The name of the host's current theme: `"Default"` for Default, the `title` in the manifest's `contributes.themes`
   * for a theme an extension provides.
   */
  title: string;
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
   * - 工作区背景 Workspace backdrop：`--agw-workspace-backdrop`，为 `none` 或用逗号分隔的 CSS 渐变，可直接作为
   *   `background-image` 的值；宿主把它画在 Main Workspace 上，extension 自行决定是否在自己的页面中使用。The value is
   *   `none` or comma-separated CSS gradients, usable as a `background-image` value; the host draws it on the Main
   *   Workspace, and each extension decides whether its own pages use it.
   * - 字号 Font size：`--agw-font-size-base`，为计算后的 px 值。The computed px value.
   *
   * CSS 主题下，这里仍是宿主页面中这些公共变量的计算值；`theme.apply()` 之后，extension 页面中的值由默认变量样式表与主题 CSS
   * 在自己的 document 中计算。
   * Under a CSS theme these are still the computed values of the public variables in the host page; after
   * `theme.apply()`, the extension page computes its own values from the default variable stylesheet and the theme CSS.
   */
  tokens: Record<string, string>;
  /**
   * CSS 主题的完整样式文本：读取主题 CSS 入口、展开包内 `@import` 并内嵌图片与字体后的结果。JSON 主题与 Default 没有这个字段。
   * `theme.apply()` 把它放进 extension 自己的 document；主题可以用 `:root[data-agw-document="extension"]`、
   * `:root[data-agw-extension="<extensionId>"]` 限定作用于 extension 页面的规则，用 `:root.dark`、`:root:not(.dark)`
   * 区分深浅色。
   * The complete style text of a CSS theme: the theme CSS entry with package `@import` rules expanded and images and
   * fonts inlined. JSON themes and Default have no such field. `theme.apply()` puts it into the extension's own
   * document; themes can scope rules for extension pages with `:root[data-agw-document="extension"]` and
   * `:root[data-agw-extension="<extensionId>"]`, and tell modes apart with `:root.dark` and `:root:not(.dark)`.
   */
  cssText?: string;
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
    /**
     * 订阅当前 Server 上全部对话的通信事件；需要 chat.observe。宿主接受订阅后返回，`listener` 可能在返回之前收到事件。
     * Subscribes to the traffic events of every conversation on the current Server; requires chat.observe.
     * Resolves once the host accepts the subscription; `listener` may receive events before that.
     */
    observe(filter: ChatEventFilter, listener: (event: ChatEvent) => void): Promise<Disposable>;
  };
  readonly ui: {
    notify(notification: { type: "info" | "success" | "error"; message: string }): void;
    /**
     * 用系统浏览器打开 `http:` 或 `https:` 地址。匹配已批准域名的地址直接打开，其他地址先由用户确认。
     * Opens an `http:` or `https:` URL in the system browser. URLs on approved hosts open directly; others need the user's confirmation.
     */
    openExternal(url: string): Promise<void>;
    /**
     * 在 Desktop 窗口上显示图片预览，与聊天中的图片预览相同：滚轮与按钮缩放，拖动平移，右键菜单复制或另存图片，点击背景
     * 或 `Esc` 关闭。用户关闭时 resolve；同一时间只有一个预览，新的预览替换当前预览，被替换的调用 resolve；发出调用的页面
     * 关闭或被替换时预览关闭，调用为 `ERR_FRAME_GONE`。只有视图可以调用，background 为 `ERR_UNSUPPORTED_OPERATION`；
     * 图片不符合 `PreviewImage` 的要求时为 `ERR_INVALID_ARGUMENT`。
     * Shows an image preview over the Desktop window, the same as the chat's image preview: wheel and button zoom, drag
     * to pan, a context menu that copies or saves the image, and closing by clicking the backdrop or `Esc`. Resolves when
     * the user closes it. One preview shows at a time: a new preview replaces the current one, whose call resolves; when
     * the calling page closes or is replaced the preview closes and the call fails with `ERR_FRAME_GONE`. Views only;
     * the background gets `ERR_UNSUPPORTED_OPERATION`. An image not meeting `PreviewImage` fails with
     * `ERR_INVALID_ARGUMENT`.
     */
    previewImage(image: PreviewImage): Promise<void>;
  };
  readonly theme: {
    /** 当前主题的完整状态，CSS 主题包含 `cssText`。The complete current theme; CSS themes include `cssText`. */
    get(): ThemeInfo;
    /** 主题变化时收到完整状态。Receives the complete theme when it changes. */
    onDidChange(listener: (theme: ThemeInfo) => void): Disposable;
    /**
     * 在当前 document 中应用主题并保持同步：设置 dark class，加入 `/__agw/typography.css`，在根元素上设置
     * `data-agw-document="extension"` 与 `data-agw-extension`。JSON 主题与 Default 把变量写到根元素的 inline style；CSS
     * 主题使用 constructed stylesheet，默认变量样式表（`/__agw/theme-defaults.css`）位于主题样式表之前，二者排在页面的
     * `<style>`、`<link>` 之后并保留其他代码的 adopted stylesheet，只有 `--agw-font-size-base` 写为 inline 属性。元素加上
     * `agw-prose` 与 `data-density="document"` 或 `"compact"` 即使用排版规范；extension frame 中 `compact` 的字号为
     * base × 0.875，`document` 密度使用页面设置的 `--agw-font-size-document`，默认值为 base 字号。返回的 `Disposable` 只停止
     * 同步，当前外观保留。
     * Applies the theme to this document and keeps it in sync: sets the dark class, links `/__agw/typography.css` and
     * sets `data-agw-document="extension"` and `data-agw-extension` on the root element. JSON themes and Default write
     * variables to the root's inline style; CSS themes use constructed stylesheets, with the default variable
     * stylesheet (`/__agw/theme-defaults.css`) before the theme stylesheet, both after the page's `<style>` and
     * `<link>` sheets and keeping adopted stylesheets of other code, and only `--agw-font-size-base` is written as an
     * inline property. Elements with `agw-prose` and `data-density="document"` or `"compact"` use the typography; in
     * extension frames `compact` uses base × 0.875, and the `document` density uses `--agw-font-size-document` set by
     * the page, which defaults to the base size. The returned `Disposable` only stops syncing; the current appearance
     * stays.
     */
    apply(): Disposable;
  };
}

/** `ui.previewImage` 支持的图片类型。The image types `ui.previewImage` supports. */
export type PreviewImageType =
  | "image/png"
  | "image/jpeg"
  | "image/gif"
  | "image/webp"
  | "image/avif"
  | "image/bmp"
  | "image/svg+xml";

/** `ui.previewImage` 显示的图片。The image `ui.previewImage` shows. */
export interface PreviewImage {
  /**
   * 图片文件的内容，1 字节到 32 MiB；它的 ArrayBuffer 随调用转移给宿主。
   * The image file's bytes, from 1 byte to 32 MiB; its ArrayBuffer is transferred to the host by the call.
   */
  data: Uint8Array;
  type: PreviewImageType;
  /**
   * 1 到 255 个字符，用作替代文字与 "Save image as…" 的默认文件名。
   * 1 to 255 characters, used as the alt text and the default file name of "Save image as…".
   */
  name: string;
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
  /**
   * 文件查看器在宿主文件标题栏中的条目，替换之前设置的全部条目，空数组清除。只有文件查看器可以调用，其他 frame 为
   * `ERR_UNSUPPORTED_OPERATION`；条目不合法时为 `ERR_INVALID_ARGUMENT`。页面关闭或重新加载后条目清除。
   * A file viewer's items in the host's file header; replaces every item set before, and an empty array clears them.
   * File viewers only; other frames get `ERR_UNSUPPORTED_OPERATION`, and invalid items `ERR_INVALID_ARGUMENT`. The
   * items are cleared when the page closes or reloads.
   */
  setFileHeaderItems(items: FileHeaderItem[]): Promise<void>;
  /** 用户选择了条目或条目菜单中的一项。The user chose an item or an entry of its menu. */
  onDidSelectFileHeaderItem(listener: (selection: FileHeaderItemSelection) => void): Disposable;
}

/**
 * 文件查看器在宿主文件标题栏 More 菜单中的条目（`workspace.setFileHeaderItems`）。最多 16 个，菜单项合计最多 500 个。
 * A file viewer's item in the More menu of the host's file header (`workspace.setFileHeaderItems`). At most 16 items
 * and 500 menu entries in total.
 */
export interface FileHeaderItem {
  /** 1 到 64 个字符，在全部条目中唯一。1 to 64 characters, unique among all items. */
  id: string;
  /**
   * 在 More 菜单中的位置。`top`：查看器说明与 "Reload view" 之后；`bottom`：默认查看器设置之后，菜单的最后。
   * Where the item goes in the More menu. `top`: after the viewer info and "Reload view"; `bottom`: after the default
   * viewer setting, at the end of the menu.
   */
  placement: "top" | "bottom";
  /** 1 到 80 个字符。1 to 80 characters. */
  label: string;
  /** true 时显示为不可用，不能选择。When true, the item shows as unavailable and cannot be chosen. */
  disabled?: boolean;
  /**
   * 提供时条目为勾选项，true 时显示勾号；不能与 `menu` 同时提供。
   * When given, the item is a checkbox item showing a check mark for true; not allowed together with `menu`.
   */
  checked?: boolean;
  /**
   * 条目的菜单，1 到 100 项；菜单项可以再带一层子菜单。省略时选择条目本身。
   * The item's menu of 1 to 100 entries, whose entries may hold one more level. Omitted: the item itself is chosen.
   */
  menu?: FileHeaderMenuEntry[];
}

/** 条目菜单中的一项；带 `menu` 时为子菜单，本身不能被选择。An entry of an item's menu; with `menu` it is a submenu. */
export interface FileHeaderMenuEntry {
  /** 1 到 64 个字符，在同一条目的全部菜单项中唯一。1 to 64 characters, unique among the item's entries. */
  id: string;
  /** 1 到 80 个字符。1 to 80 characters. */
  label: string;
  /** true 时显示为不可用，不能选择。When true, the entry shows as unavailable and cannot be chosen. */
  disabled?: boolean;
  /**
   * 提供时为勾选项，true 时显示勾号；不能与 `menu` 同时提供。
   * When given, the entry is a checkbox item showing a check mark for true; not allowed together with `menu`.
   */
  checked?: boolean;
  /** 子菜单，1 到 100 项。A submenu of 1 to 100 entries. */
  menu?: FileHeaderMenuEntry[];
}

/** 用户的选择。The user's choice. */
export interface FileHeaderItemSelection {
  itemId: string;
  /** 选择的菜单项；条目没有菜单时为 null。The chosen entry; null for an item without a menu. */
  entryId: string | null;
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
 * 文档会话报告给宿主、显示在文件名后的保存状态；读取中与失败时不报告状态（null）。
 * The save status a document session reports to the host, shown after the file name; null while loading or failed.
 */
export type DocumentSaveStatus = "saved" | "unsaved" | "saving" | "changed-on-disk";

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
  /**
   * 需要 execution。extension 只能在自己创建的对话、开放对话（宿主创建、从未清空、没有已受理的 Turn）与新对话中写入；其他对话的会话只读，见 `ExecutionSessionApi.readOnly`。
   * Requires execution. An extension may write only in conversations it created, open conversations (host-created, never cleared and without accepted turns) and new conversations; sessions on other conversations are read-only, see `ExecutionSessionApi.readOnly`.
   */
  createSession(options: {
    /** 小写 UUID，否则以 `ERR_INVALID_ARGUMENT` 失败。A lowercase UUID; otherwise fails with `ERR_INVALID_ARGUMENT`. */
    projectId: string;
    /** 省略时新建对话；提供时必须是小写 UUID。Omit to start a new conversation; when given it must be a lowercase UUID. */
    conversationId?: string;
    environmentVariables?: Record<string, string>;
    permissionMode?: PermissionMode;
  }): Promise<ExecutionSessionApi>;
}

export interface ExecutionSessionApi {
  readonly conversationId: string;
  /**
   * 创建时确定：对话已经保存、不是开放对话、并且由其他一方（Desktop、Web、Mobile、Job、A2A 或其他 extension）创建时为 true。
   * 只读会话的 `send`、`interrupt`、`respond`、`setPermissionMode` 以 `ERR_CONVERSATION_READ_ONLY` 失败；最终以 Server 的判定为准。
   * Decided at creation: true when the conversation is saved, not open and created by another party (Desktop, Web, Mobile, a
   * Job, A2A or another extension). On a read-only session `send`, `interrupt`, `respond` and `setPermissionMode` fail with
   * `ERR_CONVERSATION_READ_ONLY`; the Server's decision is final.
   */
  readonly readOnly: boolean;
  /** 只读对话上以 `ERR_CONVERSATION_READ_ONLY` 失败。Fails with `ERR_CONVERSATION_READ_ONLY` on a read-only conversation. */
  send(input: {
    target: { type: "agent" | "agentflow"; id: string };
    text: string;
  }): Promise<{ turnId: string }>;
  /** 只读对话上以 `ERR_CONVERSATION_READ_ONLY` 失败。Fails with `ERR_CONVERSATION_READ_ONLY` on a read-only conversation. */
  interrupt(): Promise<void>;
  /** 只读对话上以 `ERR_CONVERSATION_READ_ONLY` 失败。Fails with `ERR_CONVERSATION_READ_ONLY` on a read-only conversation. */
  respond(response: InteractionResponse): Promise<void>;
  /** 只读对话上以 `ERR_CONVERSATION_READ_ONLY` 失败。Fails with `ERR_CONVERSATION_READ_ONLY` on a read-only conversation. */
  setPermissionMode(mode: PermissionMode): Promise<void>;
  /**
   * 会话自己的消息，以及这台 Desktop 从其他方（宿主与其他 extension 的会话）收到的该对话实时消息；不包括其他设备上运行的 Turn 与历史消息。
   * The session's own messages and the conversation's live messages this Desktop receives from other parties (host and
   * other extensions' sessions); turns running on other devices and history messages are not included.
   */
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
  /** 完整的主题快照，CSS 主题包含 `cssText`。The complete theme snapshot; CSS themes include `cssText`. */
  theme: ThemeInfo;
  /** CSS 主题样式文本的标识；JSON 主题与 Default 为 null。The CSS theme style text id; null for JSON themes and Default. */
  themeStyleId: string | null;
}

/**
 * `theme.changed` 的样式操作：`unchanged` 沿用这个连接已经收到的文本，`replace` 携带新的完整文本，`clear` 删除已有 CSS。
 * The style operation of `theme.changed`: `unchanged` keeps the text this connection already received, `replace`
 * carries the new complete text, and `clear` removes the existing CSS.
 */
export type ThemeStyleUpdate =
  | { operation: "unchanged"; styleId: string | null }
  | { operation: "replace"; styleId: string; cssText: string }
  | { operation: "clear"; styleId: null };

/** `theme.changed` 事件的数据。Data of the `theme.changed` event. */
export interface ThemeChangedNotification {
  theme: Omit<ThemeInfo, "cssText">;
  style: ThemeStyleUpdate;
}
