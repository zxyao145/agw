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
  ServerExtensionArchive,
  ServerExtensionCatalog,
  ServerExtensionOrigin,
  ThemeStyleRequest,
  ThemeStyleText,
} from "../../shared/contracts";
import {
  desktopApiVersion,
  EXTENSION_SDK_API_VERSION_CURRENT,
} from "../../shared/extension-manifest";
import { type HostPermission, parseHostPermissions } from "../../shared/extension-host-permissions";
import type { ExtensionPermission } from "@agw/extension-sdk";
import type { ConfirmationService } from "../confirmation/confirmation-service";
import { permissionApprovalView } from "../confirmation/permission-approval";
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
import { packageDigest } from "./extension-package-digest";
import { serviceWorkerRegistrationOutcome } from "./extension-network-rules";
import { ServerExtensionPackageStore } from "./server-extension-package-store";
import type { ExtensionProtocolState } from "./extension-protocol";
import { ExtensionRuntimeReset } from "./extension-runtime-reset";
import { ExtensionSafeModeGuard } from "./extension-safe-mode";
import { ExtensionSessionFactory } from "./extension-session";
import { themeStyleCompiler } from "./extension-theme-style-cache";
import { ExtensionViewManager } from "./extension-view-manager";
import { ReplacedFileRegistry } from "./replaced-file-registry";
import { resolveServerExtensionCatalogEntries } from "./server-extension-catalog-sync";

export type ExtensionRuntimeOptions = {
  userDataDirectory: string;
  appVersion: string;
  sdkDirectory: string;
  /** 随 Desktop 提供的内置 extension 目录，只读。The read-only directory of extensions bundled with Desktop. */
  bundledExtensionsDirectory: string;
  extensionPreloadPath: string;
  settings: DesktopSettingsStore;
  /** 安装与启用的权限批准使用的确认服务。The confirmation service for install and enable approvals. */
  confirmations: ConfirmationService;
  getMainWindow(): BrowserWindow | null;
  onViewAdded?(): void;
  /** extension 设置写入后通知 index.ts 重新读取设置。Tells index.ts to reload settings after an extension settings write. */
  onSettingsChanged(): Promise<void>;
  log(message: string): void;
};

function sameMembers(left: readonly string[], right: readonly string[]): boolean {
  return left.length === right.length && left.every((item) => right.includes(item));
}

