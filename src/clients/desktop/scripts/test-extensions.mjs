// extension 完整流程的集成测试：构建 → 启动 SQLite Standalone Host 与本地服务 → 两次运行 Electron（首次与重新启动）。
// Full-flow extension integration test: build, start a SQLite Standalone Host and local services, then run
// Electron twice (first launch and restart).
import { spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import { once } from "node:events";
import { createWriteStream } from "node:fs";
import { cp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { createServer } from "node:http";
import { connect, createServer as createTcpServer } from "node:net";
import { join, resolve } from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import electron from "electron";
import { build } from "esbuild";

import {
  buildExtensionFixtures,
  HTTP_PORT_PLACEHOLDER,
  writeSampleV2Zip,
} from "./build-extension-fixtures.mjs";
import { buildExtensionSdk } from "./build-extension-sdk.mjs";
import { mainBuildOptions } from "./build-main.mjs";

const desktopDirectory = resolve(".");
const repositoryRoot = resolve(desktopDirectory, "..", "..", "..");
const root = resolve(".scratch/extension-tests");
const userData = join(root, "user-data");
const fixtures = join(root, "fixtures");
const projectDirectory = join(root, "project");
const adminPassword = randomBytes(18).toString("hex");
const children = new Set();

function run(command, args, options = {}) {
  return new Promise((resolvePromise, reject) => {
    const child = spawn(command, args, { stdio: "inherit", ...options });
    child.once("error", reject);
    child.once("exit", (code) =>
      code === 0 ? resolvePromise() : reject(new Error(`${command} exited with ${code}`)),
    );
  });
}

async function listen(server) {
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  return server.address().port;
}

/** 取得一个空闲端口后释放，交给 Host 监听。Picks a free port and releases it for the Host. */
async function freePort() {
  const probe = createServer();
  const port = await listen(probe);
  probe.close();
  await once(probe, "close");
  return port;
}

async function waitFor(check, label, timeoutMs = 120_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      if (await check()) return;
    } catch {
      // Host 尚未就绪时连接失败，继续等待。Connections fail until the Host is ready, so keep waiting.
    }
    await delay(500);
  }
  throw new Error(`Timed out waiting for ${label}`);
}

/** OpenAI 兼容的回显服务：把最后一条用户消息回显为 `Echo: <text>`。An OpenAI-compatible echo service. */
async function startEchoService() {
  // 收到的用户消息，供 Electron 测试读取。User messages received, for the Electron test to read.
  const inputs = [];
  const server = createServer((request, response) => {
    let raw = "";
    request.on("data", (chunk) => (raw += chunk));
    request.on("end", () => {
      if (!request.url.endsWith("/chat/completions")) {
        response.writeHead(404).end();
        return;
      }
      const body = JSON.parse(raw);
      const user = [...body.messages].reverse().find((message) => message.role === "user");
      const userText =
        typeof user?.content === "string"
          ? user.content
          : (user?.content ?? []).map((part) => part.text ?? "").join("");
      inputs.push(userText);
      const text = `Echo: ${userText}`;
      const created = Math.floor(Date.now() / 1000);
      if (body.stream) {
        response.writeHead(200, {
          "content-type": "text/event-stream",
          "cache-control": "no-cache",
        });
        const chunk = (delta, finish) =>
          `data: ${JSON.stringify({
            id: "chatcmpl-echo",
            object: "chat.completion.chunk",
            created,
            model: body.model,
            choices: [{ index: 0, delta, finish_reason: finish }],
          })}\n\n`;
        response.write(chunk({ role: "assistant", content: text }, null));
        response.write(chunk({}, "stop"));
        response.end("data: [DONE]\n\n");
        return;
      }
      response.writeHead(200, { "content-type": "application/json" });
      response.end(
        JSON.stringify({
          id: "chatcmpl-echo",
          object: "chat.completion",
          created,
          model: body.model,
          choices: [
            { index: 0, message: { role: "assistant", content: text }, finish_reason: "stop" },
          ],
          usage: { prompt_tokens: 1, completion_tokens: 1, total_tokens: 2 },
        }),
      );
    });
  });
  return { server, port: await listen(server), inputs };
}

