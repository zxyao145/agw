import { spawn } from "node:child_process";
import { createHash, randomBytes } from "node:crypto";
import { once } from "node:events";
import { createWriteStream } from "node:fs";
import { access, mkdir, open, readFile, readdir, rm, stat, writeFile } from "node:fs/promises";
import { createServer as createHttpServer, request as httpRequest } from "node:http";
import { createServer as createTcpServer } from "node:net";
import { dirname, join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { setTimeout as delay } from "node:timers/promises";

import WebSocket, { WebSocketServer } from "ws";
import { v7 as uuidV7 } from "uuid";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const webDirectory = resolve(scriptDirectory, "..");
const repositoryRoot = resolve(webDirectory, "../../..");
const scratchDirectory = join(repositoryRoot, ".scratch", "server-extension-web-e2e");
const lockPath = join(repositoryRoot, ".scratch", "server-extension-web-e2e.lock");
const fixtureDirectory = join(
  repositoryRoot,
  "tests",
  "Agw.Extensions.Runtime.Tests",
  "Fixture",
  "bin",
  "Debug",
  "net10.0",
);
const fixtureAssembly = "Agw.Extensions.Runtime.TestExtension.dll";
const extensionType = "Agw.Extensions.Runtime.Tests.Fixture.TestExtension";
const extensionId = uuidV7();
const extensionName = "web-e2e-extension";
const extensionDisplayName = "Web E2E Extension";
const ports = { host: 30818, next: 3103, proxy: 3102 };
const children = new Map();
const hmrWebSocketServer = new WebSocketServer({ noServer: true });
const activeWebSockets = new Set();
const activeUpgradeSockets = new Set();
const nextHmrPaths = new Set(["/_next/hmr", "/_next/webpack-hmr"]);

let proxyServer;
let lockHandle;
let ownsLock = false;
let shutdownRequested = false;
let childFailure;
let resolveTermination;
const terminated = new Promise((resolvePromise) => {
  resolveTermination = resolvePromise;
});

function requestTermination(error) {
  shutdownRequested = true;
  if (error && !childFailure) childFailure = error;
  resolveTermination();
}

process.on("SIGINT", () => requestTermination());
process.on("SIGTERM", () => requestTermination());

async function assertPortAvailable(port) {
  const server = createTcpServer();
  server.listen(port, "127.0.0.1");
  await new Promise((resolvePromise, reject) => {
    server.once("error", reject);
    server.once("listening", () => {
      server.close((error) => (error ? reject(error) : resolvePromise()));
    });
  });
}

async function listFiles(directory) {
  const entries = await readdir(directory, { withFileTypes: true });
  const files = [];
  for (const entry of entries) {
    const fullPath = join(directory, entry.name);
    if (entry.isDirectory()) files.push(...(await listFiles(fullPath)));
    else if (entry.isFile()) files.push(fullPath);
    else throw new Error(`Unexpected fixture entry: ${fullPath}`);
  }
  return files;
}

async function writeInstalledExtension(directory, version, entryType) {
  const fixturePaths = await listFiles(fixtureDirectory);
  const packageFiles = await Promise.all(
    fixturePaths.map(async (source) => ({
      path: `server/${relative(fixtureDirectory, source).split(sep).join("/")}`,
      bytes: await readFile(source),
    })),
  );
  const checksums = Object.fromEntries(
    packageFiles.map(({ path, bytes }) => [path, createHash("sha256").update(bytes).digest("hex")]),
  );
  const manifest = {
    manifestVersion: 2,
    id: extensionId,
    name: extensionName,
    displayName: extensionDisplayName,
    version,
    targets: 1,
    server: {
      apiVersion: 1,
      entryAssembly: `server/${fixtureAssembly}`,
      entryType,
    },
    files: checksums,
  };

  await mkdir(directory, { recursive: true });
  for (const file of packageFiles) {
    const target = join(directory, file.path);
    await mkdir(dirname(target), { recursive: true });
    await writeFile(target, file.bytes);
  }
  await writeFile(join(directory, "manifest.json"), `${JSON.stringify(manifest, null, 2)}\n`);
}

function spawnOwned(name, command, args, cwd, logPath) {
  const logStream = createWriteStream(logPath, { flags: "w" });
  const child = spawn(command, args, {
    cwd,
    detached: process.platform !== "win32",
    stdio: ["ignore", "pipe", "pipe"],
  });
  const owned = { child, output: "", logStream, hasExited: false, exitPromise: null };
  children.set(name, owned);

  owned.exitPromise = new Promise((resolvePromise) => {
    child.once("error", (error) => {
      requestTermination(new Error(`${name} failed to start: ${error.message}`));
    });
    child.once("close", (code, signal) => {
      owned.hasExited = true;
      if (!shutdownRequested) {
        requestTermination(
          new Error(`${name} exited before cleanup (code ${code}, signal ${signal}).`),
        );
      }
      logStream.end(resolvePromise);
    });
  });

  for (const stream of [child.stdout, child.stderr]) {
    stream.on("data", (chunk) => {
      owned.output += chunk.toString();
      logStream.write(chunk);
      process.stdout.write(chunk);
    });
  }
  return owned;
}

function requireChildrenRunning() {
  if (childFailure) throw childFailure;
  if (shutdownRequested) throw new Error("The extension E2E harness received a shutdown signal.");
}

async function waitForReady(label, check, timeoutMs = 180_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    requireChildrenRunning();
    if (await check()) return;
    await delay(250);
  }
  requireChildrenRunning();
  throw new Error(`Timed out waiting for ${label}.`);
}

