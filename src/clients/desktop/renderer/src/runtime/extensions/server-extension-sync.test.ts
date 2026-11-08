import assert from "node:assert/strict";
import { once } from "node:events";
import { createServer } from "node:http";
import type { AddressInfo } from "node:net";
import { PerformanceObserver, performance } from "node:perf_hooks";
import test from "node:test";
import { JSDOM } from "jsdom";

import { configureApiRuntime, resetApiRuntime } from "@agw/api";
import type { HostContext } from "@agw/extension-sdk";

import { ContextService } from "./context-service";
import { ExtensionHost } from "./extension-host";
import {
  SERVER_EXTENSION_SYNC_METRIC_NAME,
  ServerExtensionSync,
  ServerExtensionSyncClient,
} from "./server-extension-sync";

const app: HostContext["app"] = { version: "0.5.0", platform: "darwin", architecture: "arm64" };
const workspace = {
  sidebarVisible: true,
  activeSidebarViewId: "agw.conversations",
  activeMainViewId: "agw.chat",
};

type SyncMetricEntry = PerformanceEntry & { detail: { result: string } };

function observeSyncMetrics() {
  const queued: SyncMetricEntry[] = [];
  const waiting: ((entry: SyncMetricEntry) => void)[] = [];
  const observer = new PerformanceObserver((list) => {
    for (const entry of list.getEntriesByName(SERVER_EXTENSION_SYNC_METRIC_NAME)) {
      const metric = entry as SyncMetricEntry;
      const resolve = waiting.shift();
      if (resolve) resolve(metric);
      else queued.push(metric);
    }
  });
  observer.observe({ entryTypes: ["measure"] });

  return {
    next(): Promise<SyncMetricEntry> {
      const existing = queued.shift();
      if (existing) return Promise.resolve(existing);
      return new Promise((resolve, reject) => {
        let receive!: (entry: SyncMetricEntry) => void;
        const timeout = setTimeout(() => {
          const index = waiting.indexOf(receive);
          if (index >= 0) waiting.splice(index, 1);
          reject(new Error("Timed out waiting for a Server extension sync measurement."));
        }, 1_000);
        receive = (entry) => {
          clearTimeout(timeout);
          resolve(entry);
        };
        waiting.push(receive);
      });
    },
    disconnect(): void {
      observer.disconnect();
    },
  };
}

test("the host publishes and clears Server extension sync failures", () => {
  const host = new ExtensionHost({
    bridge: null,
    app,
    notify: () => undefined,
  });
  const notifications: string[] = [];
  host.subscribe(() => notifications.push(host.getSnapshot().serverSyncError ?? ""));

  host.setServerSyncError("The package could not be verified.");
  assert.equal(host.getSnapshot().serverSyncError, "The package could not be verified.");
  host.setServerSyncError(null);

  assert.equal(host.getSnapshot().serverSyncError, null);
  assert.deepEqual(notifications, ["The package could not be verified.", ""]);
});

