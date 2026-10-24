import type {
  DirectoryEntry,
  ExtensionErrorData,
  ExtensionFrameKind,
  ExtensionManifest,
  ExtensionPermission,
  FileChangeEvent,
  FileStat,
} from "@agw/extension-sdk";

export type ExtensionValidation = "valid" | "invalid" | "incompatible";

/** manifest 校验或安装的错误。A manifest validation or installation error. */
export type ExtensionValidationError = {
  code: string;
  message: string;
  /** 字段路径，例如 `contributes.ribbon[0].action.view`。Field path such as `contributes.ribbon[0].action.view`. */
  path: string | null;
};

/** `settings.json` 中 `extensions` 的一项。One entry of `extensions` in `settings.json`. */
export type ExtensionSettings = {
  enabled: boolean;
  grantedPermissions: ExtensionPermission[];
  grantedHostPermissions: string[];
  lastActivatedVersion: string | null;
};

/**
 * 校验后的主题。JSON 主题：`common` 用于浅色与深色，`light`、`dark` 中的同名变量覆盖它。CSS 主题只带描述，完整样式文本由
 * `getThemeStyle` 按 `styleId` 获取。
 * A validated theme. JSON themes: `common` applies to light and dark, and same-named variables in `light` and `dark`
 * override it. CSS themes carry only a descriptor; `getThemeStyle` fetches the complete style text by `styleId`.
 */
export type ExtensionTheme =
  | {
      format: "json";
      id: string;
      title: string;
      common: Record<string, string>;
      light: Record<string, string>;
      dark: Record<string, string>;
    }
  | {
      format: "css";
      id: string;
      title: string;
      /** 主题样式文本 UTF-8 内容的 SHA-256。The SHA-256 of the theme style text's UTF-8 content. */
      styleId: string;
      /** 主题样式文本的 UTF-8 字节数。The UTF-8 byte length of the theme style text. */
      byteLength: number;
    };

/** `getThemeStyle` 的请求：`themeId` 为 `"{extensionId}:{themeId}"`。A `getThemeStyle` request; `themeId` is `"{extensionId}:{themeId}"`. */
export type ThemeStyleRequest = { themeId: string; styleId: string };

/** 已校验的主题样式文本。A validated theme style text. */
export type ThemeStyleText = { styleId: string; cssText: string };

/** 内置 extension 来自应用资源目录，用户 extension 来自 `<userData>/extensions/packages`。Bundled from the app resources, user from `<userData>/extensions/packages`. */
export type ExtensionSource = "bundled" | "user";

export type InstalledExtension = {
  id: string;
  source: ExtensionSource;
  directory: string;
  manifest: ExtensionManifest | null;
  validation: ExtensionValidation;
  errors: ExtensionValidationError[];
  enabled: boolean;
  permissionApprovalRequired: boolean;
  grantedPermissions: ExtensionPermission[];
  grantedHostPermissions: string[];
  lastActivatedVersion: string | null;
  /** manifest 中作为图标声明的包路径 → data URL。Package icon path declared in the manifest → data URL. */
  icons: Record<string, string>;
  /** `contributes.themes` 校验后的主题，extension 不合法时为空。The validated `contributes.themes`; empty when the extension is not valid. */
  themes: ExtensionTheme[];
};

export type ExtensionGrants = {
  permissions: ExtensionPermission[];
  hostPermissions: string[];
};

export type ExtensionInstallSource = "directory" | "archive";

export type ExtensionInstallPreview =
  | { canceled: true }
  | {
      canceled: false;
      installId: string;
      manifest: ExtensionManifest | null;
      errors: ExtensionValidationError[];
      installedVersion: string | null;
    };

/** 启动恢复的结果，Extensions 页面在本次运行中显示。Startup recovery outcome shown on the Extensions page during this run. */
export type ExtensionRecoveryNotice =
  | { extensionId: string; operation: "install"; outcome: "restored-previous" | "completed" }
  | { extensionId: string; operation: "uninstall"; outcome: "completed" };

export type UninstallExtensionOptions = {
  deleteData: boolean;
};

export type ExtensionActivation = {
  storageDirectory: string;
};

export type SaveExtensionSdkTypesResult =
  | { canceled: true }
  | { canceled: false; directory: string };

export type ExtensionSafeModeState = {
  reason: "crashed" | "unresponsive" | "manual";
  extensionIds: string[];
};

/* ---------- frame 通道 Frame channels ---------- */

