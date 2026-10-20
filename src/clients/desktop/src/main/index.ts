import { DesktopOidcLogin } from "./oidc-login";

import { existsSync, lstatSync, readFileSync } from "node:fs";
import { rm } from "node:fs/promises";
import { release } from "node:os";
import { dirname, join, resolve } from "node:path";
import { pathToFileURL } from "node:url";

import {
  app,
  BrowserWindow,
  dialog,
  ipcMain,
  Menu,
  nativeImage,
  net,
  Notification,
  protocol,
  safeStorage,
  session,
  shell,
  Tray,
  type IpcMainInvokeEvent,
} from "electron";
import started from "electron-squirrel-startup";

import type {
  DesktopRuntimeState,
  DesktopSettings,
  DesktopSettingsUpdate,
  ExtensionSafeModeState,
  ImageContextMenuRequest,
  UninstallRequest,
  UninstallResult,
} from "../shared/contracts";
import { DaemonManager } from "./daemon/daemon-manager";
import { registerExtensionIpc } from "./extensions/extension-ipc";
import { EXTENSION_SCHEME_PRIVILEGES } from "./extensions/extension-protocol";
import { ExtensionRuntime } from "./extensions/extension-runtime";
import { getExternalHttpUrl } from "./external-navigation";
import { showImageContextMenu } from "./image-context-menu";
import { DESKTOP_OAUTH_PROTOCOL, findOAuthDeepLink, parseOAuthDeepLink } from "./oauth-deep-link";
import {
  parseDesktopPackageMetadata,
  readDevelopmentPackageMetadata,
  type DesktopPackageMetadata,
} from "./package-metadata";
import { resolveRendererFile } from "./renderer-path";
import {
  appendRendererEvent,
  createRendererEventRecord,
  RendererRecoveryGuard,
} from "./renderer-recovery";
import { createLocalDesktopToken } from "./runtime/local-token";
import { readLocalServerRuntime } from "./runtime/local-server-runtime";
import { getServerDataDirectory } from "./runtime/server-data-directory";
import { resolveServerExecutablePath } from "./runtime/server-executable-path";
import { getTurnNotificationText, normalizeTurnNotificationRequest } from "./turn-notification";
import { DesktopSettingsStore, type SecretCodec } from "./settings/settings-store";
import { checkForDesktopUpdate } from "./update/github-release-updater";

protocol.registerSchemesAsPrivileged([
  {
    scheme: "agw",
    privileges: { standard: true, secure: true, supportFetchAPI: true, corsEnabled: true },
  },
  EXTENSION_SCHEME_PRIVILEGES,
]);

if (started) app.quit();

app.setName("Agw Desktop");

const hasSingleInstanceLock = !started && app.requestSingleInstanceLock();
if (!hasSingleInstanceLock) app.quit();

let mainWindow: BrowserWindow | null = null;
let tray: Tray | null = null;
let isQuitting = false;
let activeTaskCount = 0;
let currentSettings: DesktopSettings;
let settingsStore: DesktopSettingsStore;
let daemonManager: DaemonManager;
let packageMetadata: DesktopPackageMetadata;
let pendingOAuthRoute: string | null = null;
let rendererReady = false;
let lastTrustedRendererUrl: string | null = null;
let rendererReloadRequired = false;
let rendererRecoveryInProgress = false;
let rendererRecoveryAttempt: Promise<boolean> | null = null;
const rendererRecoveryGuard = new RendererRecoveryGuard();
let extensionRuntime: ExtensionRuntime;

function reportMainProcessError(title: string, error: unknown): void {
  console.error(title, error);
  dialog.showErrorBox(title, error instanceof Error ? error.message : String(error));
}

function readPackageMetadata(): DesktopPackageMetadata {
  if (!app.isPackaged) {
    return readDevelopmentPackageMetadata(
      app.getAppPath(),
      process.env.AGW_PACKAGE_FLAVOR === "client" ? "client" : "full",
    );
  }
  try {
    return parseDesktopPackageMetadata(
      JSON.parse(readFileSync(join(process.resourcesPath, "package-flavor.json"), "utf8")),
    );
  } catch (error) {
    console.warn("Unable to read Desktop package metadata; using application defaults.", error);
    return {
      packageFlavor: "full",
      appVersion: app.getVersion(),
    };
  }
}

