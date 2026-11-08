import { apiDownload, apiGet } from "@agw/api";

import type {
  ServerExtensionCatalog,
  ServerExtensionCatalogRelease,
  ServerExtensionOrigin,
} from "@desktop/shared/contracts";

import { connectionChangedError, type ContextService } from "./context-service";
import type { ExtensionHost } from "./extension-host";

type ServerInfoResponse = { serverId: string | null };
type ServerExtensionPackageFailure = {
  extensionId: string;
  releaseHash: string;
  message: string;
};

export type ServerExtensionCatalogChange = {
  connectionKey: string;
  origin: ServerExtensionOrigin;
  catalog: ServerExtensionCatalog;
};

export type DownloadedServerExtensionPackage = {
  extensionId: string;
  releaseHash: string;
  bytes: Uint8Array;
};

export const SERVER_EXTENSION_SYNC_INTERVAL_MS = 15_000;
export const SERVER_EXTENSION_SYNC_METRIC_NAME = "agw.desktop.extensions.server_sync";

type ServerExtensionSyncResult = "success" | "failure" | "cancelled";

function syncErrorMessage(error: unknown): string {
  if (typeof error === "object" && error !== null && "body" in error) {
    const body = error.body;
    if (
      typeof body === "object" &&
      body !== null &&
      "title" in body &&
      typeof body.title === "string"
    ) {
      return body.title;
    }
  }
  return error instanceof Error ? error.message : String(error);
}

function serverInfoResponse(value: unknown): ServerInfoResponse {
  if (typeof value !== "object" || value === null || !("serverId" in value)) {
    throw new Error("The Server information response is invalid.");
  }
  const serverId = value.serverId;
  if (serverId !== null && (typeof serverId !== "string" || serverId.trim() === "")) {
    throw new Error("The Server information response contains an invalid ServerId.");
  }
  return { serverId };
}

function catalogResponse(value: unknown, serverId: string): ServerExtensionCatalog {
  if (typeof value !== "object" || value === null) {
    throw new Error("The Server extension catalog response is invalid.");
  }
  const catalog = value as Partial<ServerExtensionCatalog>;
  if (
    catalog.serverId !== serverId ||
    !Number.isSafeInteger(catalog.revision) ||
    (catalog.revision ?? -1) < 0 ||
    !Array.isArray(catalog.extensions)
  ) {
    throw new Error("The Server extension catalog identity or revision is invalid.");
  }
  return catalog as ServerExtensionCatalog;
}

function originKey(origin: ServerExtensionOrigin): string {
  return JSON.stringify([origin.serverId, origin.userId]);
}

export class ServerExtensionSyncClient {
  private readonly revisions = new Map<string, number>();

  public constructor(private readonly context: ContextService) {}

  public async fetchCatalog(force = false): Promise<ServerExtensionCatalogChange | null> {
    const connectionKey = this.context.connectionKey;
    const userId = this.context.snapshot().user?.id;
    if (!connectionKey || !userId) return null;

    return this.context.request(async (signal) => {
      const serverInfo = serverInfoResponse(await apiGet("/api/server-info", { signal }));
      this.context.setServerId(connectionKey, serverInfo.serverId);
      if (serverInfo.serverId === null) return null;

      const origin = { serverId: serverInfo.serverId, userId };
      const previousRevision = force ? undefined : this.revisions.get(originKey(origin));
      const catalog = catalogResponse(
        await apiGet("/api/extensions/catalog", {
          params: {
            query: previousRevision === undefined ? {} : { afterRevision: previousRevision },
          },
          signal,
        }),
        serverInfo.serverId,
      );
      return { connectionKey, origin, catalog };
    });
  }

  public async downloadPackage(
    change: ServerExtensionCatalogChange,
    release: ServerExtensionCatalogRelease,
  ): Promise<DownloadedServerExtensionPackage> {
    if (!this.isCurrent(change)) throw connectionChangedError();
    const bytes = await this.context.request((signal) =>
      apiDownload("/api/extensions/package", {
        params: {
          query: { extensionId: release.id, releaseHash: release.releaseHash },
        },
        signal,
      }),
    );
    if (!this.isCurrent(change)) throw connectionChangedError();
    return { extensionId: release.id, releaseHash: release.releaseHash, bytes };
  }

