import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { createServer } from "node:net";
import { createServer as createHttpServer } from "node:http";
import { resolve } from "node:path";
import test from "node:test";

import type { DesktopRuntimeState } from "./runtime-model";
import { connectServer } from "./server-connection";

async function fixture(t: test.TestContext, kind: "local" | "remote" = "local") {
  await mkdir(".scratch", { recursive: true });
  const directory = await mkdtemp(resolve(".scratch/connection-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const server = createServer();
  await new Promise<void>((done) => server.listen(0, "127.0.0.1", done));
  const address = server.address();
  assert.ok(address && typeof address !== "string");
  await new Promise<void>((done) => server.close(() => done()));
  const state: DesktopRuntimeState = {
    isDesktop: true,
    platform: "darwin",
    architecture: "arm64",
    appVersion: "1.0.0",
    packageFlavor: "full",
    activeToken: null,
    localServerRuntime: null,
    extensionSafeMode: null,
    settings: {
      schemaVersion: 1,
      packageFlavor: "full",
      closeBehavior: "minimize-to-tray",
      activeServerId: "server",
      projectTabsByServer: {},
      extensions: {},
      removedBundledExtensions: [],
      fileViewerDefaults: {},
      appearance: { baseFontSize: 16, chatFontSize: null, codeFontSize: null, theme: "default" },
      profiles: [
        {
          id: "server",
          kind,
          name: "Server",
          baseUrl: `http://127.0.0.1:${address.port}`,
          apiMajorVersion: 1,
          allowInsecureHttp: true,
        },
      ],
    },
  };
  const file = resolve(directory, "state.json");
  await writeFile(file, JSON.stringify(state));
  const readState = async () => JSON.parse(await readFile(file, "utf8")) as DesktopRuntimeState;
  return { state, readState, server, port: address.port };
}

test("connectServer_LocalUnavailable_WaitsThirtySeconds", async (t) => {
  const { state, readState } = await fixture(t);
  const started = performance.now();
  await assert.rejects(connectServer(state, readState, new AbortController().signal), /30 seconds/);
  assert.ok(performance.now() - started >= 29_900);
  assert.ok(performance.now() - started < 33_000);
});

test("connectServer_RemoteUnavailable_FailsImmediately", async (t) => {
  const { state, readState } = await fixture(t, "remote");
  const started = performance.now();
  await assert.rejects(connectServer(state, readState, new AbortController().signal));
  assert.ok(performance.now() - started < 1_000);
});

test("connectServer_CancelledDuringRetry_StopsWaiting", async (t) => {
  const { state, readState } = await fixture(t);
  const controller = new AbortController();
  const pending = connectServer(state, readState, controller.signal);
  const timer = setTimeout(() => controller.abort(), 100);
  t.after(() => clearTimeout(timer));
  await assert.rejects(pending, { name: "AbortError" });
});

test("connectServer_UnresponsiveSocket_RetriesAndCancels", async (t) => {
  const { state, readState, server, port } = await fixture(t);
  let connections = 0;
  const sockets = new Set<import("node:net").Socket>();
  server.on("connection", (socket) => {
    connections++;
    sockets.add(socket);
    socket.on("close", () => sockets.delete(socket));
  });
  await new Promise<void>((done) => server.listen(port, "127.0.0.1", done));
  t.after(async () => {
    for (const socket of sockets) socket.destroy();
    await new Promise<void>((done) => server.close(() => done()));
  });
  const controller = new AbortController();
  const pending = connectServer(state, readState, controller.signal);
  const timer = setTimeout(() => controller.abort(), 4_500);
  t.after(() => clearTimeout(timer));
  await assert.rejects(pending, { name: "AbortError" });
  assert.ok(connections >= 2);
});

test("connectServer_ResponseConnectionReset_RetriesUntilCancelled", async (t) => {
  const { state, readState, port } = await fixture(t);
  let requests = 0;
  const server = createHttpServer((_request, response) => {
    requests++;
    response.writeHead(200, { "content-type": "application/json", "content-length": "100" });
    response.flushHeaders();
    setTimeout(() => response.destroy(), 20);
  });
  await new Promise<void>((done) => server.listen(port, "127.0.0.1", done));
  t.after(async () => {
    server.closeAllConnections();
    await new Promise<void>((done) => server.close(() => done()));
  });
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 1_500);
  t.after(() => clearTimeout(timer));
  await assert.rejects(connectServer(state, readState, controller.signal), { name: "AbortError" });
  assert.ok(requests >= 2);
});

for (const scenario of [
  {
    name: "HttpFailure",
    status: 503,
    body: JSON.stringify({ title: "Server unavailable" }),
    error: /Server unavailable/,
  },
  { name: "MalformedJson", status: 200, body: "invalid JSON", error: SyntaxError },
]) {
  test(`connectServer_${scenario.name}_FailsImmediately`, async (t) => {
    const { state, readState, port } = await fixture(t);
    let requests = 0;
    const server = createHttpServer((_request, response) => {
      requests++;
      response.writeHead(scenario.status, { "content-type": "application/json" });
      response.end(scenario.body);
    });
    await new Promise<void>((done) => server.listen(port, "127.0.0.1", done));
    t.after(async () => {
      server.closeAllConnections();
      await new Promise<void>((done) => server.close(() => done()));
    });
    await assert.rejects(
      connectServer(state, readState, new AbortController().signal),
      scenario.error,
    );
    assert.equal(requests, 1);
  });
}
