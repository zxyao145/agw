import { chmod, copyFile, mkdir } from "node:fs/promises";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { type BrowserWindow, dialog, type IpcMainEvent, session, shell } from "electron";

import type {
  CreateExtensionFrameRequest,
  ExtensionActivation,
  ExtensionCaller,
  ExtensionEvent,
  ExtensionFileSystemRequest,
  ExtensionFrameLayoutUpdate,
  ExtensionFrameObscureRequest,
  ExtensionFrameSnapshot,
  ExtensionGrants,
  ExtensionHttpRequest,
  ExtensionHttpResult,
  ExtensionInstallPreview,
  ExtensionInstallSource,
  ExtensionOpenDialogOptions,
  ExtensionRecoveryNotice,
  ExtensionSaveDialogOptions,
  ExtensionSettings,
  ExtensionWatchRequest,
  InstalledExtension,
  SaveExtensionSdkTypesResult,
} from "../../shared/contracts";
import { EXTENSION_MANIFEST_VERSION_CURRENT } from "../../shared/extension-manifest";
import { type HostPermission, parseHostPermissions } from "../../shared/extension-host-permissions";
import type { ExtensionPermission } from "@agw/extension-sdk";
import type { DesktopSettingsStore } from "../settings/settings-store";
import { ExtensionAccessGrants } from "./extension-access-grants";
import {
  type CatalogEntry,
  declaredGrants,
  mergeExtensionCatalogs,
  scanExtensionDirectory,
  type SourcedCatalogEntry,
  toInstalledExtension,
} from "./extension-catalog";
import { ExtensionEgressProxy } from "./extension-egress-proxy";
import { type FileAccessContext, ExtensionFileSystem } from "./extension-file-system";
import { ExtensionFileWatcher } from "./extension-file-watcher";
import { ExtensionFrameRegistry } from "./extension-frame-registry";
import { ExtensionHttpClient } from "./extension-http";
import { ExtensionInstaller } from "./extension-installer";
import { ExtensionOperationError } from "./extension-ipc-result";
import { serviceWorkerRegistrationOutcome } from "./extension-network-rules";
import type { ExtensionProtocolState } from "./extension-protocol";
import { ExtensionRuntimeReset } from "./extension-runtime-reset";
import { ExtensionSafeModeGuard } from "./extension-safe-mode";
import { ExtensionSessionFactory } from "./extension-session";
import { ExtensionViewManager } from "./extension-view-manager";
import { ReplacedFileRegistry } from "./replaced-file-registry";

export type ExtensionRuntimeOptions = {
  userDataDirectory: string;
  appVersion: string;
  sdkDirectory: string;
  /** 随 Desktop 提供的内置 extension 目录，只读。The read-only directory of extensions bundled with Desktop. */
  bundledExtensionsDirectory: string;
  extensionPreloadPath: string;
  settings: DesktopSettingsStore;
  getMainWindow(): BrowserWindow | null;
  /** extension 设置写入后通知 index.ts 重新读取设置。Tells index.ts to reload settings after an extension settings write. */
  onSettingsChanged(): Promise<void>;
  log(message: string): void;
};

function sameMembers(left: readonly string[], right: readonly string[]): boolean {
  return left.length === right.length && left.every((item) => right.includes(item));
}

/** Electron 主进程中的 extension 运行时。The extension runtime in the Electron main process. */
export class ExtensionRuntime {
  /** 用户安装的 extension 包：`<userData>/extensions/packages/<id>`。User-installed packages. */
  public readonly packagesDirectory: string;
  /** extension 的持久数据：`<userData>/extensions/data/<id>`。Persistent extension data. */
  public readonly dataDirectory: string;
  public readonly safeMode = new ExtensionSafeModeGuard();
  public readonly registry = new ExtensionFrameRegistry();
  public readonly views: ExtensionViewManager;
  public readonly reset: ExtensionRuntimeReset;