  public isCurrent(change: ServerExtensionCatalogChange): boolean {
    const context = this.context.snapshot();
    return (
      this.context.connectionKey === change.connectionKey &&
      context.server.serverId === change.origin.serverId &&
      context.user?.id === change.origin.userId
    );
  }

  public markSynchronized(change: ServerExtensionCatalogChange): void {
    if (this.isCurrent(change)) {
      this.revisions.set(originKey(change.origin), change.catalog.revision);
    }
  }

  public forgetSynchronized(change: ServerExtensionCatalogChange): void {
    if (this.isCurrent(change)) this.revisions.delete(originKey(change.origin));
  }
}

export class ServerExtensionSync {
  private readonly client: ServerExtensionSyncClient;
  private started = false;
  private pending: Promise<void> | null = null;
  private forceAfterPending = false;
  private refreshAfterContextChange = false;
  private observedContextKey: string;
  private observedServerId: string | null;
  private fetchingCatalog = false;
  private extensionsUnavailable = false;
  private unavailablePromise: Promise<void> | null = null;
  private timer: ReturnType<typeof setInterval> | null = null;
  private unsubscribeContext: (() => void) | null = null;

  public constructor(
    private readonly host: Pick<
      ExtensionHost,
      | "context"
      | "hasServerExtensionRelease"
      | "syncServerExtensionCatalog"
      | "markServerExtensionsUnavailable"
      | "setServerSyncError"
    >,
  ) {
    this.client = new ServerExtensionSyncClient(host.context);
    this.observedContextKey = this.contextKey();
    this.observedServerId = host.context.snapshot().server.serverId ?? null;
  }

  public start(): void {
    if (this.started) return;
    this.started = true;
    this.unsubscribeContext = this.host.context.subscribe(() => this.onContextChanged());
    this.timer = setInterval(() => this.schedule(), SERVER_EXTENSION_SYNC_INTERVAL_MS);
    window.addEventListener("focus", this.onFocus);
    document.addEventListener("visibilitychange", this.onVisibilityChange);
    if (!this.hasServerContext()) {
      void this.invalidateServerExtensions().catch((error: unknown) => {
        this.host.setServerSyncError(syncErrorMessage(error));
        console.error("Unable to mark Server extensions unavailable.", error);
      });
    }
    this.schedule();
  }

  public dispose(): void {
    this.started = false;
    this.unsubscribeContext?.();
    this.unsubscribeContext = null;
    if (this.timer !== null) clearInterval(this.timer);
    this.timer = null;
    window.removeEventListener("focus", this.onFocus);
    document.removeEventListener("visibilitychange", this.onVisibilityChange);
  }

  public refresh(force = false): Promise<void> {
    if (!this.started) return Promise.resolve();
    if (this.pending) {
      this.forceAfterPending ||= force;
      return this.pending;
    }

    this.pending = this.synchronizeWithMetrics(force)
      .catch(async (error: unknown) => {
        try {
          await this.invalidateServerExtensions();
        } catch (invalidationError) {
          this.host.setServerSyncError(syncErrorMessage(invalidationError));
          console.error("Unable to mark Server extensions unavailable.", invalidationError);
          return;
        }
        if ((error as { code?: unknown } | null)?.code !== "ERR_CONNECTION_CHANGED") {
          this.host.setServerSyncError(syncErrorMessage(error));
          console.error("Unable to synchronize Server extensions.", error);
        }
      })
      .finally(() => this.finishPending());
    return this.pending;
  }

  private readonly onFocus = (): void => this.schedule();

  private readonly onVisibilityChange = (): void => {
    if (document.visibilityState === "visible") this.schedule();
  };

  private schedule(): void {
    void this.refresh();
  }