async function getResponse(url) {
  try {
    return await fetch(url, { signal: AbortSignal.timeout(2_000) });
  } catch {
    return null;
  }
}

async function waitForHost(host) {
  await waitForReady("the isolated Standalone Host", async () => {
    if (!host.output.includes(`Now listening on: http://127.0.0.1:${ports.host}`)) return false;
    const response = await getResponse(`http://127.0.0.1:${ports.host}/api/server-info`);
    if (!response?.ok) return false;
    const envelope = await response.json();
    return envelope.data?.initialized === true;
  });
}

async function waitForNext(next) {
  await waitForReady("the isolated Next.js Web app", async () => {
    if (!next.output.includes("Ready in")) return false;
    const response = await getResponse(`http://127.0.0.1:${ports.next}/login`);
    if (!response) return false;
    if (response.status >= 500) {
      throw new Error(`Next.js returned ${response.status} while starting.`);
    }
    return response.ok;
  });
}

function isBackendPath(pathname) {
  return (
    pathname === "/api" ||
    pathname.startsWith("/api/") ||
    pathname === "/openapi" ||
    pathname.startsWith("/openapi/")
  );
}

function upstreamPort(request) {
  const url = new URL(request.url ?? "/", `http://${request.headers.host ?? "127.0.0.1"}`);
  return isBackendPath(url.pathname) ? ports.host : ports.next;
}

