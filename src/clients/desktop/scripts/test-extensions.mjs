// extension 完整流程的集成测试：构建 → 启动 SQLite Standalone Host 与本地服务 → 三次运行 Electron（首次、重新启动与
// 性能测试）。
// Full-flow extension integration test: build, start a SQLite Standalone Host and local services, then run
// Electron three times (first launch, restart and performance).
import { spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import { once } from "node:events";
import { createWriteStream } from "node:fs";
import { cp, mkdir, readdir, readFile, rm, writeFile } from "node:fs/promises";
import { createServer } from "node:http";
import { connect, createServer as createTcpServer } from "node:net";
import { join, resolve } from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import electron from "electron";
import { build } from "esbuild";
import iconv from "iconv-lite";

import {
  buildExtensionFixtures,
  HTTP_PORT_PLACEHOLDER,
  writeSampleV2Zip,
} from "./build-extension-fixtures.mjs";
import { buildBundledExtensions } from "./build-bundled-extensions.mjs";
import { buildExtensionSdk } from "./build-extension-sdk.mjs";
import { buildDesktopMain } from "./build-main.mjs";

const desktopDirectory = resolve(".");
const repositoryRoot = resolve(desktopDirectory, "..", "..", "..");
const root = resolve(".scratch/extension-tests");
const userData = join(root, "user-data");
const fixtures = join(root, "fixtures");
const projectDirectory = join(root, "project");
const adminPassword = randomBytes(18).toString("hex");
const children = new Set();

const GUIDE_MARKDOWN = `# Guide

| Name | Value |
| --- | --- |
| alpha | 1 |

\`\`\`js
const answer = 42;
\`\`\`

![Logo](images/logo.png)

[Notes](docs/notes.md) and [Example](https://example.com/docs).

Long line: https://example.com/${"a".repeat(600)}

See the note[^note].

- [x] done
- [ ] todo

<b>raw</b>

[^note]: Guide note.
`;

// 两个元素的 Excalidraw 场景，其余字段由 restore 补全。A two-element scene; restore fills the other fields.
const DIAGRAM = {
  type: "excalidraw",
  version: 2,
  source: "https://excalidraw.com",
  elements: [
    { id: "box", type: "rectangle", x: 100, y: 100, width: 160, height: 80 },
    { id: "circle", type: "ellipse", x: 320, y: 100, width: 100, height: 100 },
  ],
  appState: { viewBackgroundColor: "#ffffff" },
  files: {},
};

// Edit 写回检查使用的文档：每种需要原样保留的写法各出现一次。front matter 以列表结束，CommonMark 读到的标题只有正文中的
// 两个，todo-editor 的 Outline 据此列出标题。
// The document for the Edit write-back checks: every construct that must be kept verbatim appears once. The front
// matter ends with a list, so CommonMark reads only the two headings of the body, which todo-editor's Outline lists.
const EDIT_MARKDOWN = `---
title: Edit sample
tags:
  - alpha
  - beta
---

# Edit sample

First paragraph with <kbd>inline HTML</kbd> and a footnote[^note].

Second paragraph with a [reference link][docs] at the end.

Related: [readme](../readme.md) and [example](https://example.com/related).

* star item one
* star item two

Setext heading
==============

<div class="note">
HTML block
</div>

[docs]: https://example.com/first
[docs]: https://example.com/duplicate

\`\`\`ts title=x
const x = 1;
\`\`\`

| Name | Value |
| --- | --- |
| alpha | 1 |

- [ ] open task
- [x] done task

[^note]: The footnote text.
`;

// 检查 Edit 与 Preview 排版的文档：任务列表（含嵌套列表）、表格、带定义的引用链接、行内代码、引用块中的代码块。
// The document for the Edit and Preview typography checks: a task list with a nested list, a table, a reference link
// with its definition, inline code, and a fenced code block inside a blockquote.
const TYPOGRAPHY_MARKDOWN = `# Typography sample

Top paragraph with a [typography link][typo] and \`inline code\`.

- [ ] first task
- [ ] second task
  - nested item

| Name | Value |
| --- | --- |
| alpha | 1 |

> \`\`\`ts
> const quoted = 1;
> \`\`\`

[typo]: https://example.com/typography
`;

// 检查拖动手柄与第一行对齐的文档：每种块各一个；图片文件不存在，Edit 显示占位。
// The document for the drag handle first-line checks: one block of each kind; the image file does not exist, so Edit
// shows a placeholder.
const FIRST_LINE_MARKDOWN = `---
title: First line
---

Paragraph line for the handle.

# Level one heading

### Level three heading

* Bullet item

- [ ] Task item

> Quoted line

\`\`\`ts
const line = 1;
\`\`\`

| Cell name | Value |
| --- | --- |
| alpha | 1 |

![Missing picture](images/missing.png)

---

<div class="first-line">
HTML block
</div>
`;

// 缩进不规范的 TypeScript：唯一缩进的一行为 4 个空格，Monaco 按 4 个空格缩进格式化。
// TypeScript with irregular indentation: the only indented line uses 4 spaces, so Monaco formats with 4 spaces.
const APP_TS = `function   add(a:number,b:number){
    return a+b}
const total=add( 1,2 )
`;

const CONFIG_JSON = `{
  // The port the service listens on.
  "port": 8080,
  /* Hosts allowed to connect. */
  "hosts": ["a", "b"],
}
`;

const CHINESE_TEXT =
  "这是一段用于测试编码识别的中文文字。文本编辑器需要正确地识别编码，并且按照原来的编码写回文件。\n第二行也是中文内容，包含标点符号：逗号、句号。\n";
const UTF8_BOM = [0xef, 0xbb, 0xbf];

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
        // 流式响应的用量在 [DONE] 之前单独发送。The streaming usage is sent on its own before [DONE].
        response.write(
          `data: ${JSON.stringify({
            id: "chatcmpl-echo",
            object: "chat.completion.chunk",
            created,
            model: body.model,
            choices: [],
            usage: { prompt_tokens: 1, completion_tokens: 1, total_tokens: 2 },
          })}\n\n`,
        );
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
  // 启用 summary 的 Agent：summary 的模型调用也由回显服务回答，它的用量不推送给客户端。
  // An Agent with summary enabled: the echo service also answers the summary model call, whose usage is not pushed.
  const summaryAgent = await api(baseUrl, session, "POST", "/api/agents", {
    displayName: "Echo with summary",
    name: "echo-summary",
    description: "Echoes the user message and summarizes the turn.",
    systemPrompt: "You echo the user.",
    modelProviderId: relation.id,
    enableSummary: true,
    summaryModelProviderId: relation.id,
  });
  await mkdir(join(projectDirectory, "docs"), { recursive: true });
  await writeFile(join(projectDirectory, "readme.md"), "hello cloud\n");
  await writeFile(join(projectDirectory, "docs", "notes.md"), "project notes\n");
  // 内置文件查看器使用的文件。Files for the bundled file viewers.
  await mkdir(join(projectDirectory, "images"), { recursive: true });
  await cp(
    join(desktopDirectory, "assets", "agw-logo.png"),
    join(projectDirectory, "images", "logo.png"),
  );
  await writeFile(join(projectDirectory, "guide.md"), GUIDE_MARKDOWN);
  await writeFile(
    join(projectDirectory, "diagram.excalidraw"),
    `${JSON.stringify(DIAGRAM, null, 2)}\n`,
  );
  await writeFile(join(projectDirectory, "blank.excalidraw"), "");
  // Text Editor 与 todo-editor 使用的文件。Files for Text Editor and todo-editor.
  await mkdir(join(projectDirectory, "notes"), { recursive: true });
  await writeFile(join(projectDirectory, "notes", "edit.md"), EDIT_MARKDOWN);
  await writeFile(join(projectDirectory, "notes", "typography.md"), TYPOGRAPHY_MARKDOWN);
  await writeFile(join(projectDirectory, "first-line.md"), FIRST_LINE_MARKDOWN);
  await mkdir(join(projectDirectory, "src"), { recursive: true });
  await writeFile(join(projectDirectory, "src", "app.ts"), APP_TS);
  await writeFile(join(projectDirectory, "config.json"), CONFIG_JSON);
  await writeFile(
    join(projectDirectory, "crlf.txt"),
    Buffer.concat([Buffer.from(UTF8_BOM), Buffer.from("first line\r\nsecond line\r\n")]),
  );
  await writeFile(
    join(projectDirectory, "utf16.txt"),
    Buffer.concat([
      Buffer.from([0xff, 0xfe]),
      Buffer.from("UTF-16 text: héllo wörld\nsecond line\n", "utf16le"),
    ]),
  );
  await writeFile(join(projectDirectory, "gbk.txt"), iconv.encode(CHINESE_TEXT, "gb18030"));
  await writeFile(
    join(projectDirectory, "data.bin"),
    Buffer.from([0x50, 0x4b, 3, 4, 0, 0, 20, 0, 8, 0]),
  );
  // UTF-8 BOM 之后不是 UTF-8：BOM 是唯一的候选，识别失败；按 windows-1252 可以无损读取。
  // Not UTF-8 after a UTF-8 BOM: the BOM is the only candidate, so detection fails; windows-1252 reads it losslessly.
  await writeFile(
    join(projectDirectory, "unknown.txt"),
    Buffer.concat([
      Buffer.from(UTF8_BOM),
      Buffer.from("Caf"),
      Buffer.from([0xe9]),
      Buffer.from(" au lait\n"),
    ]),
  );
  await writeFile(join(projectDirectory, "list.todo"), "- buy milk\n- write tests\n");
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
  return {
    firstToken,
    secondToken,
    agentId: agent.id,
    agentName: "echo",
    summaryAgentId: summaryAgent.id,
    projectId: project.id,
  };
}