function createSecretCodec(): SecretCodec {
  return {
    encrypt(value) {
      if (!safeStorage.isEncryptionAvailable()) {
        throw new Error("The operating system secure storage is unavailable.");
      }
      return safeStorage.encryptString(value);
    },
    decrypt(value) {
      if (!safeStorage.isEncryptionAvailable()) {
        throw new Error("The operating system secure storage is unavailable.");
      }
      return safeStorage.decryptString(value);
    },
  };
}

function serverExecutablePath(): string {
  return resolveServerExecutablePath(
    process.resourcesPath,
    process.platform,
    process.env.AGW_SERVER_PATH,
  );
}

function rendererRoot(): string {
  return app.isPackaged
    ? join(process.resourcesPath, "renderer")
    : resolve(__dirname, "..", "..", "resources", "renderer");
}

function extensionSdkRoot(): string {
  return app.isPackaged
    ? join(process.resourcesPath, "extension-sdk")
    : resolve(__dirname, "..", "..", "resources", "extension-sdk");
}

function bundledExtensionsRoot(): string {
  return app.isPackaged
    ? join(process.resourcesPath, "extensions")
    : resolve(__dirname, "..", "..", "resources", "extensions");
}

function trayIconPath(): string {
  return app.isPackaged
    ? join(process.resourcesPath, "assets", "tray-icon.svg")
    : resolve(__dirname, "..", "..", "assets", "tray-icon.svg");
}

function appIconPath(): string {
  return app.isPackaged
    ? join(process.resourcesPath, "assets", "agw-logo.png")
    : resolve(__dirname, "..", "..", "assets", "agw-logo.png");
}

function isTrustedRenderer(url: string): boolean {
  if (url.startsWith("agw://app/")) return true;
  const developmentUrl = process.env.AGW_RENDERER_URL;
  return Boolean(developmentUrl && url.startsWith(developmentUrl));
}

function assertTrustedSender(url: string): void {
  if (!isTrustedRenderer(url))
    throw new Error("Desktop IPC is only available to the bundled renderer.");
}

function senderUrl(event: IpcMainInvokeEvent): string {
  return event.senderFrame?.url ?? event.sender.getURL();
}

let oidcLogin: DesktopOidcLogin | null = null;

async function runtimeState(): Promise<DesktopRuntimeState> {
  const activeToken = await settingsStore.loadToken(currentSettings.activeServerId);
  return {
    isDesktop: true,
    platform: process.platform,
    architecture: process.arch,
    appVersion: packageMetadata.appVersion,
    packageFlavor: currentSettings.packageFlavor,
    settings: currentSettings,
    activeToken,
    activeCredentialSource: await settingsStore.credentialSource(currentSettings.activeServerId),
    localServerRuntime: await readLocalServerRuntime(),
    extensionSafeMode: extensionRuntime.safeMode.state,
  };
}

function registerStaticProtocol(): void {
  protocol.handle("agw", async (request) => {
    const url = new URL(request.url);
    if (url.host !== "app") return new Response("Not found", { status: 404 });
    try {
      const file = resolveRendererFile(rendererRoot(), url.pathname);
      return await net.fetch(pathToFileURL(file).toString());
    } catch {
      return new Response("Not found", { status: 404 });
    }
  });
}

async function prepareRendererSession(): Promise<void> {
  if (!app.isPackaged && process.env.AGW_RENDERER_URL) {
    await session.defaultSession.clearCache();
  }
}

async function loadRenderer(pathname = "/desktop/chat/"): Promise<void> {
  if (!mainWindow) return;
  const developmentUrl = process.env.AGW_RENDERER_URL;
  const target = developmentUrl
    ? new URL(pathname, developmentUrl).toString()
    : `agw://app${pathname}`;
  await loadTrustedRendererUrl(target);
}

// 主进程主动加载渲染页面时不询问未保存修改。Renderer loads started by the main process skip the unsaved-changes prompt.
let mainProcessRendererLoad = false;