function createProxy() {
  const server = createHttpServer((request, response) => {
    const url = new URL(request.url ?? "/", `http://${request.headers.host ?? "127.0.0.1"}`);
    if (url.pathname === "/__e2e/ready") {
      response.writeHead(200, { "content-type": "text/plain; charset=utf-8" }).end("ready");
      return;
    }
    const port = upstreamPort(request);
    if (port === ports.host)
      process.stdout.write(`[proxy] ${request.method} ${url.pathname} -> isolated Host\n`);
    const remoteAddress = request.socket.remoteAddress;
    const publicHost = request.headers.host;
    if (!remoteAddress || !publicHost) {
      response.destroy(
        new Error("The local proxy received a request without socket or Host metadata."),
      );
      return;
    }
    const upstream = httpRequest(
      {
        hostname: "127.0.0.1",
        port,
        method: request.method,
        path: request.url,
        headers: {
          ...request.headers,
          "x-forwarded-for": remoteAddress,
          "x-forwarded-host": publicHost,
          "x-forwarded-proto": "http",
        },
      },
      (upstreamResponse) => {
        response.writeHead(upstreamResponse.statusCode ?? 502, upstreamResponse.headers);
        upstreamResponse.pipe(response);
      },
    );
    upstream.once("error", (error) => {
      process.stderr.write(
        `[proxy] ${request.method} ${url.pathname} upstream failure: ${error.message}\n`,
      );
      response.destroy(error);
    });
    request.pipe(upstream);
  });

  server.on("upgrade", (request, socket, head) => {
    const url = new URL(request.url ?? "/", `http://${request.headers.host ?? "127.0.0.1"}`);
    if (!nextHmrPaths.has(url.pathname)) {
      socket.destroy();
      return;
    }

    activeUpgradeSockets.add(socket);
    socket.once("close", () => activeUpgradeSockets.delete(socket));
    const protocolsHeader = request.headers["sec-websocket-protocol"];
    const protocols =
      typeof protocolsHeader === "string"
        ? protocolsHeader
            .split(",")
            .map((protocol) => protocol.trim())
            .filter(Boolean)
        : [];
    const excludedHeaders = new Set([
      "connection",
      "host",
      "origin",
      "sec-websocket-extensions",
      "sec-websocket-key",
      "sec-websocket-protocol",
      "sec-websocket-version",
      "upgrade",
    ]);
    const headers = Object.fromEntries(
      Object.entries(request.headers).filter(
        ([name, value]) => value !== undefined && !excludedHeaders.has(name.toLowerCase()),
      ),
    );
    const upstream = new WebSocket(`ws://127.0.0.1:${ports.next}${request.url}`, protocols, {
      headers,
      ...(typeof request.headers.origin === "string" ? { origin: request.headers.origin } : {}),
    });
    activeWebSockets.add(upstream);
    let downstream;
    const pendingMessages = [];

    upstream.on("message", (data, isBinary) => {
      if (!downstream) {
        pendingMessages.push({ data, isBinary });
        return;
      }
      forwardWebSocketMessage(upstream, downstream, data, isBinary);
    });
    upstream.once("error", (error) => {
      if (!shutdownRequested) {
        process.stderr.write(`[proxy] Next HMR WebSocket upstream failure: ${error.message}\n`);
      }
      if (!socket.destroyed) socket.destroy(error);
      if (downstream && downstream.readyState !== WebSocket.CLOSED) downstream.terminate();
    });
    upstream.once("close", () => {
      activeWebSockets.delete(upstream);
      if (downstream && downstream.readyState === WebSocket.OPEN) downstream.close();
    });
    upstream.once("open", () => {
      if (socket.destroyed) {
        upstream.terminate();
        return;
      }
      hmrWebSocketServer.handleUpgrade(request, socket, head, (client) => {
        downstream = client;
        activeWebSockets.add(client);
        client.on("message", (data, isBinary) => {
          forwardWebSocketMessage(client, upstream, data, isBinary);
        });
        client.once("error", (error) => {
          if (!shutdownRequested) {
            process.stderr.write(`[proxy] Next HMR WebSocket client failure: ${error.message}\n`);
          }
          upstream.terminate();
        });
        client.once("close", () => {
          activeWebSockets.delete(client);
          if (upstream.readyState === WebSocket.OPEN) upstream.close();
          else if (upstream.readyState === WebSocket.CONNECTING) upstream.terminate();
        });
        for (const message of pendingMessages) {
          forwardWebSocketMessage(upstream, client, message.data, message.isBinary);
        }
        pendingMessages.length = 0;
      });
    });
  });

  return server;
}

function forwardWebSocketMessage(source, target, data, isBinary) {
  if (target.readyState !== WebSocket.OPEN) return;
  target.send(data, { binary: isBinary }, (error) => {
    if (!error) return;
    if (!shutdownRequested) {
      process.stderr.write(`[proxy] Next HMR WebSocket forward failure: ${error.message}\n`);
    }
    source.terminate();
    target.terminate();
  });
}

async function startProxy() {
  proxyServer = createProxy();
  proxyServer.listen(ports.proxy, "127.0.0.1");
  await once(proxyServer, "listening");
}

function signalChild(owned, signal) {
  if (!owned.child.pid) return;
  if (process.platform === "win32") {
    if (!owned.hasExited) owned.child.kill(signal);
    return;
  }
  try {
    process.kill(-owned.child.pid, signal);
  } catch (error) {
    if (error.code !== "ESRCH") throw error;
  }
}

function processStillOwned(owned) {
  if (!owned.child.pid) return false;
  if (process.platform === "win32") return !owned.hasExited;
  try {
    process.kill(-owned.child.pid, 0);
    return true;
  } catch (error) {
    if (error.code === "ESRCH") return false;
    throw error;
  }
}