/**
 * 本地测试服务：正常路径、永远不响应的路径、重定向到未声明域名的路径，以及供 Electron 测试读取的观察结果。
 * The local test service: a normal path, a path that never responds, a redirect to an undeclared host, and
 * observations the Electron test reads.
 */
async function startTestService(hubProxy, echo) {
  const requests = [];
  let neverClosed = 0;
  const server = createServer((request, response) => {
    const url = new URL(request.url, "http://local");
    const headers = { "Access-Control-Allow-Origin": "*", "Cache-Control": "no-store" };
    if (url.pathname === "/__test/state") {
      response.writeHead(200, { "content-type": "application/json" });
      response.end(
        JSON.stringify({ requests, neverClosed, hub: hubProxy.state(), echoInputs: echo.inputs }),
      );
      return;
    }
    if (url.pathname === "/__test/delay-hub") {
      hubProxy.setDelay(url.searchParams.get("enabled") === "true");
      response.writeHead(204).end();
      return;
    }
    requests.push(`${request.method} ${url.pathname}`);
    if (url.pathname === "/ok") {
      response.writeHead(200, { ...headers, "content-type": "text/plain" }).end("ok");
    } else if (url.pathname === "/never") {
      request.socket.once("close", () => (neverClosed += 1));
    } else if (url.pathname === "/redirect-out") {
      response.writeHead(302, { ...headers, location: "http://198.51.100.1/elsewhere" }).end();
    } else {
      response.writeHead(404, headers).end();
    }
  });
  return { server, port: await listen(server) };
}

/**
 * Host 前的 TCP 转发：开启延迟后，新的 `/api/hubs/exec` 连接不转发，直到客户端关闭。
 * A TCP forwarder in front of the Host: while delaying, new `/api/hubs/exec` connections are held until the client closes.
 */
async function startHubProxy(hostPort) {
  let delayHub = false;
  let held = 0;
  let heldClosed = 0;
  let hubOpen = 0;
  const server = createTcpServer((client) => {
    client.once("data", (first) => {
      client.pause();
      const isHub = first.subarray(0, 64).toString("latin1").startsWith("GET /api/hubs/exec");
      if (isHub && delayHub) {
        held += 1;
        client.on("error", () => undefined);
        client.once("close", () => (heldClosed += 1));
        return;
      }
      const upstream = connect(hostPort, "127.0.0.1", () => {
        if (isHub) hubOpen += 1;
        upstream.write(first);
        client.pipe(upstream);
        upstream.pipe(client);
        client.resume();
      });
      upstream.once("close", () => {
        if (isHub) hubOpen -= 1;
        client.destroy();
      });
      upstream.on("error", () => client.destroy());
      client.on("error", () => upstream.destroy());
      client.once("close", () => upstream.destroy());
    });
  });
  return {
    server,
    port: await listen(server),
    setDelay: (enabled) => (delayHub = enabled),
    state: () => ({ delayHub, held, heldClosed, hubOpen }),
  };
}

async function api(baseUrl, session, method, path, body) {
  const headers = { accept: "application/json" };
  if (body !== undefined) headers["content-type"] = "application/json";
  if (session.bearer) headers.authorization = `Bearer ${session.bearer}`;
  else {
    if (session.cookies.size > 0)
      headers.cookie = [...session.cookies].map(([key, value]) => `${key}=${value}`).join("; ");
    if (session.csrf) headers["X-CSRF-TOKEN"] = session.csrf;
  }
  const init = { method, headers };
  if (body !== undefined) init.body = JSON.stringify(body);
  const response = await fetch(`${baseUrl}${path}`, init);
  for (const cookie of response.headers.getSetCookie()) {
    const [pair] = cookie.split(";");
    const [name, ...rest] = pair.split("=");
    session.cookies.set(name.trim(), rest.join("="));
  }
  const text = await response.text();
  const json = text ? JSON.parse(text) : null;
  if (!response.ok || (json && typeof json === "object" && "code" in json && json.code !== 0)) {
    throw new Error(`${method} ${path} -> ${response.status}: ${text.slice(0, 300)}`);
  }
  return json && typeof json === "object" && "data" in json ? json.data : json;
}