const FENCE = "```";

/** paragraph 之间有空行，包含普通文字、表格、任务列表和代码块。Blank lines between blocks, with text, tables, task lists and code. */
function spacedUnit(index) {
  return [
    `## Section ${index}`,
    "",
    `Paragraph ${index} explains the change with **bold words**, \`inline code\`, a [link](https://example.com/${index}) ` +
      "and enough plain words to look like an ordinary answer from the assistant in a long conversation.",
    "",
    "| Name | Value | Note |",
    "| --- | ---: | --- |",
    `| alpha | ${index} | first row |`,
    `| beta | ${index + 1} | second row |`,
    "",
    `- [x] finished item ${index}`,
    `- [ ] open item ${index}`,
    "",
    `${FENCE}ts`,
    `const value${index} = ${index};`,
    `function double${index}(input: number) {`,
    "  return input * 2;",
    "}",
    FENCE,
    "",
  ].join("\n");
}

/** 标题与正文之间都没有空行，整篇没有空行。No blank line anywhere: each heading is followed directly by body text. */
function compactUnit(index) {
  return [
    `## Section title ${index}`,
    `Body line one for section ${index} with **bold** text and \`code\` that keeps going for a while.`,
    "Body line two continues the same paragraph with more ordinary words and a [link](https://example.com).",
  ].join("\n");
}

