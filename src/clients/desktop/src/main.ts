import { existsSync, lstatSync, readFileSync } from "node:fs";
import { rm } from "node:fs/promises";
import { homedir } from "node:os";
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

import type { DesktopRuntimeState, UninstallRequest, UninstallResult } from "./desktop-contract";
import { DaemonManager } from "./daemon/daemon-manager";
import { createLocalDesktopToken } from "./local-token";
import { readLocalServerRuntime } from "./local-server-runtime";
import { resolveRendererFile } from "./renderer-path";
import { resolveServerExecutablePath } from "./server-executable-path";
import {
  DesktopSettingsStore,
  type DesktopSettings,
  type PackageFlavor,
  type SecretCodec,
} from "./settings-store";

protocol.registerSchemesAsPrivileged([
  {
    scheme: "agw",
    privileges: { standard: true, secure: true, supportFetchAPI: true, corsEnabled: true },
  },
]);

if (started) app.quit();

let mainWindow: BrowserWindow | null = null;
let tray: Tray | null = null;
let isQuitting = false;
let activeTaskCount = 0;
let currentSettings: DesktopSettings;
let settingsStore: DesktopSettingsStore;
let daemonManager: DaemonManager;

function readPackageFlavor(): PackageFlavor {
  if (!app.isPackaged) return process.env.AGW_PACKAGE_FLAVOR === "client" ? "client" : "full";
  try {
    const value = JSON.parse(
      readFileSync(join(process.resourcesPath, "package-flavor.json"), "utf8"),
    ) as { packageFlavor?: string };
    return value.packageFlavor === "client" ? "client" : "full";
  } catch {
    return "full";
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
    : resolve(__dirname, "..", "resources", "renderer");
}

function trayIconPath(): string {
  return app.isPackaged
    ? join(process.resourcesPath, "assets", "tray-icon.svg")
    : resolve(__dirname, "..", "assets", "tray-icon.svg");
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

async function loadRenderer(pathname = "/desktop/chat/"): Promise<void> {
  if (!mainWindow) return;
  const developmentUrl = process.env.AGW_RENDERER_URL;
  await mainWindow.loadURL(
    developmentUrl ? new URL(pathname, developmentUrl).toString() : `agw://app${pathname}`,
  );
}

function createMainWindow(): BrowserWindow {
  const window = new BrowserWindow({
    width: 1440,
    height: 920,
    minWidth: 980,
    minHeight: 680,
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
      preload: join(__dirname, "preload.js"),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });

  window.webContents.setWindowOpenHandler(() => ({ action: "deny" }));
  window.webContents.on("will-navigate", (event, target) => {
    if (!isTrustedRenderer(target)) event.preventDefault();
  });
  window.once("ready-to-show", () => window.show());
  window.on("close", (event) => {
    if (!isQuitting && currentSettings.closeBehavior === "minimize-to-tray") {
      event.preventDefault();
      window.hide();
    }
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
      { label: "Open Agw Chat", click: () => void showWindow("/desktop/chat/") },
      {
        label: activeTaskCount === 1 ? "1 active task" : `${activeTaskCount} active tasks`,
        enabled: false,
      },
      { type: "separator" },
      { label: "Settings", click: () => void showWindow("/settings/") },
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
  tray.on("click", () => void showWindow("/desktop/chat/"));
  rebuildTrayMenu();
}

async function showWindow(pathname?: string): Promise<void> {
  if (!mainWindow) mainWindow = createMainWindow();
  if (pathname) await loadRenderer(pathname);
  mainWindow.show();
  mainWindow.focus();
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

  const updateExecutable = join(dirname(dirname(process.execPath)), "Update.exe");
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

function registerIpc(): void {
  ipcMain.handle("agw:get-runtime-state", async (event) => {
    assertTrustedSender(senderUrl(event));
    return runtimeState();
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
});

app.on("activate", () => void showWindow());

void app.whenReady().then(async () => {
  const flavor = readPackageFlavor();
  settingsStore = new DesktopSettingsStore(app.getPath("userData"), flavor, createSecretCodec());
  currentSettings = await settingsStore.load();
  daemonManager = new DaemonManager(process.platform, serverExecutablePath());

  registerStaticProtocol();
  registerIpc();
  mainWindow = createMainWindow();
  createTray();

  try {
    if (flavor === "full" && (await daemonManager.isServerBundled())) await daemonManager.install();
    if (flavor === "client") await daemonManager.uninstall();
  } catch (error) {
    dialog.showErrorBox(
      "Agw Server daemon",
      error instanceof Error ? error.message : String(error),
    );
  }

  await loadRenderer();
});
