import assert from "node:assert/strict";
import { once } from "node:events";
import { Agent, createServer, request as httpRequest } from "node:http";
import test from "node:test";

import { startHubProxy } from "./extension-test-proxy.mjs";

async function listen(server) {
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  return server.address().port;
}

function requestThroughProxy(port, agent, path) {
  const request = httpRequest({
    hostname: "127.0.0.1",
    port,
    path,
    agent,
    headers: { connection: "keep-alive" },
  });
  const socket = new Promise((resolve) => request.once("socket", resolve));
  const response = new Promise((resolve, reject) => {
    request.once("response", (incoming) => {
      const chunks = [];
      incoming.on("data", (chunk) => chunks.push(chunk));
      incoming.once("end", () =>
        resolve({ statusCode: incoming.statusCode, body: Buffer.concat(chunks).toString() }),
      );
    });
    request.once("error", reject);
  });
  request.end();
  return { socket, response };
}

function startUpgradeThroughProxy(port, path) {
  const request = httpRequest({
    hostname: "127.0.0.1",
    port,
    path,
    headers: { connection: "Upgrade", upgrade: "agw-test" },
  });
  const socket = new Promise((resolve) => request.once("socket", resolve));
  const response = new Promise((resolve, reject) => {
    request.once("upgrade", (incoming, upgradedSocket, head) => {
      resolve({ incoming, socket: upgradedSocket, head });
    });
    request.once("response", (incoming) => {
      incoming.resume();
      reject(new Error(`Expected an upgrade response, received ${incoming.statusCode}.`));
    });
    request.once("error", reject);
  });
  response.catch(() => undefined);
  request.end();
  return { request, socket, response };
}

async function until(read, message) {
  const deadline = Date.now() + 3_000;
  while (Date.now() < deadline) {
    const value = read();
    if (value !== null) return value;
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
  throw new Error(`Timed out waiting for ${message}.`);
}

async function within(promise, message) {
  let timer;
  try {
    return await Promise.race([
      promise,
      new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error(`Timed out waiting for ${message}.`)), 3_000);
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
}

async function closeServer(server) {
  if (!server.listening) return;
  await new Promise((resolve, reject) =>
    server.close((error) => (error ? reject(error) : resolve())),
  );
}

test("the catalog gate handles requests on an existing keep-alive connection", async (context) => {
  const upstreamRequests = [];
  const upstream = createServer((request, response) => {
    upstreamRequests.push(`${request.method} ${request.url}`);
    response.writeHead(200, { "content-type": "application/json" });
    response.end(JSON.stringify({ path: request.url }));
  });
  upstream.keepAliveTimeout = 60_000;
  const upstreamPort = await listen(upstream);
  const proxy = await startHubProxy(upstreamPort);
  const agent = new Agent({ keepAlive: true, maxSockets: 1 });

  context.after(async () => {
    agent.destroy();
    await closeServer(proxy.server);
    await closeServer(upstream);
  });

  const serverInfo = requestThroughProxy(proxy.port, agent, "/api/server-info");
  const keepAliveSocket = await serverInfo.socket;
  assert.deepEqual(await serverInfo.response, {
    statusCode: 200,
    body: JSON.stringify({ path: "/api/server-info" }),
  });

  proxy.setCatalogDelay(true);
  const firstCatalog = requestThroughProxy(proxy.port, agent, "/api/extensions/catalog");
  assert.strictEqual(await firstCatalog.socket, keepAliveSocket);
  const firstPending = await until(
    () => (proxy.state().pendingCatalog === 1 ? proxy.state() : null),
    "the first catalog request to be held",
  );
  assert.equal(firstPending.catalogHeld, 1);
  assert.deepEqual(upstreamRequests, ["GET /api/server-info"]);
  proxy.setCatalogDelay(false);
  assert.deepEqual(await firstCatalog.response, {
    statusCode: 200,
    body: JSON.stringify({ path: "/api/extensions/catalog" }),
  });

  proxy.setCatalogDelay(true);
  const nextCatalog = requestThroughProxy(
    proxy.port,
    agent,
    "/api/extensions/catalog?afterRevision=7",
  );
  assert.strictEqual(await nextCatalog.socket, keepAliveSocket);
  await until(
    () => (proxy.state().pendingCatalog === 1 ? proxy.state() : null),
    "the next catalog request to be held",
  );
  assert.deepEqual(upstreamRequests, ["GET /api/server-info", "GET /api/extensions/catalog"]);
  proxy.setCatalogDelay(false);
  assert.deepEqual(await nextCatalog.response, {
    statusCode: 200,
    body: JSON.stringify({ path: "/api/extensions/catalog?afterRevision=7" }),
  });
  assert.equal(proxy.state().pendingCatalog, 0);
  assert.deepEqual(proxy.state().catalogRequests, [
    "GET /api/extensions/catalog",
    "GET /api/extensions/catalog?afterRevision=7",
  ]);
});

test("SignalR upgrade forwarding tunnels bytes in both directions and tracks held clients", async (context) => {
  const upgradedPaths = [];
  const upstream = createServer();
  upstream.on("upgrade", (request, socket, head) => {
    upgradedPaths.push(request.url);
    socket.write(
      "HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: agw-test\r\n\r\n",
    );
    if (head.length > 0) socket.write(head);
    socket.on("data", (chunk) => socket.write(chunk));
    socket.once("end", () => socket.end());
  });
  const upstreamPort = await listen(upstream);
  const proxy = await startHubProxy(upstreamPort);
  const clientSockets = new Set();
  const clientRequests = new Set();
  const proxySockets = new Set();
  proxy.server.on("connection", (socket) => {
    proxySockets.add(socket);
    socket.once("close", () => proxySockets.delete(socket));
  });

  context.after(async () => {
    for (const request of clientRequests) request.destroy();
    for (const socket of clientSockets) socket.destroy();
    for (const socket of proxySockets) socket.destroy();
    await closeServer(proxy.server);
    await closeServer(upstream);
  });

  proxy.setDelay(true);
  const held = startUpgradeThroughProxy(proxy.port, "/api/hubs/exec?transport=WebSockets");
  clientRequests.add(held.request);
  void held.socket.then((socket) => clientSockets.add(socket));
  const heldSocket = await within(held.socket, "the held hub socket");
  clientSockets.add(heldSocket);
  await until(() => (proxy.state().held === 1 ? true : null), "the hub upgrade to be held");
  assert.deepEqual(upgradedPaths, []);
  held.request.destroy();
  heldSocket.destroy();
  await until(() => (proxy.state().heldClosed === 1 ? true : null), "the held hub client to close");

  proxy.setDelay(false);
  const connection = startUpgradeThroughProxy(proxy.port, "/api/hubs/exec?transport=WebSockets");
  clientRequests.add(connection.request);
  void connection.socket.then((socket) => clientSockets.add(socket));
  const upgraded = await within(connection.response, "the upstream upgrade response");
  clientSockets.add(upgraded.socket);
  assert.equal(upgraded.incoming.statusCode, 101);
  await until(() => (proxy.state().hubOpen === 1 ? true : null), "the hub tunnel to open");

  const payload = Buffer.from([0, 1, 127, 128, 255]);
  const echoed = new Promise((resolve) => upgraded.socket.once("data", resolve));
  upgraded.socket.write(payload);
  assert.deepEqual(await within(echoed, "the echoed upgrade payload"), payload);
  upgraded.socket.destroy();
  await until(() => (proxy.state().hubOpen === 0 ? true : null), "the hub tunnel to close");
  assert.deepEqual(upgradedPaths, ["/api/hubs/exec?transport=WebSockets"]);
});