async function prepareServerData(baseUrl, echoPort) {
  const session = { cookies: new Map(), csrf: null, bearer: null };
  session.csrf = (await api(baseUrl, session, "GET", "/api/auth/antiforgery")).requestToken;
  await api(baseUrl, session, "POST", "/api/auth/login", { password: adminPassword });
  session.csrf = (await api(baseUrl, session, "GET", "/api/auth/antiforgery")).requestToken;
  const firstToken = (
    await api(baseUrl, session, "POST", "/api/auth/tokens", { name: "extension-tests-first" })
  ).token;
  const secondToken = (
    await api(baseUrl, session, "POST", "/api/auth/tokens", { name: "extension-tests-second" })
  ).token;
  session.bearer = firstToken;
  const provider = await api(baseUrl, session, "POST", "/api/providers", {
    name: "echo-provider",
    providerType: 0,
    description: null,
    endpoint: `http://127.0.0.1:${echoPort}/v1`,
    authConfigs: [{ authType: 0, apiKey: "sk-test", envKey: null, enable: true }],
    modelNames: ["echo-model"],
  });
  const relation = (await api(baseUrl, session, "GET", "/api/model-providers")).find(
    (item) => item.providerId === provider.id,
  );
  const agent = await api(baseUrl, session, "POST", "/api/agents", {
    displayName: "Echo",
    name: "echo",
    description: "Echoes the user message.",
    systemPrompt: "You echo the user.",
    modelProviderId: relation.id,
  });
  await mkdir(join(projectDirectory, "docs"), { recursive: true });
  await writeFile(join(projectDirectory, "readme.md"), "hello cloud\n");
  await writeFile(join(projectDirectory, "docs", "notes.md"), "project notes\n");
  const project = await api(baseUrl, session, "POST", "/api/projects", {
    name: "Extension tests",
    description: "Project for the extension integration tests.",
    workspace: projectDirectory,
    extraSetting: null,
  });
  // A2A 执行使用内置的 a2a Project，它默认的 ~/.agw/a2a 在测试数据目录之外。
  // A2A runs in the built-in a2a Project, whose default ~/.agw/a2a is outside the test data directory.
  const a2aWorkspace = join(root, "a2a-workspace");
  await mkdir(a2aWorkspace, { recursive: true });
  const a2aProject = (await api(baseUrl, session, "GET", "/api/projects")).find(
    (item) => item.name === "a2a",
  );
  await api(baseUrl, session, "PUT", `/api/projects/${a2aProject.id}`, {
    name: a2aProject.name,
    description: a2aProject.description ?? null,
    workspace: a2aWorkspace,
    extraSetting: a2aProject.extraSetting ?? null,
  });
  return { firstToken, secondToken, agentId: agent.id, agentName: "echo", projectId: project.id };
}

/** 把 fixture 复制到安装目录，替换本地服务端口。Copies fixtures into the install directory with the service port. */
async function installFixtures(httpPort) {
  const extensions = join(userData, "extensions");
  await mkdir(extensions, { recursive: true });
  for (const id of [
    "sample",
    "broken",
    "slow",
    "stuck-deactivate",
    "busy",
    "bad-view",
    "no-background",
    "undeclared",
    "react-ts",
    "wasm",
    "vue",
    "blazor",
  ]) {
    await cp(join(fixtures, id), join(extensions, id), { recursive: true });
    const manifestFile = join(extensions, id, "manifest.json");
    const manifest = await readFile(manifestFile, "utf8");
    await writeFile(manifestFile, manifest.replaceAll(HTTP_PORT_PLACEHOLDER, String(httpPort)));
  }
  // sample-v2.zip 由替换端口后的 sample 生成。sample-v2.zip is generated from the port-substituted sample.
  await writeSampleV2Zip(join(extensions, "sample"), join(fixtures, "sample-v2.zip"));
}

