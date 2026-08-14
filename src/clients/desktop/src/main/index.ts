import { existsSync, lstatSync, readFileSync } from "node:fs";
import { rm } from "node:fs/promises";
import { homedir, release } from "node:os";
import { basename, dirname, join, resolve } from "node:path";
import { pathToFileURL } from "node:url";

import {
  app,
  BrowserWindow,
  dialog,
  ipcMain,
  Menu,
  nativeImage,
  net,
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
  UninstallRequest,
  UninstallResult,
} from "../shared/contracts";
import { DaemonManager } from "./daemon/daemon-manager";
import { DESKTOP_OAUTH_PROTOCOL, findOAuthDeepLink, parseOAuthDeepLink } from "./oauth-deep-link";
import { parseDesktopPackageMetadata, type DesktopPackageMetadata } from "./package-metadata";
import { resolveRendererFile } from "./renderer-path";
import {
  appendRendererEvent,
  createRendererEventRecord,
  RendererRecoveryGuard,
} from "./renderer-recovery";
import { createLocalDesktopToken } from "./runtime/local-token";
import { readLocalServerRuntime } from "./runtime/local-server-runtime";
import { resolveServerExecutablePath } from "./runtime/server-executable-path";
import { DesktopSettingsStore, type SecretCodec } from "./settings/settings-store";
import { checkForDesktopUpdate } from "./update/github-release-updater";

protocol.registerSchemesAsPrivileged([
  {
    scheme: "agw",
    privileges: { standard: true, secure: true, supportFetchAPI: true, corsEnabled: true },
  },
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
const rendererRecoveryGuard = new RendererRecoveryGuard();

function reportMainProcessError(title: string, error: unknown): void {
  console.error(title, error);
  dialog.showErrorBox(title, error instanceof Error ? error.message : String(error));
}

function readPackageMetadata(): DesktopPackageMetadata {
  if (!app.isPackaged) {
    return parseDesktopPackageMetadata({
      packageFlavor: process.env.AGW_PACKAGE_FLAVOR === "client" ? "client" : "full",
      appVersion: process.env.AGW_RELEASE_VERSION || app.getVersion(),
    });
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
    localServerRuntime: await readLocalServerRuntime(),
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

async function loadTrustedRendererUrl(target: string): Promise<void> {
  if (!mainWindow) return;
  if (!isTrustedRenderer(target)) {
    throw new Error("Refusing to load an untrusted renderer URL.");
  }
  rendererRecoveryGuard.markLoadStarted();
  lastTrustedRendererUrl = target;
  await mainWindow.loadURL(target);
}

function writeRendererEvent(
  event: "render-process-gone" | "did-fail-load" | "unresponsive" | "responsive",
  reason: string,
  url: string,
  exitCode?: number,
): void {
  const record = createRendererEventRecord(
    { event, reason, pathname: url, ...(exitCode === undefined ? {} : { exitCode }) },
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

async function reloadRendererWindow(): Promise<void> {
  const target =
    lastTrustedRendererUrl ??
    (process.env.AGW_RENDERER_URL
      ? new URL("/desktop/chat/", process.env.AGW_RENDERER_URL).toString()
      : "agw://app/desktop/chat/");
  await loadTrustedRendererUrl(target);
}

async function showManualRendererRecovery(window: BrowserWindow): Promise<void> {
  if (window.isDestroyed()) return;
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
    return;
  }

  rendererReloadRequired = true;
  window.hide();
}

async function recoverRenderer(window: BrowserWindow): Promise<void> {
  if (isQuitting || window.isDestroyed() || rendererRecoveryInProgress) return;
  rendererRecoveryInProgress = true;
  const action = rendererRecoveryGuard.recordFailure();
  try {
    if (action === "auto-reload") {
      try {
        await reloadRendererWindow();
        return;
      } catch {
        // A failed automatic navigation immediately hands control to manual recovery.
      }
    }
    await showManualRendererRecovery(window);
  } finally {
    rendererRecoveryInProgress = false;
  }
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

  window.webContents.setWindowOpenHandler(() => ({ action: "deny" }));
  window.webContents.on("will-navigate", (event, target) => {
    if (!isTrustedRenderer(target)) event.preventDefault();
  });
  window.webContents.on("did-navigate", (_event, target) => {
    if (isTrustedRenderer(target)) lastTrustedRendererUrl = target;
  });
  window.webContents.on("did-finish-load", () => {
    const target = window.webContents.getURL();
    if (isTrustedRenderer(target)) lastTrustedRendererUrl = target;
    rendererRecoveryGuard.markLoadSucceeded();
  });
  window.webContents.on("render-process-gone", (_event, details) => {
    const target = window.webContents.getURL() || lastTrustedRendererUrl || "agw://app/";
    writeRendererEvent("render-process-gone", details.reason, target, details.exitCode);
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
    writeRendererEvent(
      "unresponsive",
      "unresponsive",
      window.webContents.getURL() || lastTrustedRendererUrl || "",
    );
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

function showWindowSafely(pathname?: string): void {
  void showWindow(pathname).catch((error) =>
    reportMainProcessError("Unable to open Agw Desktop", error),
  );
}

function handleOAuthDeepLink(value: string): boolean {
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
    const dataRoot = resolve(homedir(), "agw");
    if (basename(dataRoot) !== "agw" || dirname(dataRoot) !== resolve(homedir())) {
      throw new Error("Refusing to remove an unexpected data directory.");
    }
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
  ipcMain.handle("agw:save-settings", async (event, settings: DesktopSettings) => {
    assertTrustedSender(senderUrl(event));
    await settingsStore.save(settings);
    currentSettings = await settingsStore.load();
    return runtimeState();
  });
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
    const url = new URL(value);
    if (url.protocol !== "https:" && url.protocol !== "http:") {
      throw new Error("Agw Desktop can only open HTTP(S) external URLs.");
    }
    await shell.openExternal(url.toString());
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

app.on("before-quit", () => {
  isQuitting = true;
  rendererRecoveryGuard.dispose();
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
    daemonManager = new DaemonManager(process.platform, serverExecutablePath());

    registerStaticProtocol();
    registerIpc();
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
    await loadRenderer(initialRoute);
    rendererReady = true;
    if (pendingOAuthRoute) {
      const nextRoute = pendingOAuthRoute;
      pendingOAuthRoute = null;
      await showWindow(nextRoute);
    }
  })
  .catch((error) => reportMainProcessError("Unable to start Agw Desktop", error));