  private catalog = new Map<string, SourcedCatalogEntry>();
  private extensionSettings: Record<string, ExtensionSettings> = {};
  private recoveryNotices: ExtensionRecoveryNotice[] = [];
  private readonly grants = new ExtensionAccessGrants();
  private readonly replacedFiles = new ReplacedFileRegistry();
  private readonly files = new ExtensionFileSystem(this.grants, this.replacedFiles);
  private readonly watcher: ExtensionFileWatcher;
  private readonly http: ExtensionHttpClient;
  private readonly proxies = new Map<string, ExtensionEgressProxy>();
  private readonly sessions: ExtensionSessionFactory;
  private readonly installer: ExtensionInstaller;

  public constructor(private readonly options: ExtensionRuntimeOptions) {
    const extensionsDirectory = join(options.userDataDirectory, "extensions");
    this.packagesDirectory = join(extensionsDirectory, "packages");
    this.dataDirectory = join(extensionsDirectory, "data");
    this.watcher = new ExtensionFileWatcher((event) => this.emit(event), this.replacedFiles);
    this.sessions = new ExtensionSessionFactory({
      sdkDirectory: options.sdkDirectory,
      protocolState: (id) => this.protocolState(id),
      grantedPermissions: (id) => this.grantedPermissions(id),
      hostPermissions: (id) => this.hostPermissions(id),
      onRequestBlocked: (event) => this.emit(event),
      onServiceWorkerRegistered: (id, scope) => void this.serviceWorkerRegistered(id, scope),
    });
    this.http = new ExtensionHttpClient((id) => this.sessions.httpTransportFor(id));
    this.installer = new ExtensionInstaller({
      packagesDirectory: this.packagesDirectory,
      dataDirectory: this.dataDirectory,
      appVersion: options.appVersion,
      settings: options.settings,
      clearSessionData: (id) => this.sessions.clearData(id),
      log: options.log,
    });
    this.views = new ExtensionViewManager({
      getMainWindow: options.getMainWindow,
      registry: this.registry,
      sessions: this.sessions,
      preloadPath: options.extensionPreloadPath,
      manifestVersion: (id) => this.validEntry(id).manifest.manifestVersion,
      hostPermissions: (id) => this.hostPermissions(id),
      emit: (event) => this.emit(event),
      onPageReplaced: (frame, pageId) => {
        this.watcher.releasePage(frame.frameId, pageId);
        this.http.releasePage(frame.frameId, pageId);
      },
    });
    this.reset = new ExtensionRuntimeReset({
      destroyAllFrames: () => this.views.destroyAll(),
      releaseAllResources: () => {
        this.watcher.releaseAll();
        this.http.releaseAll();
        this.grants.releaseAll();
      },
      closeAllProxies: () => this.closeAllProxies(),
      clearActivations: () => this.safeMode.clearActivations(),
      clearHost: () => this.registry.clearHost(),
    });
  }

  /** 启动时先恢复未完成的安装与卸载，再扫描。At startup, recover unfinished installs and uninstalls, then scan. */
  public async initialize(): Promise<void> {
    this.recoveryNotices = await this.installer.recover();
    await this.refresh();
  }

  /* ---------- 管理通道 Management channels ---------- */

  public async attachHost(hostInstanceId: string): Promise<void> {
    const state = this.registry.attachState(hostInstanceId);
    if (state === "unchanged") return;
    if (state === "reset-required") await this.reset.run();
    this.registry.setHost(hostInstanceId);
  }

  public async listExtensions(): Promise<InstalledExtension[]> {
    await this.refresh();
    return [...this.catalog.values()].map((entry) =>
      toInstalledExtension(entry, this.extensionSettings[entry.id]),
    );
  }

  public listRecoveryNotices(): ExtensionRecoveryNotice[] {
    return this.recoveryNotices;
  }

