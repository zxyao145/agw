import type { ConfirmationAnswer, HostConfirmationRequest } from "./confirmation";
import type { DesktopRuntimeState } from "./runtime";
import type { DesktopSettingsUpdate } from "./settings";
import type { DesktopUpdateCheckResult } from "./update";
import type { ImageContextMenuRequest } from "./image";
import type { DecodedText, EncodedText, EncodeTextOptions, TextEncodingInfo } from "./text";
import type {
  CreateExtensionFrameRequest,
  ExtensionActivation,
  ExtensionCaller,
  ExtensionEvent,
  ExtensionFileSystemRequest,
  ExtensionFileSystemResult,
  ExtensionFrameLayoutUpdate,
  ExtensionFrameObscureRequest,
  ExtensionFrameSnapshot,
  ExtensionHttpRequest,
  ExtensionHttpResult,
  ExtensionInstallPreview,
  ExtensionInstallSource,
  ExtensionIpcResult,
  ExtensionOpenDialogOptions,
  ExtensionRecoveryNotice,
  ExtensionSaveDialogOptions,
  ExtensionWatchRequest,
  InstalledExtension,
  SaveExtensionSdkTypesResult,
  ThemeStyleRequest,
  ThemeStyleText,
  UninstallExtensionOptions,
} from "./extension";

export type UninstallRequest = {
  deleteServerData: boolean;
};

export type UninstallResult = {
  manualActionRequired: boolean;
  message: string;
};

export type TurnNotificationStatus = "completed" | "failed";

export type TurnNotificationRequest = {
  status: TurnNotificationStatus;
  title?: string;
};