async function stopOwnedProcesses() {
  for (const webSocket of activeWebSockets) webSocket.terminate();
  for (const socket of activeUpgradeSockets) socket.destroy();
  if (proxyServer?.listening) {
    proxyServer.closeAllConnections();
    await new Promise((resolvePromise) => proxyServer.close(resolvePromise));
  }

  const ownedProcesses = [...children.values()];
  for (const owned of ownedProcesses) signalChild(owned, "SIGTERM");
  const graceDeadline = Date.now() + 5_000;
  while (ownedProcesses.some(processStillOwned) && Date.now() < graceDeadline) await delay(100);
  for (const owned of ownedProcesses.filter(processStillOwned)) signalChild(owned, "SIGKILL");
  for (const owned of ownedProcesses) {
    await owned.exitPromise;
  }
}

async function main() {
  await mkdir(dirname(lockPath), { recursive: true });
  lockHandle = await open(lockPath, "wx");
  ownsLock = true;
  await assertPortAvailable(ports.host);
  await assertPortAvailable(ports.next);
  await assertPortAvailable(ports.proxy);

  await rm(scratchDirectory, { recursive: true, force: true });
  const logDirectory = join(scratchDirectory, "logs");
  const extensionsDirectory = join(scratchDirectory, "Extensions");
  const serverDataDirectory = join(scratchDirectory, "server-data");
  await mkdir(logDirectory, { recursive: true });
  await mkdir(extensionsDirectory, { recursive: true });
  await mkdir(serverDataDirectory, { recursive: true });

  const assemblyPath = join(fixtureDirectory, fixtureAssembly);
  await access(assemblyPath);
  if (!(await stat(fixtureDirectory)).isDirectory()) {
    throw new Error(`The compiled extension fixture directory is missing: ${fixtureDirectory}`);
  }

  await writeInstalledExtension(join(extensionsDirectory, extensionName), "1.0.0", extensionType);

  const adminPassword = randomBytes(24).toString("base64url");
  await writeFile(
    join(scratchDirectory, "credentials.json"),
    `${JSON.stringify({ adminPassword, extensionId, extensionName, extensionDisplayName })}\n`,
    { mode: 0o600 },
  );

  const host = spawnOwned(
    "host",
    "dotnet",
    [
      "run",
      "--project",
      join(repositoryRoot, "src", "server", "Agw.Standalone.Host"),
      "--no-build",
      "--no-launch-profile",
      "--configuration",
      "Debug",
      "--",
      "--environment=Production",
      `--urls=http://127.0.0.1:${ports.host}`,
      `--AgwDataDir=${serverDataDirectory}`,
      `--AgwLogDir=${logDirectory}`,
      "--Database:Provider=sqlite",
      `--Database:ConnectionString=Data Source=${join(serverDataDirectory, "agw.db")};Pooling=False`,
      "--Execution:Provider=InProcess",
      "--DistributedLock:Provider=inmemory",
      `--Extensions:Directory=${extensionsDirectory}`,
      "--Extensions:StatePollInterval=00:00:00.100",
      `--Setup:AdminPassword=${adminPassword}`,
    ],
    repositoryRoot,
    join(logDirectory, "host.log"),
  );
  await waitForHost(host);

  const next = spawnOwned(
    "next",
    "pnpm",
    ["exec", "next", "dev", "--port", String(ports.next), "--hostname", "127.0.0.1", "--webpack"],
    webDirectory,
    join(logDirectory, "next.log"),
  );
  await waitForNext(next);
  await startProxy();

  process.stdout.write(
    `[harness] Host ${ports.host}, Next.js ${ports.next}, same-origin proxy ${ports.proxy}\n`,
  );
  await terminated;
  if (childFailure) throw childFailure;
}

try {
  await main();
} catch (error) {
  process.stderr.write(`${error instanceof Error ? error.stack : String(error)}\n`);
  process.exitCode = 1;
} finally {
  try {
    await stopOwnedProcesses();
  } finally {
    if (ownsLock) {
      await rm(join(scratchDirectory, "credentials.json"), { force: true });
      await lockHandle.close();
      await rm(lockPath, { force: true });
    }
  }
}