/** 标题与短表格交替，没有空行，单元格含代码中的 `|`。Headings alternate with short tables, no blank lines, cells hold a code `|`. */
function tableUnit(index) {
  return [`# Section ${index}`, "| a | b |", "| --- | --- |", `| \`x|y\` | ${index} |`].join("\n");
}

function typescriptUnit(index) {
  return [
    `export interface Record${index} {`,
    "  id: number;",
    "  name: string;",
    "}",
    "",
    `export function describe${index}(record: Record${index}): string {`,
    `  const parts = [record.name, String(record.id), "${index}"];`,
    '  return parts.join("-");',
    "}",
    "",
  ].join("\n");
}

/** 重复同一单元直到达到目标大小（KiB，内容都是 ASCII）。Repeats one unit up to the target size in KiB (ASCII content). */
function repeatUnit(unit, targetKiB) {
  const parts = [];
  let length = 0;
  for (let index = 0; length < targetKiB * 1024; index += 1) {
    const part = unit(index);
    parts.push(part);
    length += part.length + 1;
  }
  return `${parts.join("\n")}\n`;
}

/**
 * 性能测试的输入，生成到 Project 目录：Tiptap 渲染方案“性能测试”中三组内容的 158 KiB 与 631 KiB Markdown，以及
 * 10 MiB 的 TypeScript。返回相对 Project 目录的路径。
 * Performance inputs generated into the Project directory: 158 KiB and 631 KiB Markdown in the three compositions of
 * the Tiptap rendering design's performance tests, plus 10 MiB of TypeScript. Returns paths relative to the Project.
 */