test("sync measures success and failure", { timeout: 5_000 }, async () => {
  let initialized = false;
  let failCatalog = false;
  let delayServerInfo = false;
  let expectNextServerInfoRequest = false;
  let serverInfoRequests = 0;
  let signalServerInfoRequest: (() => void) | null = null;
  const serverInfoRequest = new Promise<void>((resolve) => {
    signalServerInfoRequest = resolve;
  });
  let signalNextServerInfoRequest: (() => void) | null = null;
  const nextServerInfoRequest = new Promise<void>((resolve) => {
    signalNextServerInfoRequest = resolve;
  });
  const server = createServer((request, response) => {
    const url = new URL(request.url ?? "/", "http://localhost");
    response.setHeader("content-type", "application/json");
    if (url.pathname === "/api/server-info") {
      serverInfoRequests += 1;
      if (expectNextServerInfoRequest) {
        expectNextServerInfoRequest = false;
        signalNextServerInfoRequest?.();
      }
      const body = JSON.stringify({
        code: 0,
        title: "ok",
        data: { serverId: initialized ? "server-a" : null },
      });
      if (delayServerInfo) {
        delayServerInfo = false;
        signalServerInfoRequest?.();
        const timer = setTimeout(() => response.end(body), 50);
        response.on("close", () => clearTimeout(timer));
        return;
      }
      response.end(body);
      return;
    }
    if (url.pathname === "/api/extensions/catalog") {
      if (failCatalog) {
        response.writeHead(500);
        response.end(JSON.stringify({ code: 5000001, title: "Catalog failed" }));
        return;
      }
      const body = JSON.stringify({
        code: 0,
        title: "ok",
        data: { serverId: "server-a", revision: 1, extensions: [] },
      });
      response.end(body);
      return;
    }
    response.writeHead(404).end();
  });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const baseUrl = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
  const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "http://localhost/" });
  const previousWindow = Object.getOwnPropertyDescriptor(globalThis, "window");
  const previousDocument = Object.getOwnPropertyDescriptor(globalThis, "document");
  Object.defineProperty(globalThis, "window", { configurable: true, value: dom.window });
  Object.defineProperty(globalThis, "document", { configurable: true, value: dom.window.document });
  const host = new ExtensionHost({
    bridge: null,
    app,
    notify: () => undefined,
  });
  const identity = { profileId: "profile-1", baseUrl, token: "token-1" };
  host.context.setConnection(identity, { id: "profile-1", name: "Remote", kind: "remote" });
  host.context.setUser(host.context.connectionKey!, {
    id: "user-1",
    displayName: "Ada",
    loginProvider: null,
    isAdmin: false,
  });
  configureApiRuntime({ baseUrl, token: identity.token });
  performance.clearMeasures(SERVER_EXTENSION_SYNC_METRIC_NAME);
  const metrics = observeSyncMetrics();
  let sync = new ServerExtensionSync(host);

  try {
    sync.start();
    await sync.refresh();
    const success = await metrics.next();
    assert.equal(success.name, SERVER_EXTENSION_SYNC_METRIC_NAME);
    assert.equal(success.detail.result, "success");
    assert.ok(Number.isFinite(success.duration) && success.duration >= 0);

    const requestsBeforeNavigation = serverInfoRequests;
    host.context.setWorkspace({ ...workspace, activeMainViewId: "agw.files" });
    const context = host.context.snapshot();
    host.context.setSelection(host.context.connectionKey!, {
      project: context.project ?? null,
      conversation: context.conversation ?? null,
      target: context.target ?? null,
    });
    await new Promise<void>((resolve) => setTimeout(resolve, 50));
    assert.equal(serverInfoRequests, requestsBeforeNavigation);

    initialized = true;
    failCatalog = true;

    await sync.refresh(true);
    assert.equal(host.getSnapshot().serverSyncError, "Catalog failed");
    const failure = await metrics.next();
    assert.equal(failure.name, SERVER_EXTENSION_SYNC_METRIC_NAME);
    assert.equal(failure.detail.result, "failure");
    assert.ok(Number.isFinite(failure.duration) && failure.duration >= 0);

    await new Promise<void>((resolve) => setImmediate(resolve));
    failCatalog = false;
    delayServerInfo = true;
    const cancellation = sync.refresh(true);
    await serverInfoRequest;
    const requestsBeforeSwitch = serverInfoRequests;
    initialized = false;
    expectNextServerInfoRequest = true;
    host.context.setConnection(
      { ...identity, token: "token-2" },
      { id: "profile-1", name: "Remote", kind: "remote" },
    );
    host.context.setUser(host.context.connectionKey!, {
      id: "user-1",
      displayName: "Ada",
      loginProvider: null,
      isAdmin: false,
    });
    configureApiRuntime({ baseUrl, token: "token-2" });
    await cancellation;
    const refreshedAfterSwitch = await Promise.race([
      nextServerInfoRequest.then(() => true),
      new Promise<boolean>((resolve) => setTimeout(() => resolve(false), 250)),
    ]);
    assert.equal(
      refreshedAfterSwitch,
      true,
      "the new Server context should refresh after the old request ends",
    );
    assert.equal(serverInfoRequests, requestsBeforeSwitch + 1);
    const cancelled = await metrics.next();
    assert.equal(cancelled.detail.result, "cancelled");
    assert.ok(Number.isFinite(cancelled.duration) && cancelled.duration >= 0);
    const contextRefresh = await metrics.next();
    assert.equal(contextRefresh.detail.result, "success");
    assert.equal(host.getSnapshot().serverSyncError, null);
    assert.equal(performance.getEntriesByName(SERVER_EXTENSION_SYNC_METRIC_NAME).length, 1);
    assert.deepEqual(Object.keys(cancelled.detail), ["result"]);
  } finally {
    sync.dispose();
    metrics.disconnect();
    performance.clearMeasures(SERVER_EXTENSION_SYNC_METRIC_NAME);
    resetApiRuntime();
    server.close();
    await once(server, "close");
    dom.window.close();
    if (previousWindow) Object.defineProperty(globalThis, "window", previousWindow);
    else Reflect.deleteProperty(globalThis, "window");
    if (previousDocument) Object.defineProperty(globalThis, "document", previousDocument);
    else Reflect.deleteProperty(globalThis, "document");
  }
});

