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
  ExtensionGrants,
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
  commitExtensionInstall(installId: string): Promise<InstalledExtension>;
  cancelExtensionInstall(installId: string): Promise<void>;
  uninstallExtension(
    extensionId: string,
    options: UninstallExtensionOptions,
  ): Promise<DesktopRuntimeState>;
  setExtensionEnabled(
    extensionId: string,
    enabled: boolean,
    grants?: ExtensionGrants,
  ): Promise<DesktopRuntimeState>;
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