  public async prepareInstall(source: ExtensionInstallSource): Promise<ExtensionInstallPreview> {
    const window = this.options.getMainWindow();
    const options: Electron.OpenDialogOptions =
      source === "directory"
        ? { title: "Install extension from folder", properties: ["openDirectory"] }
        : {
            title: "Install extension from .zip",
            properties: ["openFile"],
            filters: [{ name: "Extension package", extensions: ["zip"] }],
          };
    const result = window
      ? await dialog.showOpenDialog(window, options)
      : await dialog.showOpenDialog(options);
    if (result.canceled || result.filePaths.length === 0) return { canceled: true };
    const prepared =
      source === "directory"
        ? await this.installer.prepareFromDirectory(result.filePaths[0])
        : await this.installer.prepareFromArchive(result.filePaths[0]);
    // 只有内置副本时，确认弹窗显示内置 extension 的版本。With only a bundled copy, the confirmation shows the bundled version.
    const bundled = prepared.manifest ? this.catalog.get(prepared.manifest.id) : undefined;
    const installedVersion =
      prepared.installedVersion ??
      (bundled?.source === "bundled" ? (bundled.manifest?.version ?? null) : null);
    return { canceled: false, ...prepared, installedVersion };
  }

  public async commitInstall(installId: string): Promise<InstalledExtension> {
    const entry = await this.installer.commit(installId);
    await this.refresh();
    return toInstalledExtension(
      this.catalog.get(entry.id) ?? { ...entry, source: "user" },
      this.extensionSettings[entry.id],
    );
  }

  public cancelInstall(installId: string): Promise<void> {
    return this.installer.cancel(installId);
  }

  public async uninstall(extensionId: string, deleteData: boolean): Promise<void> {
    const entry = this.catalog.get(extensionId);
    if (!entry) throw new Error(`Extension ${extensionId} is not installed.`);
    this.releaseExtension(extensionId);
    if (entry.source === "bundled") await this.installer.removeBundled(extensionId, deleteData);
    else await this.installer.uninstall(extensionId, deleteData);
    await this.refresh();
  }

  public async setEnabled(
    extensionId: string,
    enabled: boolean,
    grants?: ExtensionGrants,
  ): Promise<void> {
    if (enabled) {
      const entry = this.validEntry(extensionId);
      const declared = declaredGrants(entry.manifest);
      if (
        !grants ||
        !sameMembers(grants.permissions, declared.permissions) ||
        !sameMembers(grants.hostPermissions, declared.hostPermissions)
      ) {
        throw new Error("The approved grants must equal the manifest declaration.");
      }
      await this.options.settings.setExtensionEnabled(extensionId, true, declared);
    } else {
      this.http.releaseExtension(extensionId);
      this.watcher.releaseExtension(extensionId);
      await this.options.settings.setExtensionEnabled(extensionId, false);
    }
    await this.reloadSettings();
  }

  public async beginActivation(
    hostInstanceId: string,
    extensionId: string,
  ): Promise<ExtensionActivation> {
    this.registry.assertHost(hostInstanceId);
    this.validEntry(extensionId);
    if (!this.extensionSettings[extensionId]?.enabled)
      throw new Error(`Extension ${extensionId} is not enabled.`);
    this.safeMode.beginActivation(extensionId);
    const storageDirectory = join(this.dataDirectory, extensionId);
    await mkdir(storageDirectory, { recursive: true, mode: 0o700 });
    await chmod(storageDirectory, 0o700);
    await this.proxies.get(extensionId)?.close();
    const proxy = new ExtensionEgressProxy({
      getPermissions: () => this.hostPermissions(extensionId),
      resolveUpstream: (url) => session.defaultSession.resolveProxy(url),
      onBlocked: (event) => this.emit({ type: "request-blocked", extensionId, ...event }),
    });
    this.proxies.set(extensionId, proxy);
    await this.sessions.setProxy(extensionId, await proxy.start());
    return { storageDirectory };
  }

  public endActivation(hostInstanceId: string, extensionId: string): void {
    this.registry.assertHost(hostInstanceId);
    this.safeMode.endActivation(extensionId);
  }

  public async recordActivation(
    hostInstanceId: string,
    extensionId: string,
    version: string,
  ): Promise<void> {
    this.registry.assertHost(hostInstanceId);
    await this.options.settings.recordExtensionActivation(extensionId, version);
    await this.reloadSettings();
  }

  public releaseFrameResources(hostInstanceId: string, extensionId: string, frameId: string): void {
    this.registry.assertHost(hostInstanceId);
    this.watcher.releaseFrame(extensionId, frameId);
    this.http.releaseFrame(extensionId, frameId);
  }