test(
  "a Server sync error remains visible while recovery is pending",
  { timeout: 5_000 },
  async () => {
    let serverId: string | null = "server-a";
    let failCatalog = true;
    let holdNextServerInfo = false;
    const releaseServerInfo: (() => void)[] = [];
    let signalServerInfo: (() => void) | null = null;
    const serverInfoStarted = new Promise<void>((resolve) => {
      signalServerInfo = resolve;
    });
    const server = createServer((request, response) => {
      const url = new URL(request.url ?? "/", "http://localhost");
      response.setHeader("content-type", "application/json");
      if (url.pathname === "/api/server-info") {
        const body = JSON.stringify({ code: 0, title: "ok", data: { serverId } });
        if (holdNextServerInfo) {
          holdNextServerInfo = false;
          signalServerInfo?.();
          releaseServerInfo.push(() => response.end(body));
          return;
        }
        response.end(body);
        return;
      }
      if (url.pathname === "/api/extensions/catalog" && failCatalog) {
        response.writeHead(500);
        response.end(JSON.stringify({ code: 5000001, title: "Catalog failed" }));
        return;
      }
      response.writeHead(404).end();
    });
    server.listen(0, "127.0.0.1");
    await once(server, "listening");
    const baseUrl = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
    const dom = new JSDOM("<!doctype html><html><body></body></html>", {
      url: "http://localhost/",
    });
    const previousWindow = Object.getOwnPropertyDescriptor(globalThis, "window");
    const previousDocument = Object.getOwnPropertyDescriptor(globalThis, "document");
    Object.defineProperty(globalThis, "window", { configurable: true, value: dom.window });
    Object.defineProperty(globalThis, "document", {
      configurable: true,
      value: dom.window.document,
    });
    const host = new ExtensionHost({ bridge: null, app, notify: () => undefined });
    const identity = { profileId: "profile-1", baseUrl, token: "token-1" };
    host.context.setConnection(identity, { id: "profile-1", name: "Remote", kind: "remote" });
    host.context.setUser(host.context.connectionKey!, {
      id: "user-1",
      displayName: "Ada",
      loginProvider: null,
      isAdmin: false,
    });
    configureApiRuntime({ baseUrl, token: identity.token });
    const sync = new ServerExtensionSync(host);
    let signalFailure: (() => void) | null = null;
    const failurePublished = new Promise<void>((resolve) => {
      signalFailure = resolve;
    });
    const unsubscribe = host.subscribe(() => {
      if (host.getSnapshot().serverSyncError !== null) signalFailure?.();
    });

    try {
      sync.start();
      await failurePublished;
      assert.equal(host.getSnapshot().serverSyncError, "Catalog failed");
      await new Promise<void>((resolve) => setImmediate(resolve));
      failCatalog = false;
      serverId = null;
      holdNextServerInfo = true;
      const recovery = sync.refresh(true);
      const recoveryStarted = await Promise.race([
        serverInfoStarted.then(() => true),
        new Promise<boolean>((resolve) => setTimeout(() => resolve(false), 250)),
      ]);
      assert.equal(recoveryStarted, true, "the recovery request should start");

      assert.equal(host.getSnapshot().serverSyncError, "Catalog failed");

      releaseServerInfo[0]?.();
      await recovery;
      assert.equal(host.getSnapshot().serverSyncError, null);
    } finally {
      unsubscribe();
      sync.dispose();
      releaseServerInfo[0]?.();
      resetApiRuntime();
      server.close();
      await once(server, "close");
      dom.window.close();
      if (previousWindow) Object.defineProperty(globalThis, "window", previousWindow);
      else Reflect.deleteProperty(globalThis, "window");
      if (previousDocument) Object.defineProperty(globalThis, "document", previousDocument);
      else Reflect.deleteProperty(globalThis, "document");
    }
  },
);