async function loadTrustedRendererUrl(target: string): Promise<void> {
  if (!mainWindow) return;
  if (!isTrustedRenderer(target)) {
    throw new Error("Refusing to load an untrusted renderer URL.");
  }
  rendererRecoveryGuard.markLoadStarted();
  lastTrustedRendererUrl = target;
  mainProcessRendererLoad = true;
  try {
    await mainWindow.loadURL(target);
  } finally {
    mainProcessRendererLoad = false;
  }
}

function writeRendererEvent(
  event:
    | "render-process-gone"
    | "did-fail-load"
    | "unresponsive"
    | "responsive"
    | "extension-safe-mode",
  reason: string,
  url: string,
  exitCode?: number,
  extensionIds?: string[],
): void {
  const record = createRendererEventRecord(
    {
      event,
      reason,
      pathname: url,
      ...(exitCode === undefined ? {} : { exitCode }),
      ...(extensionIds === undefined ? {} : { extensionIds }),
    },
    {
      appVersion: packageMetadata?.appVersion ?? app.getVersion(),
      electronVersion: process.versions.electron,
      os: `${process.platform} ${release()}`,
    },
  );
  void appendRendererEvent(join(app.getPath("logs"), "renderer-events.jsonl"), record).catch(
    (error) => console.warn("Unable to write renderer diagnostic event.", error),
  );
}

function writeExtensionSafeModeEvent(state: ExtensionSafeModeState, url: string): void {
  writeRendererEvent("extension-safe-mode", state.reason, url, undefined, state.extensionIds);
}

/** 旧宿主页面结束：销毁 extension frame 并释放资源。The old host page ended: destroy extension frames and release resources. */
function resetExtensionRuntime(): void {
  void extensionRuntime.reset
    .run()
    .catch((error) => console.error("Unable to reset the extension runtime.", error));
}

/** 托盘 "Reload without extensions"。The tray's "Reload without extensions". */
async function reloadWithoutExtensions(): Promise<void> {
  const state = extensionRuntime.safeMode.enterManual();
  writeExtensionSafeModeEvent(state, lastTrustedRendererUrl ?? "agw://app/");
  await extensionRuntime.reset.run();
  if (!mainWindow) mainWindow = createMainWindow();
  await reloadRendererWindow();
  mainWindow.show();
}

async function reloadRendererWindow(): Promise<void> {
  const target =
    lastTrustedRendererUrl ??
    (process.env.AGW_RENDERER_URL
      ? new URL("/desktop/chat/", process.env.AGW_RENDERER_URL).toString()
      : "agw://app/desktop/chat/");
  await loadTrustedRendererUrl(target);
}

async function showManualRendererRecovery(window: BrowserWindow): Promise<boolean> {
  if (window.isDestroyed()) return false;
  const answer = await dialog.showMessageBox(window, {
    type: "error",
    buttons: ["Reload Chat", "Close Window"],
    defaultId: 0,
    cancelId: 1,
    title: "Chat renderer stopped",
    message: "The Chat renderer stopped again before recovery completed.",
    detail:
      "Reload Chat retries without starting a new execution. Close Window hides Agw and reloads Chat the next time it is opened.",
  });

  if (answer.response === 0) {
    await reloadRendererWindow();
    return true;
  }

  rendererReloadRequired = true;
  window.hide();
  return false;
}

function recoverRenderer(window: BrowserWindow): Promise<boolean> {
  if (rendererRecoveryInProgress) return rendererRecoveryAttempt ?? Promise.resolve(false);
  if (isQuitting || window.isDestroyed()) return Promise.resolve(false);
  rendererRecoveryInProgress = true;
  const action = rendererRecoveryGuard.recordFailure();
  const recovery = (async () => {
    try {
      if (action === "auto-reload") {
        try {
          await reloadRendererWindow();
          return true;
        } catch {
          // A failed automatic navigation immediately hands control to manual recovery.
        }
      }
      return await showManualRendererRecovery(window);
    } finally {
      rendererRecoveryInProgress = false;
    }
  })();
  rendererRecoveryAttempt = recovery;
  return recovery;
}