export type CreateExtensionFrameRequest = {
  extensionId: string;
  frameId: string;
  kind: ExtensionFrameKind;
  viewId: string | null;
  entry: string | null;
  backgroundColor: string;
};

export type ExtensionFrameBounds = {
  x: number;
  y: number;
  width: number;
  height: number;
};

export type ExtensionFrameLayoutUpdate = {
  frameId: string;
  bounds: ExtensionFrameBounds;
  visible: boolean;
};

export type ExtensionFrameSnapshot = {
  frameId: string;
  snapshot: string | null;
  /** 遮挡请求的序号，画出截图后用它隐藏 view。The obscure request token used to hide the view once the snapshot is painted. */
  token: number;
};

/** 宿主画出截图后请求隐藏的 view。A view the host asks to hide once it painted the snapshot. */
export type ExtensionFrameObscureRequest = {
  frameId: string;
  token: number;
};

/** 主窗口 preload 交给宿主页面的端口消息。Port message the main-window preload hands to the host page. */
export type ExtensionPortMessage = {
  type: "agw:extension-port";
  extensionId: string;
  frameId: string;
  pageId: string;
};

/* ---------- 能力通道 Capability channels ---------- */

export type ExtensionCaller = {
  hostInstanceId: string;
  extensionId: string;
  frameId: string;
  pageId: string;
};

export type ExtensionIpcError = ExtensionErrorData;

export type ExtensionIpcResult<T> =
  | { ok: true; value: T }
  | { ok: false; error: ExtensionIpcError };

export type ExtensionFileSystemRequest =
  | { operation: "readFile"; uri: string }
  | {
      operation: "writeFile";
      uri: string;
      data: Uint8Array;
      overwrite: boolean;
      createParents: boolean;
      /**
       * 写入前提：磁盘上文件的内容哈希（64 位小写十六进制），只能与 `overwrite: true` 一起使用；与磁盘上的哈希不一致时为 `ERR_FILE_CHANGED`。
       * Write precondition: the content hash on disk (64 lowercase hex digits), only with `overwrite: true`; a mismatch is `ERR_FILE_CHANGED`.
       */
      expectedSha256?: string;
    }
  | { operation: "readDirectory"; uri: string }
  | { operation: "stat"; uri: string }
  | { operation: "exists"; uri: string }
  | { operation: "createDirectory"; uri: string; recursive: boolean }
  | { operation: "delete"; uri: string; recursive: boolean }
  | { operation: "rename"; from: string; to: string; overwrite: boolean };

export type ExtensionFileSystemResult = {
  readFile: Uint8Array;
  writeFile: null;
  readDirectory: DirectoryEntry[];
  stat: FileStat;
  exists: boolean;
  createDirectory: null;
  delete: null;
  rename: null;
};

export type ExtensionWatchRequest = {
  watchId: string;
  uri: string;
  recursive: boolean;
};

export type ExtensionDialogFilter = {
  name: string;
  extensions: string[];
};

export type ExtensionOpenDialogOptions = {
  title?: string;
  directory?: boolean;
  multiple?: boolean;
  filters?: ExtensionDialogFilter[];
};

export type ExtensionSaveDialogOptions = {
  title?: string;
  defaultName?: string;
  filters?: ExtensionDialogFilter[];
};

export type ExtensionHttpRequest = {
  requestId: string;
  url: string;
  method?: string;
  headers?: Record<string, string>;
  body?: string | Uint8Array;
};

export type ExtensionHttpResult = {
  status: number;
  headers: Record<string, string>;
  body: Uint8Array;
};

/* ---------- 推送事件 Push events ---------- */

export type ExtensionRequestBlockedReason =
  | "host-not-permitted"
  | "navigation-blocked"
  | "upstream-proxy-unsupported"
  | "service-worker-blocked";

export type ExtensionEvent =
  | {
      type: "file-change";
      extensionId: string;
      frameId: string;
      pageId: string;
      watchId: string;
      event: FileChangeEvent;
    }
  | {
      type: "request-blocked";
      extensionId: string;
      url: string;
      resourceType: string;
      reason: ExtensionRequestBlockedReason;
    }
  | {
      type: "frame-failed";
      extensionId: string;
      frameId: string;
      code: string;
      message: string;
    }
  | {
      type: "frame-page-replaced";
      extensionId: string;
      frameId: string;
      pageId: string;
    }
  | {
      type: "frame-gone";
      extensionId: string;
      frameId: string;
      reason: string;
      osProcessId: number;
    }
  | {
      type: "extension-failed";
      extensionId: string;
      code: string;
      message: string;
    };