async function main() {
  await rm(root, { recursive: true, force: true });
  await mkdir(join(root, "logs"), { recursive: true });

  await run("pnpm", ["build:renderer"], { cwd: desktopDirectory });
  await rm(join(desktopDirectory, "resources", "renderer"), { recursive: true, force: true });
  await cp(
    join(desktopDirectory, "renderer", "out"),
    join(desktopDirectory, "resources", "renderer"),
    { recursive: true },
  );
  await build(mainBuildOptions);
  await buildExtensionSdk();
  await buildExtensionFixtures(fixtures);

  await run("dotnet", ["build", "src/server/Agw.Standalone.Host"], { cwd: repositoryRoot });
  const hostPort = await freePort();
  const hostLog = createWriteStream(join(root, "logs", "host.log"));
  const host = spawn(
    "dotnet",
    [
      "run",
      "--no-build",
      "--no-launch-profile",
      "--project",
      "src/server/Agw.Standalone.Host",
      "--",
      "--environment=Production",
      `--urls=http://127.0.0.1:${hostPort}`,
      `--AgwDataDir=${join(root, "server")}`,
      `--AgwLogDir=${join(root, "logs")}`,
      "--Database:Provider=sqlite",
      `--Database:ConnectionString=Data Source=${join(root, "server", "agw.db")}`,
      "--Execution:Provider=InProcess",
      "--DistributedLock:Provider=inmemory",
      `--Setup:AdminPassword=${adminPassword}`,
    ],
    { cwd: repositoryRoot, stdio: ["ignore", "pipe", "pipe"] },
  );
  children.add(host);
  host.stdout.pipe(hostLog);
  host.stderr.pipe(hostLog);
  const hostUrl = `http://127.0.0.1:${hostPort}`;
  await waitFor(async () => {
    const response = await fetch(`${hostUrl}/api/server-info`);
    return response.ok && (await response.json()).data?.initialized === true;
  }, "the Standalone Host");

  const echo = await startEchoService();
  const serverData = await prepareServerData(hostUrl, echo.port);
  const hubProxy = await startHubProxy(hostPort);
  const testService = await startTestService(hubProxy, echo);
  await installFixtures(testService.port);

  const remoteProfile = {
    id: "test-remote",
    kind: "remote",
    name: "Test Server",
    baseUrl: `http://127.0.0.1:${hubProxy.port}`,
    apiMajorVersion: 1,
    allowInsecureHttp: true,
  };
  await mkdir(userData, { recursive: true });
  await writeFile(
    join(userData, "settings.json"),
    `${JSON.stringify(
      {
        schemaVersion: 1,
        closeBehavior: "quit-desktop",
        profiles: [
          {
            id: "local",
            kind: "local",
            name: "Local",
            baseUrl: "http://127.0.0.1:30816",
            apiMajorVersion: 1,
            allowInsecureHttp: true,
          },
          remoteProfile,
        ],
        activeServerId: remoteProfile.id,
        projectTabsByServer: { [remoteProfile.id]: [serverData.projectId] },
      },
      null,
      2,
    )}\n`,
  );

  const entry = join(root, "main.cjs");
  await build({
    entryPoints: ["src/main/extensions.integration.ts"],
    outfile: entry,
    bundle: true,
    platform: "node",
    format: "cjs",
    target: "node24",
    external: ["electron"],
    logLevel: "warning",
  });

  for (const phase of ["first", "restart"]) {
    await writeFile(
      join(root, "config.json"),
      JSON.stringify(
        {
          phase,
          userData,
          fixtures,
          projectDirectory,
          remoteProfile,
          testServiceUrl: `http://127.0.0.1:${testService.port}`,
          hostUrl,
          ...serverData,
        },
        null,
        2,
      ),
    );
    console.log(`--- Electron (${phase}) ---`);
    await run(electron, [entry], { cwd: desktopDirectory });
  }
}

try {
  await main();
  console.log("PASS extension integration tests");
} catch (error) {
  console.error(error);
  process.exitCode = 1;
} finally {
  for (const child of children) child.kill();
  process.exit();
}
