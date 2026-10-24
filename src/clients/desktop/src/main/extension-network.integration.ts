// extension 网络限制的集成测试：真实的 Extension session、出站代理与 WebContentsView。
// Integration test for extension network limits with a real Extension session, egress proxy and WebContentsView.
import assert from "node:assert/strict";
import { createSocket } from "node:dgram";
import { once } from "node:events";
import { mkdir, rm, writeFile } from "node:fs/promises";
import { createServer, type IncomingMessage, type Server } from "node:http";
import { connect, createServer as createTcpServer, type Socket } from "node:net";
import { join, resolve } from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import { app, BrowserWindow, protocol, session } from "electron";
import { WebSocketServer } from "ws";

import type { ExtensionEvent } from "../shared/contracts";
import { answerConfirmation } from "./confirmation/confirmation.integration-helper";
import { CONFIRMATION_SCHEME_PRIVILEGES } from "./confirmation/confirmation-protocol";
import { ConfirmationService } from "./confirmation/confirmation-service";
import {
  createConfirmationWindowFactory,
  prepareConfirmationSession,
  registerConfirmationIpc,
} from "./confirmation/confirmation-window";
import { registerExtensionIpc } from "./extensions/extension-ipc";
import { EXTENSION_SCHEME_PRIVILEGES } from "./extensions/extension-protocol";
import { ExtensionRuntime } from "./extensions/extension-runtime";
import { extensionPartition } from "./extensions/extension-session";
import { DesktopSettingsStore } from "./settings/settings-store";

const directory = resolve(".scratch/extension-network");
const userData = join(directory, "user-data");
const HOST = "host-network-test";
const EXTENSION = "net-test";

protocol.registerSchemesAsPrivileged([EXTENSION_SCHEME_PRIVILEGES, CONFIRMATION_SCHEME_PRIVILEGES]);
app.setPath("userData", userData);
app.setName("Agw extension network tests");

type RecordedRequest = { url: string; headers: IncomingMessage["headers"] };

async function listen(server: Server | ReturnType<typeof createTcpServer>): Promise<number> {
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const address = server.address();
  if (address === null || typeof address === "string") throw new Error("No address.");
  return address.port;
}

/** 允许访问的 HTTP 服务，同时提供 Service Worker 与 Worker 页面。The allowed HTTP service with Service Worker and worker pages. */
async function startAllowedHttp() {
  const requests: RecordedRequest[] = [];
  const sockets = new Set<Socket>();
  const server = createServer((request, response) => {
    requests.push({ url: request.url ?? "", headers: request.headers });
    const headers = { "Access-Control-Allow-Origin": "*", "Cache-Control": "no-store" };
    const path = new URL(request.url ?? "/", "http://local").pathname;
    if (path === "/ping" || path === "/worker-ping") {
      response.writeHead(200, { ...headers, "Content-Type": "text/plain" }).end("ok");
    } else if (path === "/redirect-out") {
      response.writeHead(302, { ...headers, Location: "http://127.0.0.1:9/elsewhere" }).end();
    } else if (path === "/sw.js") {
      response
        .writeHead(200, { ...headers, "Content-Type": "text/javascript" })
        .end("self.addEventListener('fetch', () => {});");
    } else if (path === "/worker.js") {
      response
        .writeHead(200, { ...headers, "Content-Type": "text/javascript" })
        .end(
          "postMessage('dedicated-ready'); setInterval(() => fetch('/worker-ping?from=dedicated'), 150);",
        );
    } else if (path === "/shared-worker.js") {
      response
        .writeHead(200, { ...headers, "Content-Type": "text/javascript" })
        .end(
          "onconnect = (event) => { event.ports[0].postMessage('shared-ready'); }; setInterval(() => fetch('/worker-ping?from=shared'), 150);",
        );
    } else if (path === "/sw-page.html") {
      response.writeHead(200, { ...headers, "Content-Type": "text/html" })
        .end(`<!doctype html><script>
        const report = (key, value) => parent.postMessage({ key, value }, "*");
        navigator.serviceWorker.register("/sw.js").then(() => report("sw", "registered"), (error) => report("sw", "failed:" + error.message));
        const dedicated = new Worker("/worker.js");
        dedicated.onmessage = (event) => report("dedicated", event.data);
        const shared = new SharedWorker("/shared-worker.js");
        shared.port.onmessage = (event) => report("shared", event.data);
        shared.port.start();
      </script>`);
    } else {
      response.writeHead(404, headers).end();
    }
  });
  server.on("connection", (socket) => {
    sockets.add(socket);
    socket.on("close", () => sockets.delete(socket));
  });
  return { server, requests, port: await listen(server) };
}