  public async releaseResources(hostInstanceId: string, extensionId: string): Promise<void> {
    this.registry.assertHost(hostInstanceId);
    this.releaseExtension(extensionId);
    const proxy = this.proxies.get(extensionId);
    this.proxies.delete(extensionId);
    await proxy?.close();
  }

  public async openDirectory(directory: string): Promise<void> {
    await mkdir(directory, { recursive: true });
    const error = await shell.openPath(directory);
    if (error) throw new Error(error);
  }

  public cancelHttpRequest(requestId: string): void {
    this.http.cancel(requestId);
  }

  public async saveSdkTypes(): Promise<SaveExtensionSdkTypesResult> {
    const window = this.options.getMainWindow();
    const options: Electron.OpenDialogOptions = {
      title: "Save SDK types",
      properties: ["openDirectory", "createDirectory"],
    };
    const result = window
      ? await dialog.showOpenDialog(window, options)
      : await dialog.showOpenDialog(options);
    if (result.canceled || result.filePaths.length === 0) return { canceled: true };
    const directory = result.filePaths[0];
    const source = join(this.options.sdkDirectory, `v${EXTENSION_MANIFEST_VERSION_CURRENT}`);
    await copyFile(join(source, "sdk.d.ts"), join(directory, "sdk.d.ts"));
    await copyFile(join(source, "manifest.schema.json"), join(directory, "manifest.schema.json"));
    return { canceled: false, directory };
  }

  /* ---------- frame 通道 Frame channels ---------- */

  public createFrame(hostInstanceId: string, request: CreateExtensionFrameRequest): void {
    this.registry.assertHost(hostInstanceId);
    this.validEntry(request.extensionId);
    if (!this.extensionSettings[request.extensionId]?.enabled) {
      throw new Error(`Extension ${request.extensionId} is not enabled.`);
    }
    if (!this.proxies.has(request.extensionId)) {
      throw new Error(`Extension ${request.extensionId} has not begun activation.`);
    }
    this.views.create(request);
  }

  public updateFrameLayout(
    hostInstanceId: string,
    updates: readonly ExtensionFrameLayoutUpdate[],
  ): void {
    this.registry.assertHost(hostInstanceId);
    this.views.updateLayout(updates);
  }

  public obscureFrames(
    hostInstanceId: string,
    frameIds: readonly string[],
  ): Promise<ExtensionFrameSnapshot[]> {
    this.registry.assertHost(hostInstanceId);
    return this.views.obscure(frameIds);
  }

  public hideObscuredFrames(
    hostInstanceId: string,
    requests: readonly ExtensionFrameObscureRequest[],
  ): void {
    this.registry.assertHost(hostInstanceId);
    this.views.hideObscured(requests);
  }

  public revealFrames(hostInstanceId: string, frameIds: readonly string[]): void {
    this.registry.assertHost(hostInstanceId);
    this.views.reveal(frameIds);
  }

  public setFrameBackground(hostInstanceId: string, color: string): void {
    this.registry.assertHost(hostInstanceId);
    this.views.setBackground(color);
  }

  public destroyFrame(hostInstanceId: string, frameId: string): void {
    this.registry.assertHost(hostInstanceId);
    this.views.destroy(frameId);
  }

  public terminateFrameProcess(hostInstanceId: string, frameId: string): void {
    this.registry.assertHost(hostInstanceId);
    this.views.terminateProcess(frameId);
  }

  public openFrameDevTools(hostInstanceId: string, frameId: string): void {
    this.registry.assertHost(hostInstanceId);
    this.views.openDevTools(frameId);
  }

  public handleHello(event: IpcMainEvent, payload: unknown): void {
    this.views.handleHello(event, payload);
  }

  public handleCspViolation(event: IpcMainEvent, payload: unknown): void {
    this.views.handleCspViolation(event, payload);
  }

  /** 某个 frame 当前持有的监听与 HTTP 请求数量。Watchers and HTTP requests a frame currently holds. */
  public frameResources(
    frameId: string,
    pageId?: string,
  ): { watchers: number; httpRequests: number } {
    const owned = (owner: { frameId: string; pageId: string }) =>
      owner.frameId === frameId && (pageId === undefined || owner.pageId === pageId);
    return { watchers: this.watcher.count(owned), httpRequests: this.http.count(owned) };
  }