type ServerCatalogSyncRequest = {
  origin: ServerExtensionOrigin;
  catalog: ServerExtensionCatalog;
  archives: ServerExtensionArchive[];
  failures?: Array<{ extensionId: string; releaseHash: string; message: string }>;
};

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
  private readonly serverInstances = new Map<string, SourcedCatalogEntry>();
  private readonly serverReleases = new Map<string, SourcedCatalogEntry>();
  private readonly serverCatalogRevisions = new Map<string, number>();
  private readonly serverCatalogInstances = new Map<string, Set<string>>();
  private serverSyncDepth = 0;
  private serverCatalogSyncSucceeded = false;
  private pinnedThemeRelease: { themeId: string; releaseHash: string } | null = null;
  private extensionSettings: Record<string, ExtensionSettings> = {};
  private recoveryNotices: ExtensionRecoveryNotice[] = [];
  /** 已经批准、尚未提交的安装：installId → 批准绑定的包摘要、版本与权限。Approved installs not yet committed: installId → the bound digest, version and grants. */
  private readonly installApprovals = new Map<
    string,
    { digest: string; version: string; grants: ExtensionGrants }
  >();
  private readonly grants = new ExtensionAccessGrants();
  private readonly replacedFiles = new ReplacedFileRegistry();
  private readonly files = new ExtensionFileSystem(this.grants, this.replacedFiles);
  private readonly watcher: ExtensionFileWatcher;
  private readonly http: ExtensionHttpClient;
  private readonly proxies = new Map<string, ExtensionEgressProxy>();
  private readonly sessions: ExtensionSessionFactory;
  private readonly installer: ExtensionInstaller;
  private readonly serverPackages: ServerExtensionPackageStore;

  public constructor(private readonly options: ExtensionRuntimeOptions) {
    const extensionsDirectory = join(options.userDataDirectory, "extensions");
    this.packagesDirectory = join(extensionsDirectory, "packages");
    this.dataDirectory = join(extensionsDirectory, "data");
    this.serverPackages = new ServerExtensionPackageStore(
      join(extensionsDirectory, "server-packages"),
      options.appVersion,
    );
    this.watcher = new ExtensionFileWatcher((event) => this.emit(event), this.replacedFiles);
    this.sessions = new ExtensionSessionFactory({
      sdkDirectory: options.sdkDirectory,
      protocolState: (id, releaseHash) => this.protocolState(id, releaseHash),
      grantedPermissions: (id, webContentsId) => this.grantedPermissions(id, webContentsId),
      hostPermissions: (id, webContentsId) => this.hostPermissions(id, webContentsId),
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
      onViewAdded: options.onViewAdded,
      registry: this.registry,
      sessions: this.sessions,
      preloadPath: options.extensionPreloadPath,
      apiVersion: (id, releaseHash) => desktopApiVersion(this.validEntry(id, releaseHash).manifest),
      hostPermissions: (id, webContentsId) => this.hostPermissions(id, webContentsId),
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
    await this.serverPackages.initialize();
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
      toInstalledExtension(entry, this.extensionSettings[entry.instanceId ?? entry.id]),
    );
  }

  public hasServerExtensionRelease(
    origin: ServerExtensionOrigin,
    extensionId: string,
    releaseHash: string,
  ): Promise<boolean> {
    return this.serverPackages.has(origin, extensionId, releaseHash);
  }

  public async markServerExtensionsUnavailable(hostInstanceId: string): Promise<void> {
    this.registry.assertHost(hostInstanceId);
    for (const [instanceId, entry] of this.serverInstances) {
      if (entry.serverAvailable)
        this.serverInstances.set(instanceId, { ...entry, serverAvailable: false });
    }
    await this.refresh();
  }

  public async syncServerExtensionCatalog(
    hostInstanceId: string,
    request: ServerCatalogSyncRequest,
  ): Promise<boolean> {
    this.serverSyncDepth++;
    let applied: boolean;
    try {
      applied = await this.applyServerExtensionCatalog(hostInstanceId, request);
    } finally {
      this.serverSyncDepth--;
    }
    this.serverCatalogSyncSucceeded ||= applied;
    await this.pruneServerPackages();
    return applied;
  }

  private async applyServerExtensionCatalog(
    hostInstanceId: string,
    request: ServerCatalogSyncRequest,
  ): Promise<boolean> {
    this.registry.assertHost(hostInstanceId);
    if (request.catalog.serverId !== request.origin.serverId) {
      throw new Error("The Server catalog belongs to another Server.");
    }
    if (!Number.isSafeInteger(request.catalog.revision) || request.catalog.revision < 0) {
      throw new Error("The Server catalog revision is invalid.");
    }
    const originKey = JSON.stringify([request.origin.serverId, request.origin.userId]);
    const previousRevision = this.serverCatalogRevisions.get(originKey);
    if (
      previousRevision === request.catalog.revision &&
      request.catalog.extensions.length === 0 &&
      !request.failures?.length
    ) {
      const currentInstances = this.serverCatalogInstances.get(originKey) ?? new Set<string>();
      for (const [instanceId, entry] of this.serverInstances) {
        if (!entry.origin) continue;
        const sameOrigin =
          entry.origin.serverId === request.origin.serverId &&
          entry.origin.userId === request.origin.userId;
        const serverAvailable = sameOrigin && currentInstances.has(instanceId);
        if (entry.serverAvailable !== serverAvailable) {
          this.serverInstances.set(instanceId, { ...entry, serverAvailable });
        }
      }
      await this.refresh();
      return true;
    }

    const resolvedEntries = await resolveServerExtensionCatalogEntries({
      packages: this.serverPackages,
      origin: request.origin,
      catalog: request.catalog,
      archives: request.archives,
      failures: request.failures,
      previousInstances: this.serverInstances,
    });
    const currentInstances = new Set<string>();
    let available = true;
    for (const resolved of resolvedEntries) {
      const key = this.serverReleaseKey(resolved.instanceId, resolved.releaseHash);
      if (resolved.available) {
        this.serverReleases.set(key, resolved.entry);
        currentInstances.add(resolved.instanceId);
      } else available = false;
      this.serverInstances.set(resolved.instanceId, resolved.entry);
    }

    for (const [instanceId, entry] of this.serverInstances) {
      if (
        entry.origin &&
        (!currentInstances.has(instanceId) ||
          entry.origin.serverId !== request.origin.serverId ||
          entry.origin.userId !== request.origin.userId)
      ) {
        this.serverInstances.set(instanceId, { ...entry, serverAvailable: false });
      }
    }
    this.serverCatalogInstances.set(originKey, currentInstances);
    this.serverCatalogRevisions.set(originKey, request.catalog.revision);
    await this.refresh();
    return available;
  }

  public listRecoveryNotices(): ExtensionRecoveryNotice[] {
    return this.recoveryNotices;
  }

  /** 已经安装的版本；只有内置副本时为内置版本。The installed version; the bundled version when only a bundled copy exists. */
  private async installedVersion(extensionId: string): Promise<string | null> {
    await this.refresh();
    return this.catalog.get(extensionId)?.manifest?.version ?? null;
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

  /**
   * 在确认窗口中批准安装：批准绑定暂存包的摘要、版本与权限集合，批准之前复核摘要。
   * Approves an install in the confirmation window: the approval binds the staged package's digest, version and
   * permission set, and the digest is checked again before approval.
   */
  public async approveInstall(installId: string): Promise<boolean> {
    this.installApprovals.delete(installId);
    const { directory, entry } = await this.installer.inspect(installId);
    if (entry.errors.length > 0 || !entry.manifest) {
      throw new Error("An install with errors cannot be approved.");
    }
    const manifest = entry.manifest;
    const digest = await packageDigest(directory);
    const answer = await this.options.confirmations.request(
      permissionApprovalView("install", manifest, await this.installedVersion(manifest.id)),
      {
        owner: this.options.getMainWindow()?.webContents,
        verify: async () => (await packageDigest(directory)) === digest,
      },
    );
    if (answer !== true) return false;
    this.installApprovals.set(installId, {
      digest,
      version: manifest.version,
      grants: declaredGrants(manifest),
    });
    return true;
  }

  public async commitInstall(installId: string): Promise<InstalledExtension> {
    const approval = this.installApprovals.get(installId);
    if (!approval) throw new Error("The install has not been approved.");
    this.installApprovals.delete(installId);
    const entry = await this.installer.commit(installId, async (directory, staged) => {
      const grants = declaredGrants(staged.manifest!);
      if (
        staged.manifest!.version !== approval.version ||
        !sameMembers(grants.permissions, approval.grants.permissions) ||
        !sameMembers(grants.hostPermissions, approval.grants.hostPermissions) ||
        (await packageDigest(directory)) !== approval.digest
      ) {
        throw new Error("The extension package changed after it was approved.");
      }
    });
    await themeStyleCompiler.invalidate(entry.directory);
    await this.refresh();
    return toInstalledExtension(
      this.catalog.get(entry.id) ?? { ...entry, source: "user" },
      this.extensionSettings[entry.instanceId ?? entry.id],
    );
  }

  public cancelInstall(installId: string): Promise<void> {
    this.installApprovals.delete(installId);
    return this.installer.cancel(installId);
  }

  public async uninstall(extensionId: string, deleteData: boolean): Promise<void> {
    const entry = this.catalog.get(extensionId);
    if (!entry) throw new Error(`Extension ${extensionId} is not installed.`);
    if (entry.source === "server") {
      throw new Error("Server-managed extensions are removed by the Server administrator.");
    }
    this.releaseExtension(extensionId);
    await themeStyleCompiler.invalidate(entry.directory);
    if (entry.source === "bundled") await this.installer.removeBundled(extensionId, deleteData);
    else await this.installer.uninstall(extensionId, deleteData);
    await this.refresh();
  }

  /** 显式 reload：重新读取全部依赖后再校验。An explicit reload: re-reads every dependency before validating. */
  public async reloadExtension(extensionId: string): Promise<void> {
    const entry = this.catalog.get(extensionId);
    if (entry) await themeStyleCompiler.invalidate(entry.directory);
    await this.refresh();
  }

  /**
   * 当前选中的 CSS 主题的完整样式文本：主题必须仍然启用、校验通过，且内容标识与描述一致。
   * The complete style text of the selected CSS theme: the theme must still be enabled and valid, and its content id
   * must match the descriptor.
   */
  public async getThemeStyle(request: ThemeStyleRequest): Promise<ThemeStyleText> {
    const separator = request.themeId.indexOf(":");
    const extensionId = request.themeId.slice(0, separator);
    const localId = request.themeId.slice(separator + 1);
    const entry = separator > 0 ? this.catalog.get(extensionId) : undefined;
    if (
      !entry?.manifest ||
      entry.validation !== "valid" ||
      !this.extensionSettings[extensionId]?.enabled
    ) {
      throw new Error(`Theme ${request.themeId} is not available.`);
    }
    const theme = entry.themes.find((item) => item.id === localId);
    const contribution = entry.manifest.contributes?.themes?.find((item) => item.id === localId);
    if (theme?.format !== "css" || !contribution) {
      throw new Error(`Theme ${request.themeId} is not a CSS theme.`);
    }
    const changed = () =>
      new Error(`Theme ${request.themeId} changed after the extension list was read.`);
    if (theme.styleId !== request.styleId) throw changed();
    const style = await themeStyleCompiler.compile(entry.directory, contribution.path);
    if (style.styleId !== request.styleId) throw changed();
    await themeStyleCompiler.pin(entry.directory, contribution.path);
    this.pinnedThemeRelease = entry.releaseHash
      ? { themeId: request.themeId, releaseHash: entry.releaseHash }
      : null;
    return { styleId: style.styleId, cssText: style.cssText };
  }

  /**
   * 启用时在确认窗口中批准 manifest 声明的权限，批准绑定包摘要、版本与权限集合；只有批准结果能保存权限。返回之后是否为启用状态。
   * Enabling approves the permissions the manifest declares in the confirmation window; the approval binds the
   * package digest, version and permission set, and only an approval saves permissions. Returns whether the extension
   * is enabled afterwards.
   */
  public async setEnabled(extensionId: string, enabled: boolean): Promise<boolean> {
    if (!enabled) {
      this.registry.revokeGrants(extensionId);
      this.grants.release(extensionId);
      this.http.releaseExtension(extensionId);
      this.watcher.releaseExtension(extensionId);
      await this.options.settings.setExtensionEnabled(extensionId, false);
      await this.reloadSettings();
      return false;
    }
    const entry = this.validEntry(extensionId);
    const declared = declaredGrants(entry.manifest);
    const digest = await packageDigest(entry.directory);
    const answer = await this.options.confirmations.request(
      permissionApprovalView("enable", entry.manifest, null),
      {
        owner: this.options.getMainWindow()?.webContents,
        verify: async () => {
          const current = this.catalog.get(extensionId);
          if (!current?.manifest || current.validation !== "valid") return false;
          const grants = declaredGrants(current.manifest);
          return (
            current.directory === entry.directory &&
            current.manifest.version === entry.manifest.version &&
            sameMembers(grants.permissions, declared.permissions) &&
            sameMembers(grants.hostPermissions, declared.hostPermissions) &&
            (await packageDigest(entry.directory)) === digest
          );
        },
      },
    );
    if (answer !== true) return false;
    await this.options.settings.setExtensionEnabled(extensionId, true, declared);
    await this.reloadSettings();
    return true;
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
    await this.pruneServerPackages();
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
    const source = join(this.options.sdkDirectory, `v${EXTENSION_SDK_API_VERSION_CURRENT}`);
    await copyFile(join(source, "sdk.d.ts"), join(directory, "sdk.d.ts"));
    await copyFile(join(source, "manifest.schema.json"), join(directory, "manifest.schema.json"));
    return { canceled: false, directory };
  }

  /* ---------- frame 通道 Frame channels ---------- */

  public createFrame(hostInstanceId: string, request: CreateExtensionFrameRequest): void {
    this.registry.assertHost(hostInstanceId);
    const entry = this.validEntry(request.instanceId, request.releaseHash);
    if (entry.id !== request.extensionId)
      throw new Error("The frame extension identity is invalid.");
    if (
      entry.source === "server"
        ? !entry.origin ||
          !this.sameOrigin(entry.origin, request.origin) ||
          entry.releaseHash !== request.releaseHash
        : request.origin !== null || request.releaseHash !== null
    ) {
      throw new Error("The frame source identity is invalid.");
    }
    if (!this.extensionSettings[request.instanceId]?.enabled) {
      throw new Error(`Extension ${request.instanceId} is not enabled.`);
    }
    if (!this.proxies.has(request.instanceId)) {
      throw new Error(`Extension ${request.instanceId} has not begun activation.`);
    }
    this.views.create(request);
    const settings = this.extensionSettings[request.instanceId];
    this.registry.approveGrants(request.frameId, {
      permissions: settings.grantedPermissions,
      hostPermissions: settings.grantedHostPermissions,
    });
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

  public focusFrame(hostInstanceId: string, frameId: string): void {
    this.registry.assertHost(hostInstanceId);
    this.views.focus(frameId);
  }

  public setFrameBackground(hostInstanceId: string, color: string): void {
    this.registry.assertHost(hostInstanceId);
    this.views.setBackground(color);
  }

  public async destroyFrame(hostInstanceId: string, frameId: string): Promise<void> {
    this.registry.assertHost(hostInstanceId);
    this.views.destroy(frameId);
    await this.pruneServerPackages();
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
        { extensionId: caller.instanceId, frameId: caller.frameId, pageId: caller.pageId },
        request.watchId,
        path,
        request.recursive,
      ),
    );
    return null;
  }

  public unwatch(caller: ExtensionCaller, watchId: string): null {
    this.checkCaller(caller);
    this.watcher.remove(caller.instanceId, watchId);
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
    this.checkCaller(caller);
    for (const path of result.filePaths) await this.grants.grantRead(caller.instanceId, path);
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
    this.checkCaller(caller);
    await this.grants.grantWrite(caller.instanceId, result.filePath);
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
    const grants = this.checkCaller(caller);
    return this.http.request(
      { extensionId: caller.instanceId, frameId: caller.frameId, pageId: caller.pageId },
      parseHostPermissions(grants.hostPermissions),
      request,
    );
  }

  /* ---------- 内部 Internal ---------- */

  /** 能力请求需要当前宿主和 frame 的已批准权限。Capability calls require the current host and the frame's approved grants. */
  private checkCaller(caller: ExtensionCaller): ExtensionGrants {
    if (!this.registry.isCurrentHost(caller.hostInstanceId)) {
      throw new ExtensionOperationError(
        "ERR_FRAME_GONE",
        "This extension host is no longer attached.",
      );
    }
    return this.registry.requireGrants(
      caller,
      this.extensionSettings[caller.instanceId]?.enabled === true,
    );
  }

  private accessContext(caller: ExtensionCaller): FileAccessContext {
    const grants = this.checkCaller(caller);
    return {
      extensionId: caller.instanceId,
      grantedPermissions: grants.permissions,
      storageDirectory: join(this.dataDirectory, caller.instanceId),
    };
  }

  private validEntry(
    instanceId: string,
    releaseHash?: string | null,
  ): SourcedCatalogEntry & { manifest: NonNullable<CatalogEntry["manifest"]> } {
    const entry =
      releaseHash === undefined || releaseHash === null
        ? this.catalog.get(instanceId)
        : this.serverReleases.get(this.serverReleaseKey(instanceId, releaseHash));
    if (!entry || entry.validation !== "valid" || !entry.manifest) {
      throw new Error(`Extension instance ${instanceId} is not installed or not valid.`);
    }
    return entry as SourcedCatalogEntry & { manifest: NonNullable<CatalogEntry["manifest"]> };
  }

  private protocolState(
    instanceId: string,
    releaseHash: string | null,
  ): ExtensionProtocolState | null {
    const entry =
      releaseHash === null
        ? this.catalog.get(instanceId)
        : this.serverReleases.get(this.serverReleaseKey(instanceId, releaseHash));
    if (!entry || entry.validation !== "valid" || !entry.manifest) return null;
    const settings = this.extensionSettings[instanceId];
    const retainedGrants = this.registry
      .framesOf(instanceId)
      .find((frame) => frame.releaseHash === releaseHash && frame.approvedGrants)?.approvedGrants;
    return {
      manifest: entry.manifest,
      rootDirectory: entry.directory,
      enabled: settings?.enabled === true || retainedGrants != null,
      grantedHostPermissions:
        retainedGrants?.hostPermissions ??
        (settings?.enabled ? settings.grantedHostPermissions : []),
    };
  }

  private serverReleaseKey(instanceId: string, releaseHash: string): string {
    return instanceId + "\u0000" + releaseHash;
  }

  private sameOrigin(left: ServerExtensionOrigin, right: ServerExtensionOrigin | null): boolean {
    return right !== null && left.serverId === right.serverId && left.userId === right.userId;
  }

  private grantedPermissions(
    extensionId: string,
    webContentsId?: number,
  ): readonly ExtensionPermission[] {
    const frame =
      webContentsId === undefined ? undefined : this.registry.findByWebContents(webContentsId);
    return frame?.instanceId === extensionId ? (frame.approvedGrants?.permissions ?? []) : [];
  }

  private hostPermissions(extensionId: string, webContentsId?: number): readonly HostPermission[] {
    if (webContentsId !== undefined) {
      const frame = this.registry.findByWebContents(webContentsId);
      return parseHostPermissions(
        frame?.instanceId === extensionId ? (frame.approvedGrants?.hostPermissions ?? []) : [],
      );
    }
    const settings = this.extensionSettings[extensionId];
    return parseHostPermissions([
      ...new Set([
        ...(settings?.enabled ? settings.grantedHostPermissions : []),
        ...this.registry
          .framesOf(extensionId)
          .flatMap((frame) => frame.approvedGrants?.hostPermissions ?? []),
      ]),
    ]);
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
    const entries = mergeExtensionCatalogs(bundled, user, settings.removedBundledExtensions, [
      ...this.serverInstances.values(),
    ]);
    this.catalog = new Map(entries.map((entry) => [entry.instanceId ?? entry.id, entry]));
    // 内置 extension 的声明视为已批准；用户 extension 有新增项时停用等待批准。
    // Bundled declarations count as approved; user extensions with new entries are disabled until approved.
    const declared: Record<"bundled" | "user" | "server", Map<string, ExtensionGrants>> = {
      bundled: new Map(),
      user: new Map(),
      server: new Map(),
    };
    for (const entry of entries) {
      if (entry.validation === "valid" && entry.manifest) {
        declared[entry.source].set(entry.instanceId ?? entry.id, declaredGrants(entry.manifest));
      }
    }
    await this.options.settings.applyBundledExtensions(declared.bundled);
    await this.options.settings.reconcileExtensionGrants(
      new Map([...declared.user, ...declared.server]),
    );
    await this.reloadSettings();
  }

  private async reloadSettings(): Promise<void> {
    const settings = await this.options.settings.load();
    this.extensionSettings = settings.extensions;
    if (this.pinnedThemeRelease?.themeId !== settings.appearance.theme)
      this.pinnedThemeRelease = null;
    await this.options.onSettingsChanged();
  }

  private async pruneServerPackages(): Promise<void> {
    if (this.serverSyncDepth > 0 || !this.serverCatalogSyncSucceeded) return;
    const retainedKeys = new Set<string>();
    const retainedHashes = new Set<string>();
    for (const instanceIds of this.serverCatalogInstances.values()) {
      for (const instanceId of instanceIds) {
        const hash = this.serverInstances.get(instanceId)?.releaseHash;
        if (hash) {
          retainedKeys.add(this.serverReleaseKey(instanceId, hash));
          retainedHashes.add(hash);
        }
      }
    }
    for (const frame of this.registry.all()) {
      if (frame.releaseHash) {
        retainedKeys.add(this.serverReleaseKey(frame.instanceId, frame.releaseHash));
        retainedHashes.add(frame.releaseHash);
      }
    }
    if (this.pinnedThemeRelease) retainedHashes.add(this.pinnedThemeRelease.releaseHash);
    for (const [key, entry] of this.serverReleases) {
      if (!retainedKeys.has(key) && entry.releaseHash !== this.pinnedThemeRelease?.releaseHash)
        this.serverReleases.delete(key);
    }
    await this.serverPackages.prune(retainedHashes);
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
    if (window && !window.isDestroyed()) {
      const instanceId = event.instanceId ?? event.extensionId;
      const extensionId = this.catalog.get(instanceId)?.id ?? event.extensionId;
      window.webContents.send("agw:extension-event", { ...event, instanceId, extensionId });
    }
  }
}