/** 未声明的服务：只记录收到的连接。An undeclared service that records connections. */
async function startCounter() {
  let connections = 0;
  const server = createTcpServer((socket) => {
    connections += 1;
    socket.on("error", () => undefined);
    socket.end();
  });
  return { server, port: await listen(server), connections: () => connections };
}

async function startTcpTarget() {
  let connections = 0;
  const sockets = new Set<Socket>();
  const server = createTcpServer((socket) => {
    connections += 1;
    sockets.add(socket);
    socket.on("error", () => undefined);
    socket.on("close", () => sockets.delete(socket));
  });
  return { server, sockets, port: await listen(server), connections: () => connections };
}

async function startUpstreamProxy() {
  const tunnels: string[] = [];
  const server = createServer();
  server.on("connect", (request, client: Socket, head: Buffer) => {
    tunnels.push(request.url ?? "");
    const [host, port] = (request.url ?? "").split(":");
    const target = connect(Number(port), host, () => {
      client.write("HTTP/1.1 200 Connection Established\r\n\r\n");
      if (head.length > 0) target.write(head);
      target.pipe(client);
      client.pipe(target);
    });
    target.on("error", () => client.destroy());
    client.on("error", () => target.destroy());
  });
  return { server, tunnels, port: await listen(server) };
}

async function waitFor(
  condition: () => boolean | Promise<boolean>,
  message: string,
  timeoutMs = 15_000,
) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (await condition()) return;
    await delay(50);
  }
  throw new Error(`Timed out: ${message}`);
}