function createMainWindow(): BrowserWindow {
  let destructionPlanned = false;
  const window = new BrowserWindow({
    width: 1440,
    height: 920,
    minWidth: 980,
    minHeight: 680,
    icon: appIconPath(),
    show: false,
    title: "Agw Desktop",
    titleBarStyle: process.platform === "darwin" ? "hiddenInset" : "hidden",
    titleBarOverlay:
      process.platform === "darwin"
        ? false
        : { color: "#00000000", symbolColor: "#8f96a3", height: 48 },
    trafficLightPosition: process.platform === "darwin" ? { x: 18, y: 18 } : undefined,
    backgroundColor: "#0b0c0f",
    webPreferences: {
      preload: resolve(__dirname, "..", "preload", "index.js"),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });

  window.webContents.setWindowOpenHandler(({ url }) => {
    const externalUrl = getExternalHttpUrl(url);
    if (externalUrl) {
      void shell
        .openExternal(externalUrl)
        .catch((error) => reportMainProcessError("Unable to open external link", error));
    }
    return { action: "deny" };
  });
  window.webContents.on("will-navigate", (event, target) => {
    if (!isTrustedRenderer(target)) event.preventDefault();
  });
  // 文件查看器有未保存修改时，关闭窗口、退出或刷新前由用户确认。preventDefault 表示继续卸载。
  // With unsaved file viewer changes, closing, quitting or reloading asks the user first; preventDefault continues the unload.
  window.webContents.on("will-prevent-unload", (event) => {
    if (mainProcessRendererLoad) {
      event.preventDefault();
      return;
    }
    const choice = dialog.showMessageBoxSync(window, {
      type: "warning",
      buttons: ["Discard", "Cancel"],
      defaultId: 1,
      cancelId: 1,
      message: "Discard unsaved changes?",
      detail: "A file opened in an extension has changes that have not been saved.",
    });
    if (choice === 0) event.preventDefault();
  });
  window.webContents.on("did-navigate", (_event, target) => {
    if (isTrustedRenderer(target)) lastTrustedRendererUrl = target;
    // 宿主文档已被替换，旧页面创建的 extension frame 与资源不再有接收方。
    // The host document was replaced, so frames and resources created by the old page have no owner.
    resetExtensionRuntime();
  });
  window.webContents.on("did-finish-load", () => {
    const target = window.webContents.getURL();
    if (isTrustedRenderer(target)) lastTrustedRendererUrl = target;
    rendererRecoveryGuard.markLoadSucceeded();
  });
  window.webContents.on("render-process-gone", (_event, details) => {
    const target = window.webContents.getURL() || lastTrustedRendererUrl || "agw://app/";
    writeRendererEvent("render-process-gone", details.reason, target, details.exitCode);
    // 安全模式的判断先于清理，判断使用的正在激活集合不受清理影响。
    // Safe mode is decided before the cleanup, so the cleanup cannot affect the activation set it reads.
    if (details.reason !== "clean-exit") {
      const safeMode = extensionRuntime.safeMode.recordHostFailure("crashed");
      if (safeMode) writeExtensionSafeModeEvent(safeMode, target);
    }
    resetExtensionRuntime();
    if (
      details.reason === "clean-exit" ||
      destructionPlanned ||
      isQuitting ||
      window.isDestroyed()
    ) {
      return;
    }
    void recoverRenderer(window).catch((error) =>
      reportMainProcessError("Unable to recover Chat renderer", error),
    );
  });
  window.webContents.on(
    "did-fail-load",
    (_event, errorCode, errorDescription, validatedUrl, isMainFrame) => {
      if (
        !isMainFrame ||
        destructionPlanned ||
        errorCode === -3 ||
        errorDescription === "ERR_ABORTED"
      ) {
        return;
      }
      const target = validatedUrl || window.webContents.getURL() || lastTrustedRendererUrl || "";
      writeRendererEvent("did-fail-load", errorDescription, target, errorCode);
      void recoverRenderer(window).catch((error) =>
        reportMainProcessError("Unable to recover Chat renderer", error),
      );
    },
  );
  window.on("unresponsive", () => {
    const target = window.webContents.getURL() || lastTrustedRendererUrl || "";
    writeRendererEvent("unresponsive", "unresponsive", target);
    // extension 激活期间主界面失去响应：进入安全模式并结束宿主进程，由现有恢复流程重新加载。
    // The host hung while extensions were activating: enter safe mode and end the host process for recovery.
    const safeMode = extensionRuntime.safeMode.recordHostFailure("unresponsive");
    if (!safeMode) return;
    writeExtensionSafeModeEvent(safeMode, target);
    resetExtensionRuntime();
    window.webContents.forcefullyCrashRenderer();
  });
  window.on("responsive", () => {
    writeRendererEvent(
      "responsive",
      "responsive",
      window.webContents.getURL() || lastTrustedRendererUrl || "",
    );
  });
  window.once("ready-to-show", () => window.show());
  window.on("close", (event) => {
    if (!isQuitting && currentSettings.closeBehavior === "minimize-to-tray") {
      event.preventDefault();
      window.hide();
      return;
    }
    destructionPlanned = true;
    rendererRecoveryGuard.markLoadStarted();
  });
  window.on("closed", () => {
    mainWindow = null;
    resetExtensionRuntime();
  });
  return window;
}

function rebuildTrayMenu(): void {
  if (!tray) return;
  tray.setContextMenu(
    Menu.buildFromTemplate([
      { label: "Open Agw Chat", click: () => showWindowSafely("/desktop/chat/") },
      {
        label: activeTaskCount === 1 ? "1 active task" : `${activeTaskCount} active tasks`,
        enabled: false,
      },
      { type: "separator" },
      { label: "Settings", click: () => showWindowSafely("/settings/") },
      {
        label: "Reload without extensions",
        click: () => {
          void reloadWithoutExtensions().catch((error) =>
            reportMainProcessError("Unable to reload without extensions", error),
          );
        },
      },
      { type: "separator" },
      {
        label: "Quit Desktop",
        click: () => {
          isQuitting = true;
          app.quit();
        },
      },
    ]),
  );
}

function createTray(): void {
  const image = nativeImage.createFromPath(trayIconPath());
  if (process.platform === "darwin") image.setTemplateImage(true);
  tray = new Tray(image);
  tray.setToolTip("Agw Desktop");
  tray.on("click", () => showWindowSafely("/desktop/chat/"));
  rebuildTrayMenu();
}

async function showWindow(pathname?: string): Promise<void> {
  if (!mainWindow) mainWindow = createMainWindow();
  if (rendererReloadRequired) {
    rendererReloadRequired = false;
    try {
      await reloadRendererWindow();
    } catch (error) {
      rendererReloadRequired = true;
      throw error;
    }
  } else if (pathname) {
    await loadRenderer(pathname);
  }
  mainWindow.show();
  mainWindow.focus();
}

function showTurnNotification(request: unknown): void {
  const notificationRequest = normalizeTurnNotificationRequest(request);
  if (!notificationRequest || !Notification.isSupported()) return;
  if (mainWindow?.isFocused()) return;
  const notification = new Notification(
    getTurnNotificationText(notificationRequest.status, notificationRequest.title),
  );
  notification.on("click", () => showWindowSafely());
  notification.show();
}

function showWindowSafely(pathname?: string): void {
  void showWindow(pathname).catch((error) =>
    reportMainProcessError("Unable to open Agw Desktop", error),
  );
}

function handleOAuthDeepLink(value: string): boolean {
  if (oidcLogin?.handleRedirect(value)) {
    showWindowSafely();
    return true;
  }
  const route = parseOAuthDeepLink(value);
  if (!route) return false;
  if (!rendererReady) {
    pendingOAuthRoute = route;
    return true;
  }
  showWindowSafely(route);
  return true;
}

function registerOAuthProtocolClient(): void {
  const registered =
    process.defaultApp && process.argv[1]
      ? app.setAsDefaultProtocolClient(DESKTOP_OAUTH_PROTOCOL, process.execPath, [
          resolve(process.argv[1]),
        ])
      : app.setAsDefaultProtocolClient(DESKTOP_OAUTH_PROTOCOL);
  if (!registered) console.warn(`Unable to register ${DESKTOP_OAUTH_PROTOCOL} as a URL handler.`);
}

async function openSetup(baseUrl: string): Promise<void> {
  if (!mainWindow) return;
  const origin = new URL(baseUrl).origin;
  const setupWindow = new BrowserWindow({
    parent: mainWindow,
    modal: true,
    width: 760,
    height: 760,
    title: "Set up Agw Server",
    backgroundColor: "#0b0c0f",
    webPreferences: { contextIsolation: true, nodeIntegration: false, sandbox: true },
  });
  setupWindow.webContents.setWindowOpenHandler(() => ({ action: "deny" }));
  setupWindow.webContents.on("will-navigate", (event, target) => {
    const targetUrl = new URL(target);
    if (targetUrl.origin !== origin) event.preventDefault();
  });
  let completed = false;
  setupWindow.webContents.on("did-navigate", (_event, target) => {
    const targetUrl = new URL(target);
    if (targetUrl.origin === origin && !targetUrl.pathname.startsWith("/setup")) {
      completed = true;
      setupWindow.close();
    }
  });
  const setupCompleted = new Promise<void>((resolve, reject) => {
    setupWindow.once("closed", () => {
      if (completed) resolve();
      else reject(new Error("Server setup was closed before it completed."));
    });
  });
  await setupWindow.loadURL(`${origin}/setup`);
  await setupCompleted;
}

async function prepareUninstall(request: UninstallRequest): Promise<UninstallResult> {
  await daemonManager.uninstall();
  if (request.deleteServerData) {
    const dataRoot = getServerDataDirectory();
    if (existsSync(dataRoot) && lstatSync(dataRoot).isSymbolicLink()) {
      throw new Error("Refusing to remove a symbolic-link data directory.");
    }
    await rm(dataRoot, { recursive: true, force: true });
  }

  if (process.platform === "darwin") {
    shell.showItemInFolder(process.execPath);
    return {
      manualActionRequired: true,
      message: "Move Agw Desktop to Trash after the application closes.",
    };
  }
  if (process.platform === "linux") {
    return {
      manualActionRequired: true,
      message:
        "Close Agw Desktop, then uninstall the agw-desktop package with your package manager.",
    };
  }

  const updateExecutable = windowsUpdateExecutablePath();
  if (existsSync(updateExecutable)) {
    const { spawn } = await import("node:child_process");
    spawn(updateExecutable, ["--uninstall", "-s"], { detached: true, stdio: "ignore" }).unref();
    isQuitting = true;
    app.quit();
    return { manualActionRequired: false, message: "Agw Desktop is being uninstalled." };
  }
  return {
    manualActionRequired: true,
    message: "Use Windows Apps settings to uninstall Agw Desktop.",
  };
}

function windowsUpdateExecutablePath(): string {
  return join(dirname(dirname(process.execPath)), "Update.exe");
}

function windowsDistribution(): "squirrel" | "portable" {
  return process.platform === "win32" && !existsSync(windowsUpdateExecutablePath())
    ? "portable"
    : "squirrel";
}

function registerIpc(): void {
  ipcMain.handle("agw:image-context-menu", (event, request: ImageContextMenuRequest) => {
    assertTrustedSender(senderUrl(event));
    showImageContextMenu(event.sender, request, reportMainProcessError);
  });
  ipcMain.handle("agw:oidc-providers", async (event, profileId: string) => {
    assertTrustedSender(senderUrl(event));
    return oidcLogin!.providers(profileId);
  });
  ipcMain.handle("agw:oidc-login", async (event, profileId: string, providerId: string) => {
    assertTrustedSender(senderUrl(event));
    return oidcLogin!.login(profileId, providerId);
  });
  ipcMain.handle("agw:oidc-cancel", async (event, profileId: string) => {
    assertTrustedSender(senderUrl(event));
    oidcLogin!.cancel(profileId);
  });
  ipcMain.handle("agw:oidc-logout", async (event, profileId: string) => {
    assertTrustedSender(senderUrl(event));
    return oidcLogin!.logout(profileId);
  });
  ipcMain.handle("agw:get-runtime-state", async (event) => {
    assertTrustedSender(senderUrl(event));
    return runtimeState();
  });
  ipcMain.handle("agw:check-for-updates", async (event) => {
    assertTrustedSender(senderUrl(event));
    return checkForDesktopUpdate((input, init) => net.fetch(input, init), {
      currentVersion: packageMetadata.appVersion,
      packageFlavor: packageMetadata.packageFlavor,
      platform: process.platform,
      architecture: process.arch,
      windowsDistribution: windowsDistribution(),
    });
  });
  ipcMain.handle("agw:save-settings", async (event, settings: DesktopSettingsUpdate) => {
    assertTrustedSender(senderUrl(event));
    if (settings.profiles) oidcLogin?.cancelAll();
    await settingsStore.save(settings);
    currentSettings = await settingsStore.load();
    return runtimeState();
  });
  ipcMain.handle("agw:get-file-viewer-defaults", async (event) => {
    assertTrustedSender(senderUrl(event));
    return (await settingsStore.load()).fileViewerDefaults;
  });
  ipcMain.handle(
    "agw:set-file-viewer-default",
    async (event, fileType: string, viewerId: string | null) => {
      assertTrustedSender(senderUrl(event));
      const defaults = await settingsStore.setFileViewerDefault(fileType, viewerId);
      currentSettings = await settingsStore.load();
      return defaults;
    },
  );
  ipcMain.handle("agw:save-token", async (event, profileId: string, token: string) => {
    assertTrustedSender(senderUrl(event));
    await settingsStore.saveToken(profileId, token);
  });
  ipcMain.handle("agw:delete-token", async (event, profileId: string) => {
    assertTrustedSender(senderUrl(event));
    await settingsStore.deleteToken(profileId);
  });
  ipcMain.handle("agw:provision-local-token", async (event) => {
    assertTrustedSender(senderUrl(event));
    const localProfile = currentSettings.profiles.find((profile) => profile.id === "local");
    if (!localProfile || currentSettings.activeServerId !== localProfile.id) {
      throw new Error("A Desktop token can only be provisioned for the active local Server.");
    }
    const token = await createLocalDesktopToken(
      session.defaultSession.fetch.bind(session.defaultSession),
      localProfile.baseUrl,
      `Agw Desktop ${crypto.randomUUID().slice(0, 8)}`,
    );
    await settingsStore.saveToken(localProfile.id, token);
    return token;
  });
  ipcMain.handle("agw:open-external", async (event, value: string) => {
    assertTrustedSender(senderUrl(event));
    const url = getExternalHttpUrl(value);
    if (!url) {
      throw new Error("Agw Desktop can only open HTTP(S) external URLs.");
    }
    await shell.openExternal(url);
  });
  ipcMain.handle("agw:open-setup", async (event, baseUrl: string) => {
    assertTrustedSender(senderUrl(event));
    const activeProfile = currentSettings.profiles.find(
      (profile) => profile.id === currentSettings.activeServerId,
    );
    if (!activeProfile || new URL(baseUrl).origin !== new URL(activeProfile.baseUrl).origin) {
      throw new Error("Setup is only available for the active Server profile.");
    }
    await openSetup(activeProfile.baseUrl);
  });
  ipcMain.handle("agw:set-active-task-count", async (event, count: number) => {
    assertTrustedSender(senderUrl(event));
    activeTaskCount = Math.max(0, Math.trunc(count));
    rebuildTrayMenu();
  });
  ipcMain.handle("agw:prepare-uninstall", async (event, request: UninstallRequest) => {
    assertTrustedSender(senderUrl(event));
    return prepareUninstall(request);
  });
  ipcMain.handle("agw:show-window", async (event) => {
    assertTrustedSender(senderUrl(event));
    await showWindow();
  });
  ipcMain.handle("agw:show-turn-notification", async (event, request: unknown) => {
    assertTrustedSender(senderUrl(event));
    showTurnNotification(request);
  });
  ipcMain.handle("agw:quit-desktop", async (event) => {
    assertTrustedSender(senderUrl(event));
    if (activeTaskCount > 0) {
      const answer = await dialog.showMessageBox(mainWindow!, {
        type: "warning",
        buttons: ["Cancel", "Quit Desktop"],
        defaultId: 0,
        cancelId: 0,
        title: "Tasks are still running",
        message: `${activeTaskCount} task${activeTaskCount === 1 ? " is" : "s are"} still running.`,
        detail:
          "The Server daemon will continue. Live streaming will stop, and pending Human Gate requests may be interrupted.",
      });
      if (answer.response === 0) return;
    }
    isQuitting = true;
    app.quit();
  });
}

/**
 * 集成测试在同一进程中读取 extension 登记表与窗口，并触发托盘菜单的操作。
 * Integration tests in the same process read the extension registry and window and trigger tray actions.
 */
export function integrationTestHooks() {
  return {
    extensionRuntime: () => extensionRuntime,
    mainWindow: () => mainWindow,
    reloadWithoutExtensions,
    openChatFromTray: () => showWindow("/desktop/chat/"),
  };
}

app.on("before-quit", () => {
  isQuitting = true;
  rendererRecoveryGuard.dispose();
  oidcLogin?.cancelAll();
});

app.on("activate", () => showWindowSafely());

if (hasSingleInstanceLock) {
  app.on("open-url", (event, url) => {
    event.preventDefault();
    handleOAuthDeepLink(url);
  });
  app.on("second-instance", (_event, argv) => {
    const deepLink = findOAuthDeepLink(argv);
    if (!deepLink || !handleOAuthDeepLink(deepLink)) showWindowSafely();
  });

  const initialDeepLink = findOAuthDeepLink(process.argv);
  if (initialDeepLink) handleOAuthDeepLink(initialDeepLink);
}

void app
  .whenReady()
  .then(async () => {
    if (!hasSingleInstanceLock) return;
    app.dock?.setIcon(appIconPath());
    registerOAuthProtocolClient();

    packageMetadata = readPackageMetadata();
    const flavor = packageMetadata.packageFlavor;
    settingsStore = new DesktopSettingsStore(app.getPath("userData"), flavor, createSecretCodec());
    currentSettings = await settingsStore.load();
    oidcLogin = new DesktopOidcLogin({
      getProfile: async (profileId) => {
        const settings = await settingsStore.load();
        const profile = settings.profiles.find((item) => item.id === profileId);
        if (!profile) throw new Error("The Server profile no longer exists.");
        const local = profile.kind === "local" ? await readLocalServerRuntime() : null;
        return local ? { ...profile, baseUrl: local.baseUrl } : profile;
      },
      fetch: (url, init) => net.fetch(url, init),
      openExternal: (url) => shell.openExternal(url),
      saveToken: (profileId, token, beforeCommit) =>
        settingsStore.saveToken(profileId, token, "oidc", beforeCommit),
      loadToken: (profileId) => settingsStore.loadToken(profileId),
      deleteToken: (profileId) => settingsStore.deleteToken(profileId),
    });
    daemonManager = new DaemonManager(process.platform, serverExecutablePath());
    extensionRuntime = new ExtensionRuntime({
      userDataDirectory: app.getPath("userData"),
      appVersion: packageMetadata.appVersion,
      sdkDirectory: extensionSdkRoot(),
      bundledExtensionsDirectory: bundledExtensionsRoot(),
      extensionPreloadPath: resolve(__dirname, "..", "preload", "extension.js"),
      settings: settingsStore,
      getMainWindow: () => mainWindow,
      onSettingsChanged: async () => {
        currentSettings = await settingsStore.load();
      },
      log: (message) => console.info(message),
    });
    // 扫描之前先恢复未完成的安装与卸载。Recover unfinished installs and uninstalls before scanning.
    await extensionRuntime.initialize();

    registerStaticProtocol();
    registerIpc();
    registerExtensionIpc({
      runtime: extensionRuntime,
      assertTrusted: (event) => assertTrustedSender(senderUrl(event)),
      runtimeState,
    });
    mainWindow = createMainWindow();
    createTray();
    await prepareRendererSession();

    try {
      if (flavor === "full" && (await daemonManager.isServerBundled()))
        await daemonManager.install();
      if (flavor === "client") await daemonManager.uninstall();
    } catch (error) {
      dialog.showErrorBox(
        "Agw Server daemon",
        error instanceof Error ? error.message : String(error),
      );
    }

    const initialRoute = pendingOAuthRoute ?? "/desktop/chat/";
    pendingOAuthRoute = null;
    try {
      await loadRenderer(initialRoute);
    } catch (error) {
      const recovery = rendererRecoveryAttempt;
      if (!recovery || !(await recovery)) throw error;
    }
    rendererReady = true;
    if (pendingOAuthRoute) {
      const nextRoute = pendingOAuthRoute;
      pendingOAuthRoute = null;
      await showWindow(nextRoute);
    }
  })
  .catch((error) => reportMainProcessError("Unable to start Agw Desktop", error));