async function writePerformanceInputs() {
  await mkdir(join(projectDirectory, "perf"), { recursive: true });
  const markdown = [];
  for (const kib of [158, 631]) {
    for (const [kind, unit] of [
      ["spaced", spacedUnit],
      ["compact", compactUnit],
      ["tables", tableUnit],
    ]) {
      const path = `perf/${kind}-${kib}.md`;
      await writeFile(join(projectDirectory, ...path.split("/")), repeatUnit(unit, kib));
      markdown.push({ path, kind, kib });
    }
  }
  const code = "perf/large.ts";
  await writeFile(
    join(projectDirectory, ...code.split("/")),
    repeatUnit(typescriptUnit, 10 * 1024),
  );
  return { markdown, code };
}

/** 把 fixture 复制到安装目录，替换本地服务端口。Copies fixtures into the install directory with the service port. */
/**
 * 把主题文件（CSS 与 SVG）中的端口占位符替换为本地 HTTP 服务端口，外部资源的检查据此确认没有请求。
 * Replaces the port placeholder in theme files (CSS and SVG) with the local HTTP service port, so the external
 * resource checks can confirm that no request was made.
 */
async function replacePortInThemes(directory, httpPort) {
  const themes = join(directory, "themes");
  const entries = await readdir(themes, { recursive: true, withFileTypes: true }).catch((error) => {
    if (error.code === "ENOENT") return [];
    throw error;
  });
  for (const entry of entries) {
    if (!entry.isFile() || !/\.(?:css|svg)$/u.test(entry.name)) continue;
    const file = join(entry.parentPath, entry.name);
    const text = await readFile(file, "utf8");
    await writeFile(file, text.replaceAll(HTTP_PORT_PLACEHOLDER, String(httpPort)));
  }
}

async function installFixtures(httpPort) {
  const extensions = join(userData, "extensions", "packages");
  await mkdir(extensions, { recursive: true });
  // css-theme 在测试中从 fixture 目录安装。css-theme installs from the fixture directory during the test.
  await replacePortInThemes(join(fixtures, "css-theme"), httpPort);
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
    "bad-theme",
  ]) {
    await cp(join(fixtures, id), join(extensions, id), { recursive: true });
    const manifestFile = join(extensions, id, "manifest.json");
    const manifest = await readFile(manifestFile, "utf8");
    await writeFile(manifestFile, manifest.replaceAll(HTTP_PORT_PLACEHOLDER, String(httpPort)));
    await replacePortInThemes(join(extensions, id), httpPort);
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
  await buildDesktopMain();
  await buildExtensionSdk();
  await buildBundledExtensions();
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

  for (const phase of ["first", "restart", "performance"]) {
    const performance = phase === "performance" ? await writePerformanceInputs() : null;
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
          performance,
          results: join(root, "performance.json"),
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