export type AgwDesktopBridge = {
  getRuntimeState(): Promise<DesktopRuntimeState>;
  checkForUpdates(): Promise<DesktopUpdateCheckResult>;
  saveSettings(settings: DesktopSettingsUpdate): Promise<DesktopRuntimeState>;
  /** 文件类型 → 默认查看器 id。File type → default viewer id. */
  getFileViewerDefaults(): Promise<Record<string, string>>;
  /** 设置文件类型的默认查看器，null 删除该项；返回更新后的设置。Sets a file type's default viewer; null removes it. */
  setFileViewerDefault(fileType: string, viewerId: string | null): Promise<Record<string, string>>;
  saveToken(profileId: string, token: string): Promise<void>;
  deleteToken(profileId: string): Promise<void>;
  provisionLocalToken(): Promise<string>;
  getOidcProviders(
    profileId: string,
  ): Promise<Array<{ id: string; displayName: string; type?: "Oidc" | "OAuth2" }>>;
  loginWithOidc(profileId: string, providerId: string): Promise<void>;
  cancelOidcLogin(profileId: string): Promise<void>;
  logoutOidc(profileId: string): Promise<void>;
  openExternal(url: string): Promise<void>;
  showImageContextMenu(request: ImageContextMenuRequest): Promise<void>;
  openSetup(baseUrl: string): Promise<void>;
  setActiveTaskCount(count: number): Promise<void>;
  prepareUninstall(request: UninstallRequest): Promise<UninstallResult>;
  showWindow(): Promise<void>;
  showTurnNotification(request: TurnNotificationRequest): Promise<void>;
  quitDesktop(): Promise<void>;

  /* extension 管理通道 Extension management channels */
  attachExtensionHost(hostInstanceId: string): Promise<void>;
  listExtensions(): Promise<InstalledExtension[]>;
  listExtensionRecoveryNotices(): Promise<ExtensionRecoveryNotice[]>;
  prepareExtensionInstall(request: {
    source: ExtensionInstallSource;
  }): Promise<ExtensionInstallPreview>;
  /**
   * 在主进程的确认窗口中批准安装；批准绑定暂存包的摘要、版本与权限，返回用户是否批准。
   * Approves an install in the main process's confirmation window; the approval binds the staged package's digest,
   * version and permissions. Returns whether the user approved.
   */
  approveExtensionInstall(installId: string): Promise<boolean>;
  /** 提交已经批准的安装；暂存包变化或没有批准时失败。Commits an approved install; fails without an approval or when the staged package changed. */
  commitExtensionInstall(installId: string): Promise<InstalledExtension>;
  cancelExtensionInstall(installId: string): Promise<void>;
  uninstallExtension(
    extensionId: string,
    options: UninstallExtensionOptions,
  ): Promise<DesktopRuntimeState>;
  /**
   * 启用时由主进程的确认窗口批准 manifest 声明的权限并保存；返回之后是否为启用状态（用户取消时为 false）。
   * Enabling has the main process's confirmation window approve and save the permissions the manifest declares;
   * returns whether the extension is enabled afterwards (false when the user canceled).
   */
  setExtensionEnabled(extensionId: string, enabled: boolean): Promise<boolean>;
  beginExtensionActivation(
    hostInstanceId: string,
    extensionId: string,
  ): Promise<ExtensionActivation>;
  endExtensionActivation(hostInstanceId: string, extensionId: string): Promise<void>;
  recordExtensionActivation(
    hostInstanceId: string,
    extensionId: string,
    version: string,
  ): Promise<DesktopRuntimeState>;
  releaseExtensionFrameResources(
    hostInstanceId: string,
    extensionId: string,
    frameId: string,
  ): Promise<void>;
  releaseExtensionResources(hostInstanceId: string, extensionId: string): Promise<void>;
  resumeExtensions(): Promise<DesktopRuntimeState>;
  openExtensionsDirectory(): Promise<void>;
  openExtensionDataDirectory(extensionId: string): Promise<void>;
  cancelExtensionHttpRequest(requestId: string): Promise<void>;
  saveExtensionSdkTypes(): Promise<SaveExtensionSdkTypesResult>;
  /** 显式 reload：主进程重新读取 extension 的全部主题依赖。An explicit reload: main re-reads every theme dependency of the extension. */
  reloadExtension(extensionId: string): Promise<void>;
  /** 当前选中的 CSS 主题的完整样式文本。The complete style text of the selected CSS theme. */
  getThemeStyle(request: ThemeStyleRequest): Promise<ThemeStyleText>;
  /**
   * 在主进程的独立确认窗口中显示确认；关闭窗口时 `message` 回答 `cancelId`，其他回答 false。
   * Shows a confirmation in the main process's separate confirmation window; closing it answers `cancelId` for
   * `message` and false for the others.
   */
  confirmHostAction(request: HostConfirmationRequest): Promise<ConfirmationAnswer>;
  /** 撤回宿主页面请求的消息框，按取消回答。Withdraws a message box the host page requested, answering it as canceled. */
  dismissHostConfirmation(clientId: string): Promise<void>;
  createExtensionFrame(hostInstanceId: string, request: CreateExtensionFrameRequest): Promise<void>;
  updateExtensionFrameLayout(
    hostInstanceId: string,
    updates: ExtensionFrameLayoutUpdate[],
  ): Promise<void>;
  obscureExtensionFrames(
    hostInstanceId: string,
    frameIds: string[],
  ): Promise<ExtensionFrameSnapshot[]>;
  /** 宿主画出截图后隐藏 view。Hides views once the host painted their snapshots. */
  hideObscuredExtensionFrames(
    hostInstanceId: string,
    requests: ExtensionFrameObscureRequest[],
  ): Promise<void>;
  revealExtensionFrames(hostInstanceId: string, frameIds: string[]): Promise<void>;
  setExtensionFrameBackground(hostInstanceId: string, color: string): Promise<void>;
  destroyExtensionFrame(hostInstanceId: string, frameId: string): Promise<void>;
  terminateExtensionFrameProcess(hostInstanceId: string, frameId: string): Promise<void>;
  openExtensionFrameDevTools(hostInstanceId: string, frameId: string): Promise<void>;

  /* extension 能力通道 Extension capability channels */
  extensionFileSystem<R extends ExtensionFileSystemRequest>(
    caller: ExtensionCaller,
    request: R,
  ): Promise<ExtensionIpcResult<ExtensionFileSystemResult[R["operation"]]>>;
  extensionWatch(
    caller: ExtensionCaller,
    request: ExtensionWatchRequest,
  ): Promise<ExtensionIpcResult<null>>;
  extensionUnwatch(caller: ExtensionCaller, watchId: string): Promise<ExtensionIpcResult<null>>;
  showExtensionOpenDialog(
    caller: ExtensionCaller,
    options: ExtensionOpenDialogOptions,
  ): Promise<ExtensionIpcResult<string[]>>;
  showExtensionSaveDialog(
    caller: ExtensionCaller,
    options: ExtensionSaveDialogOptions,
  ): Promise<ExtensionIpcResult<string | null>>;
  revealExtensionPath(caller: ExtensionCaller, uri: string): Promise<ExtensionIpcResult<null>>;
  extensionHttpRequest(
    caller: ExtensionCaller,
    request: ExtensionHttpRequest,
  ): Promise<ExtensionIpcResult<ExtensionHttpResult>>;

  /* 文本编码通道，只供宿主页面调用 Text codec channels, for the host page only */
  /** 识别编码并解码；指定 `encoding` 时按它解码。Detects the encoding and decodes, or decodes with `encoding` when given. */
  decodeText(bytes: Uint8Array, encoding?: string): Promise<ExtensionIpcResult<DecodedText>>;
  /** 替换换行符后按编码写出。Replaces line breaks and encodes. */
  encodeText(text: string, options: EncodeTextOptions): Promise<ExtensionIpcResult<EncodedText>>;
  listTextEncodings(): Promise<TextEncodingInfo[]>;

  /* 推送通道 Push channel */
  onExtensionEvent(listener: (event: ExtensionEvent) => void): () => void;
};