  /** 正在运行的出站代理端口。Ports of the running egress proxies. */
  public egressProxyPorts(): number[] {
    return [...this.proxies.values()].map((proxy) => proxy.port);
  }

  /* ---------- 能力通道 Capability channels ---------- */

  public fileSystem<R extends ExtensionFileSystemRequest>(caller: ExtensionCaller, request: R) {
    return this.files.execute(this.accessContext(caller), request);
  }

  public async watch(caller: ExtensionCaller, request: ExtensionWatchRequest): Promise<null> {
    const context = this.accessContext(caller);
    await this.files.withReadAccess(context, request.uri, (path) =>
      this.watcher.add(
        { extensionId: caller.extensionId, frameId: caller.frameId, pageId: caller.pageId },
        request.watchId,
        path,
        request.recursive,
      ),
    );
    return null;
  }

  public unwatch(caller: ExtensionCaller, watchId: string): null {
    this.checkCaller(caller);
    this.watcher.remove(caller.extensionId, watchId);
    return null;
  }

  public async showOpenDialog(
    caller: ExtensionCaller,
    options: ExtensionOpenDialogOptions,
  ): Promise<string[]> {
    this.checkCaller(caller);
    const properties: Electron.OpenDialogOptions["properties"] = [
      options.directory ? "openDirectory" : "openFile",
    ];
    if (options.multiple) properties.push("multiSelections");
    const dialogOptions: Electron.OpenDialogOptions = {
      title: options.title,
      properties,
      filters: options.filters,
    };
    const window = this.options.getMainWindow();
    const result = window
      ? await dialog.showOpenDialog(window, dialogOptions)
      : await dialog.showOpenDialog(dialogOptions);
    if (result.canceled) return [];
    for (const path of result.filePaths) await this.grants.grantRead(caller.extensionId, path);
    return result.filePaths.map((path) => pathToFileURL(path).toString());
  }

  public async showSaveDialog(
    caller: ExtensionCaller,
    options: ExtensionSaveDialogOptions,
  ): Promise<string | null> {
    this.checkCaller(caller);
    const dialogOptions: Electron.SaveDialogOptions = {
      title: options.title,
      defaultPath: options.defaultName,
      filters: options.filters,
    };
    const window = this.options.getMainWindow();
    const result = window
      ? await dialog.showSaveDialog(window, dialogOptions)
      : await dialog.showSaveDialog(dialogOptions);
    if (result.canceled || !result.filePath) return null;
    await this.grants.grantWrite(caller.extensionId, result.filePath);
    return pathToFileURL(result.filePath).toString();
  }

  public async reveal(caller: ExtensionCaller, uri: string): Promise<null> {
    await this.files.withReadAccess(this.accessContext(caller), uri, async (path) =>
      shell.showItemInFolder(path),
    );
    return null;
  }

  public httpRequest(
    caller: ExtensionCaller,
    request: ExtensionHttpRequest,
  ): Promise<ExtensionHttpResult> {
    this.checkCaller(caller);
    return this.http.request(
      { extensionId: caller.extensionId, frameId: caller.frameId, pageId: caller.pageId },
      this.hostPermissions(caller.extensionId),
      request,
    );
  }

  /* ---------- 内部 Internal ---------- */

  /** 能力通道的检查顺序：宿主登记 → 已启用 → frame 与页面。Capability checks: host registration, enabled, frame and page. */
  private checkCaller(caller: ExtensionCaller): void {
    if (!this.registry.isCurrentHost(caller.hostInstanceId)) {
      throw new ExtensionOperationError(
        "ERR_FRAME_GONE",
        "This extension host is no longer attached.",
      );
    }
    const entry = this.catalog.get(caller.extensionId);
    if (
      !entry ||
      entry.validation !== "valid" ||
      !this.extensionSettings[caller.extensionId]?.enabled
    ) {
      throw new ExtensionOperationError(
        "ERR_EXTENSION_DISABLED",
        `Extension ${caller.extensionId} is not enabled.`,
      );
    }
    this.registry.validateCaller(caller);
  }