  private onContextChanged(): void {
    const nextContextKey = this.contextKey();
    const nextServerId = this.host.context.snapshot().server.serverId ?? null;
    const contextChanged = nextContextKey !== this.observedContextKey;
    const serverChanged = nextServerId !== this.observedServerId && !this.fetchingCatalog;
    this.observedContextKey = nextContextKey;
    this.observedServerId = nextServerId;
    if (contextChanged || serverChanged) {
      if (this.pending) this.refreshAfterContextChange = true;
      void this.invalidateServerExtensions().catch((error: unknown) => {
        this.host.setServerSyncError(syncErrorMessage(error));
        console.error("Unable to mark Server extensions unavailable.", error);
      });
      this.schedule();
    }
  }

  private contextKey(): string {
    return JSON.stringify([
      this.host.context.connectionKey,
      this.host.context.snapshot().user?.id ?? null,
    ]);
  }

  private hasServerContext(): boolean {
    return this.host.context.connectionKey !== null && this.host.context.snapshot().user !== null;
  }

  private invalidateServerExtensions(force = false): Promise<void> {
    if (!force && this.extensionsUnavailable) return this.unavailablePromise ?? Promise.resolve();
    this.extensionsUnavailable = true;
    this.unavailablePromise = this.host
      .markServerExtensionsUnavailable()
      .catch((error: unknown) => {
        this.extensionsUnavailable = false;
        this.unavailablePromise = null;
        throw error;
      });
    return this.unavailablePromise;
  }

  private async synchronizeWithMetrics(force: boolean): Promise<void> {
    const startedAt = performance.now();
    let result: ServerExtensionSyncResult = "success";
    try {
      result = await this.synchronize(force);
    } catch (error) {
      result =
        (error as { code?: unknown } | null)?.code === "ERR_CONNECTION_CHANGED"
          ? "cancelled"
          : "failure";
      throw error;
    } finally {
      performance.clearMeasures(SERVER_EXTENSION_SYNC_METRIC_NAME);
      performance.measure(SERVER_EXTENSION_SYNC_METRIC_NAME, {
        start: startedAt,
        end: performance.now(),
        detail: { result },
      });
    }
  }

  private finishPending(): void {
    this.pending = null;
    const refreshAfterPending = this.forceAfterPending || this.refreshAfterContextChange;
    this.forceAfterPending = false;
    this.refreshAfterContextChange = false;
    if (!this.started || !refreshAfterPending) return;
    void this.refresh(true);
  }

  private async synchronize(force: boolean): Promise<ServerExtensionSyncResult> {
    if (this.unavailablePromise) await this.unavailablePromise;
    const hasConnection = this.hasServerContext();
    this.fetchingCatalog = true;
    let change: ServerExtensionCatalogChange | null;
    try {
      change = await this.client.fetchCatalog(force);
    } finally {
      this.fetchingCatalog = false;
    }
    if (change === null) {
      await this.invalidateServerExtensions();
      if (hasConnection) this.host.setServerSyncError(null);
      return "success";
    }

    const archives: DownloadedServerExtensionPackage[] = [];
    const failures: ServerExtensionPackageFailure[] = [];
    for (const release of change.catalog.extensions) {
      if (!this.started) return "cancelled";
      const cached = await this.host.hasServerExtensionRelease(
        change.origin,
        release.id,
        release.releaseHash,
      );
      if (!cached) {
        try {
          archives.push(await this.client.downloadPackage(change, release));
        } catch (error) {
          if (
            (error as { code?: unknown } | null)?.code === "ERR_CONNECTION_CHANGED" ||
            !this.client.isCurrent(change)
          ) {
            throw connectionChangedError();
          }
          failures.push({
            extensionId: release.id,
            releaseHash: release.releaseHash,
            message: syncErrorMessage(error),
          });
        }
      }
    }
    if (!this.started || !this.client.isCurrent(change)) return "cancelled";
    const applied = await this.host.syncServerExtensionCatalog({
      origin: change.origin,
      catalog: change.catalog,
      archives,
      failures,
    });
    if (!this.started || !this.client.isCurrent(change)) {
      await this.invalidateServerExtensions(true);
      return "cancelled";
    }
    if (!applied) this.client.forgetSynchronized(change);
    else if (failures.length === 0) this.client.markSynchronized(change);
    this.extensionsUnavailable = false;
    this.unavailablePromise = null;
    this.host.setServerSyncError(null);
    return "success";
  }
}