async function run(): Promise<void> {
  await rm(directory, { recursive: true, force: true });
  await mkdir(userData, { recursive: true });
  await app.whenReady();

  const allowedHttp = await startAllowedHttp();
  const deniedHttp = await startCounter();
  const wsServer = new WebSocketServer({ host: "127.0.0.1", port: 0 });
  await once(wsServer, "listening");
  const allowedWsPort = (wsServer.address() as { port: number }).port;
  const openWsClients = new Set<import("ws").WebSocket>();
  wsServer.on("connection", (client) => {
    openWsClients.add(client);
    client.on("close", () => openWsClients.delete(client));
  });
  const deniedWs = await startCounter();
  const allowedTcp = await startTcpTarget();
  const deniedTcp = await startCounter();
  const udpPackets: Buffer[] = [];
  const udp = createSocket("udp4");
  udp.on("message", (message) => udpPackets.push(message));
  udp.bind(0, "127.0.0.1");
  await once(udp, "listening");
  const udpPort = udp.address().port;
  const upstream = await startUpstreamProxy();

  const hostPermissions = [
    `http://127.0.0.1:${allowedHttp.port}`,
    `ws://127.0.0.1:${allowedWsPort}`,
    `https://127.0.0.1:${allowedTcp.port}`,
  ];
  const root = join(userData, "extensions", "packages", EXTENSION);
  await mkdir(root, { recursive: true });
  await writeFile(
    join(root, "manifest.json"),
    JSON.stringify({
      manifestVersion: 1,
      id: EXTENSION,
      name: "Network test",
      version: "1.0.0",
      contributes: { mainViews: [{ id: "app", title: "App", icon: "Box", entry: "index.html" }] },
      hostPermissions,
    }),
  );
  await writeFile(
    join(root, "index.html"),
    '<!doctype html><script type="module" src="./main.js"></script>',
  );
  await writeFile(join(root, "main.js"), 'import { connect } from "/__agw/sdk.js";\nconnect();\n');

  // 宿主窗口用生产 preload 收集推送事件。The host window collects pushed events through the production preload.
  const window = new BrowserWindow({
    show: false,
    webPreferences: {
      preload: resolve("dist/preload/index.js"),
      contextIsolation: true,
      sandbox: true,
    },
  });
  await window.loadURL(`http://127.0.0.1:${allowedHttp.port}/ping`);
  await window.webContents.executeJavaScript(
    "window.__events = []; window.agwDesktop.onExtensionEvent((event) => window.__events.push(event)); true",
  );
  const events = async (): Promise<ExtensionEvent[]> =>
    (await window.webContents.executeJavaScript("window.__events")) as ExtensionEvent[];

  const settings = new DesktopSettingsStore(userData, "client", {
    encrypt: (value) => Buffer.from(value, "utf8"),
    decrypt: (value) => value.toString("utf8"),
  });
  const confirmations = new ConfirmationService({
    openWindow: createConfirmationWindowFactory({
      parent: () => window,
      preloadPath: resolve("dist/preload/confirmation.js"),
      session: prepareConfirmationSession(resolve("dist/confirmation")),
    }),
  });
  registerConfirmationIpc(confirmations);
  const runtime = new ExtensionRuntime({
    userDataDirectory: userData,
    appVersion: "0.5.0",
    sdkDirectory: resolve("resources/extension-sdk"),
    // 这个测试只使用临时目录中的 extension。This test uses only the extension in its scratch directory.
    bundledExtensionsDirectory: join(directory, "bundled-extensions"),
    extensionPreloadPath: resolve("dist/preload/extension.js"),
    settings,
    confirmations,
    getMainWindow: () => window,
    onSettingsChanged: async () => undefined,
    log: (message) => console.info(message),
  });
  await runtime.initialize();
  // 与 index.ts 相同地注册 extension 通道；测试窗口是唯一的宿主。Register the channels as index.ts does; the test window is the only host.
  registerExtensionIpc({
    runtime,
    confirmations,
    assertTrusted: (event) => assert.equal(event.sender, window.webContents),
    runtimeState: async () => {
      throw new Error("The runtime state is not used by this test.");
    },
  });
  // 启用的权限由确认窗口批准。The confirmation window approves the permissions for enabling.
  const enabled = runtime.setEnabled(EXTENSION, true);
  const approval = await answerConfirmation("Enable");
  assert.ok(approval.includes(hostPermissions[0]), approval);
  assert.equal(await enabled, true);
  await runtime.attachHost(HOST);
  await runtime.beginActivation(HOST, EXTENSION);
  const extensionSession = session.fromPartition(extensionPartition(EXTENSION));
  let swRegistrations = 0;
  extensionSession.serviceWorkers.on("registration-completed", () => (swRegistrations += 1));
  runtime.createFrame(HOST, {
    extensionId: EXTENSION,
    frameId: "frame-1",
    kind: "mainView",
    viewId: "app",
    entry: "index.html",
    backgroundColor: "#ffffff",
  });
  await waitFor(
    () => runtime.registry.get("frame-1")?.pageId != null,
    "the extension page handshake",
  );
  const contents = runtime.views.get("frame-1")!.view.webContents;
  const page = <T>(code: string) => contents.executeJavaScript(code, true) as Promise<T>;
  const caller = () => ({
    hostInstanceId: HOST,
    extensionId: EXTENSION,
    frameId: "frame-1",
    pageId: runtime.registry.get("frame-1")!.pageId!,
  });

  // fetch 与 WebSocket。fetch and WebSocket.
  assert.equal(
    await page(`fetch("http://127.0.0.1:${allowedHttp.port}/ping").then((r) => r.text())`),
    "ok",
  );
  console.log("PASS fetch to an approved host succeeds");
  assert.equal(
    await page(
      `fetch("http://127.0.0.1:${deniedHttp.port}/x").then(() => "loaded", () => "failed")`,
    ),
    "failed",
  );
  assert.equal(
    await page(
      `fetch("http://localhost:${allowedHttp.port}/ping").then(() => "loaded", () => "failed")`,
    ),
    "failed",
  );
  assert.equal(deniedHttp.connections(), 0);
  await waitFor(
    async () =>
      (await events()).some(
        (event) => event.type === "request-blocked" && event.url.includes(`:${deniedHttp.port}`),
      ),
    "a request-blocked event for the undeclared service",
  );
  console.log(
    "PASS fetch to undeclared hosts, including loopback names, fails without reaching them",
  );

  const socketResult = (port: number) =>
    page<string>(`new Promise((resolve) => {
      const socket = new WebSocket("ws://127.0.0.1:${port}");
      socket.onopen = () => resolve("open");
      socket.onerror = () => resolve("error");
    })`);
  assert.equal(await socketResult(allowedWsPort), "open");
  assert.equal(await socketResult(deniedWs.port), "error");
  assert.equal(deniedWs.connections(), 0);
  console.log("PASS WebSocket only connects to the approved origin");

  // WebRTC：TCP 经出站代理，UDP 被禁止。WebRTC: TCP goes through the egress proxy, UDP is blocked.
  const gatherIce = (servers: string) =>
    page(`(async () => {
      const connection = new RTCPeerConnection({ iceServers: ${servers}, iceTransportPolicy: "relay" });
      connection.createDataChannel("probe");
      await connection.setLocalDescription(await connection.createOffer());
      await new Promise((resolve) => {
        connection.onicegatheringstatechange = () => connection.iceGatheringState === "complete" && resolve();
        setTimeout(resolve, 6000);
      });
      connection.close();
      return true;
    })()`);
  await gatherIce(
    `[{ urls: "turn:127.0.0.1:${deniedTcp.port}?transport=tcp", username: "user", credential: "secret" }]`,
  );
  assert.equal(deniedTcp.connections(), 0);
  assert.ok(
    (await events()).some(
      (event) =>
        event.type === "request-blocked" &&
        event.resourceType === "connect" &&
        event.url === `127.0.0.1:${deniedTcp.port}`,
    ),
    "the egress proxy refused the TURN/TCP CONNECT",
  );
  console.log("PASS WebRTC TURN over TCP to an undeclared port is refused by the egress proxy");
  await gatherIce(
    `[{ urls: "turn:127.0.0.1:${allowedTcp.port}?transport=tcp", username: "user", credential: "secret" }]`,
  );
  assert.ok(
    allowedTcp.connections() > 0,
    "the declared TURN/TCP target received a proxied connection",
  );
  console.log("PASS WebRTC TURN over TCP to a declared port goes through the egress proxy");
  await gatherIce(
    `[{ urls: "stun:127.0.0.1:${udpPort}" }, { urls: "turn:127.0.0.1:${udpPort}?transport=udp", username: "user", credential: "secret" }]`,
  );
  await delay(500);
  assert.equal(udpPackets.length, 0);
  console.log("PASS WebRTC sends no UDP packets");

  // http.request。
  const response = await runtime.httpRequest(caller(), {
    requestId: "request-1",
    url: `http://127.0.0.1:${allowedHttp.port}/ping`,
  });
  assert.equal(response.status, 200);
  assert.equal(new TextDecoder().decode(response.body), "ok");
  await assert.rejects(
    runtime.httpRequest(caller(), {
      requestId: "request-2",
      url: `http://127.0.0.1:${allowedHttp.port}/redirect-out`,
    }),
    { code: "ERR_HOST_NOT_PERMITTED" },
  );
  console.log("PASS http.request reaches approved hosts and refuses redirects to undeclared hosts");

  // 上游代理。Upstream proxies.
  await session.defaultSession.setProxy({
    proxyRules: `http://127.0.0.1:${upstream.port}`,
    proxyBypassRules: "<-loopback>",
  });
  assert.equal(
    await page(
      `fetch("http://127.0.0.1:${allowedHttp.port}/ping?via=upstream").then((r) => r.text())`,
    ),
    "ok",
  );
  await runtime.httpRequest(caller(), {
    requestId: "request-3",
    url: `http://127.0.0.1:${allowedHttp.port}/ping?via=http`,
  });
  assert.ok(
    upstream.tunnels.includes(`127.0.0.1:${allowedHttp.port}`),
    "the egress proxy tunneled through the upstream proxy",
  );
  await session.defaultSession.setProxy({
    proxyRules: "socks5://127.0.0.1:1080",
    proxyBypassRules: "<-loopback>",
  });
  assert.equal(
    await page(
      `fetch("http://127.0.0.1:${allowedHttp.port}/ping?via=socks").then(() => "loaded", () => "failed")`,
    ),
    "failed",
  );
  await waitFor(
    async () =>
      (await events()).some(
        (event) =>
          event.type === "request-blocked" && event.reason === "upstream-proxy-unsupported",
      ),
    "an upstream-proxy-unsupported event",
  );
  await session.defaultSession.setProxy({ mode: "direct" });
  console.log("PASS forwarded connections follow the system upstream proxy and refuse SOCKS");

  // Service Worker 被禁止，Dedicated 与 Shared Worker 正常。Service Workers are blocked; dedicated and shared workers work.
  const workerReports = await page<Record<string, string>>(`new Promise((resolve) => {
    const reports = {};
    window.addEventListener("message", (event) => {
      reports[event.data.key] = event.data.value;
      if (reports.sw && reports.dedicated && reports.shared) resolve(reports);
    });
    const frame = document.createElement("iframe");
    frame.src = "http://127.0.0.1:${allowedHttp.port}/sw-page.html";
    document.body.append(frame);
    setTimeout(() => resolve(reports), 8000);
  })`);
  assert.match(workerReports.sw ?? "", /^failed:/u);
  assert.equal(workerReports.dedicated, "dedicated-ready");
  assert.equal(workerReports.shared, "shared-ready");
  assert.equal(
    allowedHttp.requests.some(
      (request) => String(request.headers["service-worker"] ?? "").toLowerCase() === "script",
    ),
    false,
  );
  assert.ok(
    (await events()).some(
      (event) => event.type === "request-blocked" && event.reason === "service-worker-blocked",
    ),
  );
  assert.deepEqual(Object.keys(extensionSession.serviceWorkers.getAllRunning()), []);
  assert.equal(swRegistrations, 0);
  console.log("PASS Service Worker registration is blocked while dedicated and shared workers run");

  // Inspect：DevTools 前端在被检查页面的 session 中加载，不能被 extension 的域名限制拦截。
  // Inspect: the DevTools frontend loads in the inspected page's session and must not be blocked by host rules.
  const blockedCount = async () =>
    (await events()).filter((event) => event.type === "request-blocked").length;
  const blockedBeforeDevTools = await blockedCount();
  runtime.openFrameDevTools(HOST, "frame-1");
  await waitFor(() => contents.devToolsWebContents !== null, "DevTools to be created");
  const devTools = contents.devToolsWebContents!;
  await waitFor(
    async () =>
      contents.isDevToolsOpened() &&
      !devTools.isLoading() &&
      (await devTools.executeJavaScript("document.readyState")) === "complete",
    "the DevTools frontend to load",
  );
  assert.match(devTools.getURL(), /^devtools:\/\/devtools\/bundled\//u);
  assert.equal(await devTools.executeJavaScript("document.body.childElementCount > 0"), true);
  await delay(1000);
  assert.equal(await blockedCount(), blockedBeforeDevTools, "DevTools adds no blocked requests");
  contents.closeDevTools();
  console.log("PASS Inspect opens DevTools for the extension page");

  // 销毁 frame 后不再有请求；释放后代理关闭。After destroying the frame no requests remain; release closes the proxy.
  await page(
    `new Promise((resolve) => { window.__socket = new WebSocket("ws://127.0.0.1:${allowedWsPort}"); window.__socket.onopen = resolve; })`,
  );
  await waitFor(() => openWsClients.size > 0, "an open WebSocket tunnel");
  runtime.destroyFrame(HOST, "frame-1");
  await delay(500);
  const afterDestroy = allowedHttp.requests.length;
  await delay(1000);
  assert.equal(
    allowedHttp.requests.length,
    afterDestroy,
    "no requests after the frame is destroyed",
  );
  console.log("PASS destroying the frame stops the page and its workers");

  const proxyPort = Number(
    (await extensionSession.resolveProxy(`http://127.0.0.1:${allowedHttp.port}/`)).match(
      /:(\d+)/u,
    )?.[1],
  );
  await runtime.releaseResources(HOST, EXTENSION);
  await waitFor(() => openWsClients.size === 0, "tunnels closed after release");
  await assert.rejects(
    new Promise((resolvePromise, reject) => {
      const socket = connect(proxyPort, "127.0.0.1", () => resolvePromise(socket));
      socket.on("error", reject);
    }),
  );
  console.log("PASS releasing the extension closes its egress proxy and tunnels");

  window.destroy();
  for (const server of [
    allowedHttp.server,
    deniedHttp.server,
    deniedWs.server,
    allowedTcp.server,
    deniedTcp.server,
    upstream.server,
  ]) {
    server.close();
  }
  wsServer.close();
  udp.close();
}

void run().then(
  () => app.exit(0),
  (error: unknown) => {
    console.error(error);
    app.exit(1);
  },
);