  private accessContext(caller: ExtensionCaller): FileAccessContext {
    this.checkCaller(caller);
    return {
      extensionId: caller.extensionId,
      grantedPermissions: this.grantedPermissions(caller.extensionId),
      storageDirectory: join(this.dataDirectory, caller.extensionId),
    };
  }

  private validEntry(
    extensionId: string,
  ): CatalogEntry & { manifest: NonNullable<CatalogEntry["manifest"]> } {
    const entry = this.catalog.get(extensionId);
    if (!entry || entry.validation !== "valid" || !entry.manifest) {
      throw new Error(`Extension ${extensionId} is not installed or not valid.`);
    }
    return entry as CatalogEntry & { manifest: NonNullable<CatalogEntry["manifest"]> };
  }

  private protocolState(extensionId: string): ExtensionProtocolState | null {
    const entry = this.catalog.get(extensionId);
    if (!entry || entry.validation !== "valid" || !entry.manifest) return null;
    const settings = this.extensionSettings[extensionId];
    return {
      manifest: entry.manifest,
      rootDirectory: entry.directory,
      enabled: settings?.enabled ?? false,
      grantedHostPermissions: settings?.enabled ? settings.grantedHostPermissions : [],
    };
  }

  private grantedPermissions(extensionId: string): readonly ExtensionPermission[] {
    const settings = this.extensionSettings[extensionId];
    return settings?.enabled ? settings.grantedPermissions : [];
  }

  private hostPermissions(extensionId: string): readonly HostPermission[] {
    const settings = this.extensionSettings[extensionId];
    return settings?.enabled ? parseHostPermissions(settings.grantedHostPermissions) : [];
  }

  private releaseExtension(extensionId: string): void {
    this.http.releaseExtension(extensionId);
    this.watcher.releaseExtension(extensionId);
    this.grants.release(extensionId);
    this.views.destroyExtension(extensionId);
  }

  private async closeAllProxies(): Promise<void> {
    const proxies = [...this.proxies.values()];
    this.proxies.clear();
    await Promise.all(proxies.map((proxy) => proxy.close()));
  }

  private async refresh(): Promise<void> {
    const [bundled, user, settings] = await Promise.all([
      scanExtensionDirectory(
        this.options.bundledExtensionsDirectory,
        this.options.appVersion,
        "bundled",
      ),
      scanExtensionDirectory(this.packagesDirectory, this.options.appVersion, "user"),
      this.options.settings.load(),
    ]);
    const entries = mergeExtensionCatalogs(bundled, user, settings.removedBundledExtensions);
    this.catalog = new Map(entries.map((entry) => [entry.id, entry]));
    // 内置 extension 的声明视为已批准；用户 extension 有新增项时停用等待批准。
    // Bundled declarations count as approved; user extensions with new entries are disabled until approved.
    const declared: Record<"bundled" | "user", Map<string, ExtensionGrants>> = {
      bundled: new Map(),
      user: new Map(),
    };
    for (const entry of entries) {
      if (entry.validation === "valid" && entry.manifest)
        declared[entry.source].set(entry.id, declaredGrants(entry.manifest));
    }
    await this.options.settings.applyBundledExtensions(declared.bundled);
    await this.options.settings.reconcileExtensionGrants(declared.user);
    await this.reloadSettings();
  }

  private async reloadSettings(): Promise<void> {
    this.extensionSettings = (await this.options.settings.load()).extensions;
    await this.options.onSettingsChanged();
  }

  private async serviceWorkerRegistered(extensionId: string, scope: string): Promise<void> {
    const outcome = serviceWorkerRegistrationOutcome(scope);
    this.options.log(`Extension ${extensionId}: ${outcome.message}`);
    await this.sessions.clearServiceWorkers(extensionId);
    this.emit({
      type: "extension-failed",
      extensionId,
      code: outcome.code,
      message: outcome.message,
    });
  }

  private emit(event: ExtensionEvent): void {
    const window = this.options.getMainWindow();
    if (window && !window.isDestroyed()) window.webContents.send("agw:extension-event", event);
  }
}