test("fetchCatalog sends auth, records ServerId and uses revision responses", async () => {
  const requests: { url: string; authorization: string | undefined }[] = [];
  const archive = Buffer.from([0x50, 0x4b, 0x03, 0x04, 0x01]);
  const server = createServer((request, response) => {
    const url = new URL(request.url ?? "/", "http://localhost");
    requests.push({ url: url.pathname + url.search, authorization: request.headers.authorization });
    response.setHeader("content-type", "application/json");
    if (url.pathname === "/api/server-info") {
      response.end(JSON.stringify({ code: 0, title: "ok", data: { serverId: "deployment-1" } }));
      return;
    }
    if (url.pathname === "/api/extensions/catalog") {
      const unchanged = url.searchParams.get("afterRevision") === "7";
      response.end(
        JSON.stringify({
          code: 0,
          title: "ok",
          data: {
            serverId: "deployment-1",
            revision: 7,
            extensions: unchanged
              ? []
              : [
                  {
                    id: "019a4c17-5f00-7a80-8b3d-2b732f48a100",
                    name: "calendar",
                    displayName: "Calendar",
                    version: "1.0.0",
                    releaseHash: "a".repeat(64),
                    packageHash: "a".repeat(64),
                    manifest: {},
                  },
                ],
          },
        }),
      );
      return;
    }
    if (url.pathname === "/api/extensions/package") {
      response.writeHead(200, { "content-type": "application/zip" });
      response.end(archive);
      return;
    }
    response.writeHead(404).end();
  });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const baseUrl = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
  const context = new ContextService(app, workspace);
  const identity = { profileId: "profile-1", baseUrl, token: "token-1" };
  context.setConnection(identity, { id: "profile-1", name: "Remote", kind: "remote" });
  context.setUser(context.connectionKey!, {
    id: "user-1",
    displayName: "Ada",
    loginProvider: null,
    isAdmin: false,
  });
  configureApiRuntime({ baseUrl, token: identity.token });

  try {
    const sync = new ServerExtensionSyncClient(context);
    const change = await sync.fetchCatalog();
    assert.ok(change);
    assert.deepEqual(change.origin, { serverId: "deployment-1", userId: "user-1" });
    assert.equal(change.catalog.revision, 7);
    assert.equal(context.snapshot().server.serverId, "deployment-1");

    const downloaded = await sync.downloadPackage(change, change.catalog.extensions[0]!);
    assert.deepEqual(downloaded.bytes, new Uint8Array(archive));
    sync.markSynchronized(change);

    const unchanged = await sync.fetchCatalog();
    assert.ok(unchanged);
    assert.deepEqual(unchanged.catalog.extensions, []);
    sync.forgetSynchronized(unchanged);
    const retried = await sync.fetchCatalog();
    assert.ok(retried);
    assert.equal(retried.catalog.extensions.length, 1);
    assert.deepEqual(
      requests.map((request) => request.url),
      [
        "/api/server-info",
        "/api/extensions/catalog",
        `/api/extensions/package?extensionId=019a4c17-5f00-7a80-8b3d-2b732f48a100&releaseHash=${"a".repeat(64)}`,
        "/api/server-info",
        "/api/extensions/catalog?afterRevision=7",
        "/api/server-info",
        "/api/extensions/catalog",
      ],
    );
    assert.ok(requests.every((request) => request.authorization === "Bearer token-1"));
  } finally {
    resetApiRuntime();
    server.close();
    await once(server, "close");
  }
});

test("downloadPackage refuses a catalog from the previous account", async () => {
  const context = new ContextService(app, workspace);
  const identity = {
    profileId: "profile-1",
    baseUrl: "https://server.example.test",
    token: "token-1",
  };
  context.setConnection(identity, { id: "profile-1", name: "Remote", kind: "remote" });
  context.setUser(context.connectionKey!, {
    id: "user-1",
    displayName: "Ada",
    loginProvider: null,
    isAdmin: false,
  });
  context.setServerId(context.connectionKey!, "deployment-1");
  const sync = new ServerExtensionSyncClient(context);
  const change = {
    connectionKey: context.connectionKey!,
    origin: { serverId: "deployment-1", userId: "user-1" },
    catalog: { serverId: "deployment-1", revision: 1, extensions: [] },
  };
  context.setConnection(
    { ...identity, token: "token-2" },
    { id: "profile-1", name: "Remote", kind: "remote" },
  );

  assert.equal(sync.isCurrent(change), false);
  await assert.rejects(
    sync.downloadPackage(change, {
      id: "019a4c17-5f00-7a80-8b3d-2b732f48a100",
      name: "calendar",
      displayName: "Calendar",
      version: "1.0.0",
      releaseHash: "a".repeat(64),
      packageHash: "a".repeat(64),
      manifest: {} as never,
    }),
    { code: "ERR_CONNECTION_CHANGED" },
  );
});
