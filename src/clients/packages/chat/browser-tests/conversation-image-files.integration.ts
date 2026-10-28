import assert from "node:assert/strict";
import { after, before, test, type TestContext } from "node:test";
import { spawn, type ChildProcess } from "node:child_process";
import { createServer, type Server } from "node:http";
import { mkdir, mkdtemp, open, readFile, rm, symlink, writeFile } from "node:fs/promises";
import { homedir } from "node:os";
import { relative, resolve } from "node:path";
import { randomUUID } from "node:crypto";
import { DatabaseSync } from "node:sqlite";
import type { AiMessage } from "@agw/api";
import { build } from "esbuild";
import { chromium, type BrowserContext, type Locator, type Page } from "playwright";
import type {} from "./conversation-image-files-fixture";

const repositoryRoot = resolve(process.cwd(), "../../../../");
const artifactRoot = resolve(repositoryRoot, ".scratch/chat-image-file-browser-tests");
const pngBytes = Buffer.from(
  "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==",
  "base64",
);
const svgBytes = Buffer.from(
  '<svg xmlns="http://www.w3.org/2000/svg" width="2" height="1"><rect width="1" height="1" fill="#ff0000"/><rect x="1" width="1" height="1" fill="#0000ff"/></svg>',
);
const scriptSvgBytes = Buffer.from(
  '<svg xmlns="http://www.w3.org/2000/svg" width="1" height="1"><script>window.svgFileExecuted = true;</script><rect width="1" height="1" onload="window.svgFileExecuted = true"/></svg>',
);
let pelicanBytes: Buffer;
const dataPng = `data:image/png;base64,${pngBytes.toString("base64")}`;

type ApiEnvelope<T> = { data: T };
type ProjectResponse = {
  id: string;
  additionalDirectories: { id: string; path: string }[];
};
type CreatedApiToken = { token: string };

let scopeDirectory = "";
let fixtureSite: Server | undefined;
let fixtureOrigin = "";
let apiOrigin = "";
let serverProcess: ChildProcess | undefined;
let serverOutput = "";
let projectId = "";
let apiToken = "";
let workspace = "";
let extraDirectory = "";
let extraDirectoryId = "";
let foreignProjectId = "";
let imageConversationId = "";
let savedImagePath = "";

before(async () => {
  await mkdir(artifactRoot, { recursive: true });
  scopeDirectory = await mkdtemp(resolve(artifactRoot, "run-"));

  const bundle = await build({
    entryPoints: ["browser-tests/conversation-image-files-fixture.tsx"],
    bundle: true,
    write: false,
    jsx: "automatic",
    platform: "browser",
    format: "iife",
    loader: { ".css": "empty" },
    define: { "process.env.NODE_ENV": '"development"', "process.env": "{}" },
  });

  fixtureSite = createServer((request, response) => {
    if (request.url === "/fixture.js") {
      response.writeHead(200, { "content-type": "text/javascript" });
      response.end(bundle.outputFiles[0].contents);
    } else if (request.url === "/external.png") {
      response.writeHead(200, { "content-type": "image/png" });
      response.end(pngBytes);
    } else if (request.url === "/") {
      response.writeHead(200, { "content-type": "text/html" });
      response.end('<!doctype html><div id="root"></div><script src="/fixture.js"></script>');
    } else {
      response.writeHead(404);
      response.end();
    }
  });
  await listen(fixtureSite);
  fixtureOrigin = getOrigin(fixtureSite);

  apiOrigin = await reserveOrigin();
  workspace = resolve(scopeDirectory, "workspace");
  extraDirectory = resolve(scopeDirectory, "additional-directory");
  await mkdir(extraDirectory, { recursive: true });
  pelicanBytes = await readFile(
    resolve(process.cwd(), "browser-tests/fixtures/pelican-bicycle.png"),
  );

  const dataDirectory = resolve(scopeDirectory, "server-data");
  const logDirectory = resolve(scopeDirectory, "server-logs");
  const databaseFile = resolve(dataDirectory, "database/agw.db");
  serverProcess = spawn(
    "dotnet",
    [
      resolve(repositoryRoot, "src/server/Agw.Standalone.Host/bin/Debug/net10.0/agw-server.dll"),
      `--AgwDataDir=${dataDirectory}`,
      `--AgwLogDir=${logDirectory}`,
      "--Database:Provider=sqlite",
      `--Database:ConnectionString=Data Source=${databaseFile}`,
      "--Execution:Provider=InProcess",
      `--Setup:AdminPassword=browser-image-${randomUUID()}`,
      `--Auth:AllowedOrigins:0=${fixtureOrigin}`,
      `--urls=${apiOrigin}`,
      "--environment=Production",
    ],
    {
      cwd: repositoryRoot,
      detached: process.platform !== "win32",
      stdio: ["ignore", "pipe", "pipe"],
    },
  );
  serverProcess.stdout?.on("data", appendServerOutput);
  serverProcess.stderr?.on("data", appendServerOutput);
  serverProcess.on("error", (error) => {
    serverOutput += `${error.stack ?? error.message}\n`;
  });

  await waitForServer();
  apiToken = await createApiToken();

  const project = await createProject("Markdown image browser regression", workspace, [
    { path: extraDirectory },
  ]);
  projectId = project.id;
  extraDirectoryId = project.additionalDirectories[0]?.id ?? "";
  assert.ok(extraDirectoryId);
  const foreignProject = await createProject(
    `Markdown image directory ownership ${randomUUID()}`,
    resolve(scopeDirectory, "foreign-workspace"),
  );
  foreignProjectId = foreignProject.id;
  savedImagePath = resolve(scopeDirectory, "captured-only.png");
  imageConversationId = seedImageHistory(databaseFile);

  await mkdir(resolve(workspace, "outputs"), { recursive: true });
  await mkdir(resolve(extraDirectory, "outputs"), { recursive: true });
  await Promise.all([
    writeFile(resolve(workspace, "outputs/absolute.png"), pngBytes),
    writeFile(resolve(workspace, "outputs/relative.png"), pngBytes),
    writeFile(resolve(workspace, "outputs/absolute.svg"), svgBytes),
    writeFile(resolve(workspace, "outputs/relative.svg"), svgBytes),
    writeFile(resolve(workspace, "outputs/script.svg"), scriptSvgBytes),
    writeFile(resolve(workspace, "outputs/pelican-bicycle.png"), pelicanBytes),
    writeFile(resolve(extraDirectory, "outputs/additional.svg"), svgBytes),
    writeFile(resolve(workspace, "outputs/readme.txt"), "not an image"),
    writeFile(resolve(scopeDirectory, "outside.png"), pngBytes),
  ]);
  await symlink(resolve(scopeDirectory, "outside.png"), resolve(workspace, "outputs/escape.png"));
  const largeImage = await open(resolve(workspace, "outputs/large.png"), "w");
  await largeImage.truncate(101 * 1024 * 1024);
  await largeImage.close();

  await assertImageApiContract();
});

after(async () => {
  await stopServer();
  if (fixtureSite) await closeServer(fixtureSite);
  if (scopeDirectory) await rm(scopeDirectory, { recursive: true, force: true });
});

test("authenticated Markdown images render with MdCard and remain available in preview", async (t) => {
  await verifyConversationImages(t, "md-card");
});

test("authenticated Markdown images render with TiptapMarkdown and remain available in preview", async (t) => {
  await verifyConversationImages(t, "tiptap");
});

for (const renderer of ["md-card", "tiptap"] as const) {
  test(`saved Result images render from history with ${renderer}`, async (t) => {
    const page = await openPage(t);
    await assertImageFailure("saved image path outside project", savedImagePath, 403, 403_0005);
    const response = await fetch(
      `${apiOrigin}/api/projects/${projectId}/conversations/${imageConversationId}/messages?pageSize=100`,
      { headers: { Authorization: `Bearer ${apiToken}` } },
    );
    assert.equal(response.status, 200);
    const history = (await response.json()) as ApiEnvelope<{ items: AiMessage[] }>;
    const results = history.data.items.filter(
      (message) => message.additionalProperties?.type === "result",
    );
    assert.equal(results.length, 2);
    const mount = async (messages: AiMessage[]) => {
      await page.evaluate((options) => window.conversationImageFilesFixture.mount(options), {
        apiBaseUrl: apiOrigin,
        apiToken,
        projectId,
        serverId: apiOrigin,
        conversationId: imageConversationId,
        directories: [{ id: null, name: "workspace", path: workspace, isPrimary: true }],
        markdown: "",
        renderer,
        messages,
      });
      await page.waitForFunction(() => {
        const first = document.querySelector<HTMLImageElement>('img[alt="First captured image"]');
        const updated = document.querySelector<HTMLImageElement>(
          'img[alt="Updated captured image"]',
        );
        return (
          first?.complete &&
          first.naturalWidth === 1 &&
          updated?.complete &&
          updated.naturalWidth === 1000
        );
      });
      assert.equal(await page.getByRole("alert").count(), 0);
      assert.equal(
        await page.locator('img[alt="First captured image"]').getAttribute("src"),
        dataPng,
      );
      assert.equal(
        await page.locator('img[alt="Updated captured image"]').getAttribute("src"),
        `data:image/png;base64,${pelicanBytes.toString("base64")}`,
      );
    };

    await mount(history.data.items);
    await page.reload();
    await page.waitForFunction(() => Boolean(window.conversationImageFilesFixture));
    await mount(results);
  });
}

function seedImageHistory(databaseFile: string): string {
  const database = new DatabaseSync(databaseFile);
  try {
    const id = randomUUID();
    const turnId = randomUUID();
    const producerId = randomUUID();
    const threadId = randomUUID();
    const providerTurnId = randomUUID();
    const owner = database
      .prepare("SELECT create_by FROM project WHERE id = ?")
      .get(projectId.toUpperCase()) as { create_by: string };
    assert.ok(owner);
    const createdAt = new Date().toISOString();
    database
      .prepare(
        "INSERT INTO project_conversation (id, project_id, context_id, title, create_by, create_time, generation, extension_id) VALUES (?, ?, ?, ?, ?, ?, 0, 'host')",
      )
      .run(
        id.toUpperCase(),
        projectId.toUpperCase(),
        id,
        "Saved Result images",
        owner.create_by,
        createdAt,
      );
    const insert = database.prepare(
      "INSERT INTO project_conversation_chat_history (id, project_conversation_id, task_id, turn_id, status, conversation_sequence, purpose, conversation_payload, create_time) VALUES (?, ?, ?, ?, 2, ?, ?, ?, ?)",
    );
    const insertMessage = (sequence: number, content: object, purpose: "message" | "result") => {
      const messageId = randomUUID();
      const payload = {
        role: "assistant",
        messageId,
        contents: [content],
        additionalProperties: {
          threadId,
          turnId,
          producerScopeId: producerId,
          conversationSequence: sequence,
          ...(purpose === "result" ? { type: "result", resultFormat: "markdown" } : {}),
        },
      };
      insert.run(
        messageId.toUpperCase(),
        id.toUpperCase(),
        producerId.toUpperCase(),
        turnId.toUpperCase(),
        sequence,
        purpose,
        JSON.stringify(payload),
        createdAt,
      );
    };
    const imageContent = (uri: string, itemId: string) => ({
      $type: "data",
      uri,
      additionalProperties: {
        threadId,
        turnId: providerTurnId,
        itemId,
        imageSource: "imageView",
        imagePath: savedImagePath,
      },
    });
    insertMessage(1, imageContent(dataPng, "image-1"), "message");
    insertMessage(
      2,
      { $type: "text", text: `![First captured image](${savedImagePath})` },
      "result",
    );
    insertMessage(
      3,
      imageContent(`data:image/png;base64,${pelicanBytes.toString("base64")}`, "image-2"),
      "message",
    );
    insertMessage(
      4,
      { $type: "text", text: `![Updated captured image](${savedImagePath})` },
      "result",
    );
    insertMessage(5, imageContent(dataPng, "image-3"), "message");
    return id;
  } finally {
    database.close();
  }
}

async function verifyConversationImages(t: TestContext, renderer: "md-card" | "tiptap") {
  const page = await openPage(t);
  const absolutePng = resolve(workspace, "outputs/absolute.png");
  const pelicanImage = resolve(workspace, "outputs/pelican-bicycle.png");
  const absoluteSvg = resolve(workspace, "outputs/absolute.svg");
  const additionalSvg = resolve(extraDirectory, "outputs/additional.svg");
  const externalUrl = `${fixtureOrigin}/external.png`;
  const markdown = [
    `![Absolute PNG](${absolutePng})`,
    `![Pelican bicycle](${pelicanImage})`,
    "![Relative PNG](outputs/relative.png)",
    `![Absolute SVG](${absoluteSvg})`,
    "![Relative SVG](outputs/relative.svg)",
    `![Additional SVG](${additionalSvg})`,
    `![External image](${externalUrl})`,
  ].join("\n\n");

  await page.evaluate((options) => window.conversationImageFilesFixture.mount(options), {
    apiBaseUrl: apiOrigin,
    apiToken,
    projectId,
    serverId: apiOrigin,
    directories: [
      { id: null, name: "workspace", path: workspace, isPrimary: true },
      {
        id: extraDirectoryId,
        name: "additional-directory",
        path: extraDirectory,
        isPrimary: false,
      },
    ],
    markdown,
    renderer,
    includeDataImage: true,
  });

  const unauthenticatedStatus = await page.evaluate(
    async ({ apiBaseUrl, projectId, path }) => {
      const url = new URL("/api/files/image", apiBaseUrl);
      url.searchParams.set("projectId", projectId);
      url.searchParams.set("path", path);
      const response = await fetch(url, { headers: { "X-Agw-Explicit-Auth": "1" } });
      return response.status;
    },
    { apiBaseUrl: apiOrigin, projectId, path: absolutePng },
  );
  assert.equal(unauthenticatedStatus, 401);

  const imageNames = [
    "Absolute PNG",
    "Pelican bicycle",
    "Relative PNG",
    "Absolute SVG",
    "Relative SVG",
    "Additional SVG",
    "External image",
    "Data image",
  ];
  await page.waitForFunction(
    (names) =>
      names.every((name) => {
        const image = [...document.querySelectorAll<HTMLImageElement>("img")].find(
          (item) => item.alt === name,
        );
        return image?.complete && image.naturalWidth > 0;
      }),
    imageNames,
  );
  assert.equal(await page.locator("img").count(), imageNames.length);

  for (const name of ["Absolute PNG", "Relative PNG"]) {
    const image = page.locator(`img[alt="${name}"]`);
    assert.equal(await image.evaluate((element) => (element as HTMLImageElement).naturalWidth), 1);
  }
  const pelican = page.locator('img[alt="Pelican bicycle"]');
  assert.equal(
    await pelican.evaluate((element) => (element as HTMLImageElement).naturalWidth),
    1000,
  );
  assert.equal(
    await pelican.evaluate((element) => (element as HTMLImageElement).naturalHeight),
    800,
  );
  for (const name of ["Absolute SVG", "Relative SVG", "Additional SVG"]) {
    const image = page.locator(`img[alt="${name}"]`);
    assert.equal(await image.evaluate((element) => (element as HTMLImageElement).naturalWidth), 2);
    assert.deepEqual(await pixels(image), [255, 0, 0, 255, 0, 0, 255, 255]);
  }

  assert.deepEqual(
    await pixels(page.locator('img[alt="Absolute PNG"]')),
    await pixels(page.locator('img[alt="External image"]')),
  );
  assert.equal(await page.locator('img[alt="External image"]').getAttribute("src"), externalUrl);
  assert.equal(await page.locator('img[alt="Data image"]').getAttribute("src"), dataPng);
  assert.deepEqual(
    await pixels(page.locator('img[alt="Absolute PNG"]')),
    await pixels(page.locator('img[alt="Data image"]')),
  );

  await page.locator('img[alt="Additional SVG"]').click();
  const preview = page.getByRole("dialog", { name: "Image preview" });
  await preview.waitFor();
  const previewImage = page.locator(".yarl__slide_image");
  await page.waitForFunction(() => {
    const image = document.querySelector<HTMLImageElement>(".yarl__slide_image");
    return image?.complete && image.naturalWidth === 2;
  });
  await page.evaluate(() => window.conversationImageFilesFixture.updateMarkdown(""));
  await page.waitForFunction(
    () => !document.querySelector('.agw-conversation-list img[alt="Additional SVG"]'),
  );
  assert.deepEqual(await pixels(previewImage), [255, 0, 0, 255, 0, 0, 255, 255]);
  await page.keyboard.press("Escape");
  await preview.waitFor({ state: "detached" });
}

test("authenticated script-bearing SVG navigation downloads without executing the image", async (t) => {
  const profile = await mkdtemp(resolve(scopeDirectory, "svg-download-profile-"));
  const context = await chromium.launchPersistentContext(profile, {
    headless: true,
    artifactsDir: artifactRoot,
    downloadsPath: artifactRoot,
    viewport: { width: 1200, height: 900 },
  });
  context.setDefaultTimeout(10_000);
  t.after(async () => {
    await context.close();
    await rm(profile, { recursive: true, force: true });
  });
  const page = context.pages()[0] ?? (await context.newPage());
  await page.addInitScript(() => {
    (window as Window & { svgFileExecuted?: boolean }).svgFileExecuted = false;
  });
  await page.goto(fixtureOrigin);
  await context.setExtraHTTPHeaders({
    Authorization: `Bearer ${apiToken}`,
    "X-Agw-Explicit-Auth": "1",
  });
  const imageUrl = new URL("/api/files/image", apiOrigin);
  imageUrl.searchParams.set("projectId", projectId);
  imageUrl.searchParams.set("path", "outputs/script.svg");
  const downloadPromise = page.waitForEvent("download");
  await page.evaluate((url) => {
    const link = document.createElement("a");
    link.href = url;
    document.body.append(link);
    link.click();
    link.remove();
  }, imageUrl.toString());
  const download = await downloadPromise;
  assert.equal(download.suggestedFilename(), "script.svg");
  assert.equal(await download.failure(), null);
  assert.equal(await page.evaluate(() => location.origin), fixtureOrigin);
  assert.equal(
    await page.evaluate(() => (window as Window & { svgFileExecuted?: boolean }).svgFileExecuted),
    false,
  );
});

test("a missing project image reports a load failure without breaking the conversation", async (t) => {
  const page = await openPage(t);
  const missing = resolve(workspace, "outputs/missing.png");
  await page.evaluate((options) => window.conversationImageFilesFixture.mount(options), {
    apiBaseUrl: apiOrigin,
    apiToken,
    projectId,
    serverId: apiOrigin,
    directories: [{ id: null, name: "workspace", path: workspace, isPrimary: true }],
    markdown: `![Missing image](${missing})`,
    renderer: "md-card" as const,
  });

  const error = page.getByRole("alert");
  await error.waitFor();
  assert.match((await error.textContent()) ?? "", /Unable to load image: Missing image/);
  assert.equal(await page.locator("img").count(), 0);
  assert.ok(await page.getByRole("list", { name: "Conversation messages" }).count());
});

async function openPage(t: TestContext): Promise<Page> {
  const profile = await mkdtemp(resolve(scopeDirectory, "profile-"));
  const context: BrowserContext = await chromium.launchPersistentContext(profile, {
    headless: true,
    artifactsDir: artifactRoot,
    downloadsPath: artifactRoot,
    viewport: { width: 1200, height: 900 },
  });
  context.setDefaultTimeout(10_000);
  t.after(async () => {
    await context.close();
    await rm(profile, { recursive: true, force: true });
  });
  const page = context.pages()[0] ?? (await context.newPage());
  page.setDefaultTimeout(10_000);
  page.on("pageerror", (error) => assert.fail(error.stack ?? error.message));
  await page.goto(fixtureOrigin);
  await page.waitForFunction(() => Boolean(window.conversationImageFilesFixture));
  return page;
}

async function pixels(image: Locator): Promise<number[]> {
  return image.evaluate((element) => {
    const source = element as HTMLImageElement;
    const canvas = document.createElement("canvas");
    canvas.width = source.naturalWidth;
    canvas.height = source.naturalHeight;
    const context = canvas.getContext("2d");
    if (!context) throw new Error("Canvas 2D is unavailable.");
    context.drawImage(source, 0, 0);
    return [...context.getImageData(0, 0, canvas.width, canvas.height).data];
  });
}

async function createApiToken(): Promise<string> {
  const antiforgery = await fetch(`${apiOrigin}/api/auth/antiforgery`);
  assert.equal(antiforgery.status, 200);
  const antiforgeryBody = (await antiforgery.json()) as ApiEnvelope<{ requestToken: string }>;
  const setCookie = antiforgery.headers.get("set-cookie");
  assert.ok(setCookie);
  const cookie = setCookie.split(";", 1)[0];

  const response = await fetch(`${apiOrigin}/api/auth/tokens`, {
    method: "POST",
    headers: {
      "content-type": "application/json",
      cookie,
      "X-CSRF-TOKEN": antiforgeryBody.data.requestToken,
    },
    body: JSON.stringify({ name: "Markdown image browser regression" }),
  });
  assert.equal(response.status, 200);
  const result = (await response.json()) as ApiEnvelope<CreatedApiToken>;
  assert.ok(result.data.token);
  return result.data.token;
}

async function createProject(
  name: string,
  projectWorkspace: string,
  additionalDirectories: { path: string }[] = [],
): Promise<ProjectResponse> {
  const response = await fetch(`${apiOrigin}/api/projects`, {
    method: "POST",
    headers: {
      "content-type": "application/json",
      Authorization: `Bearer ${apiToken}`,
    },
    body: JSON.stringify({
      name,
      description: null,
      workspace: projectWorkspace,
      extraSetting: null,
      additionalDirectories,
    }),
  });
  assert.equal(response.status, 200);
  const result = (await response.json()) as ApiEnvelope<ProjectResponse>;
  return result.data;
}

async function assertImageApiContract(): Promise<void> {
  const absolutePng = resolve(workspace, "outputs/absolute.png");
  const pelicanImage = resolve(workspace, "outputs/pelican-bicycle.png");
  const tildePath = relative(homedir(), absolutePng).replaceAll("\\", "/");
  assert.ok(tildePath !== ".." && !tildePath.startsWith("../"));

  await assertImageResponse(absolutePng, "image/png", pngBytes);
  await assertImageResponse(pelicanImage, "image/png", pelicanBytes);
  await assertImageResponse(`~/${tildePath}`, "image/png", pngBytes);
  await assertImageResponse("outputs/relative.png", "image/png", pngBytes);
  await assertImageResponse(resolve(workspace, "outputs/absolute.svg"), "image/svg+xml", svgBytes);
  await assertImageResponse("outputs/relative.svg", "image/svg+xml", svgBytes);
  await assertImageResponse("outputs/additional.svg", "image/svg+xml", svgBytes, extraDirectoryId);

  await assertImageFailure("missing project file", "outputs/missing.png", 404, 404_0007);
  await assertImageFailure("non-image file", "outputs/readme.txt", 400, 400_0001);
  await assertImageFailure("root traversal", "../../outside.png", 403, 403_0005);
  await assertImageFailure(
    "absolute path outside the project",
    resolve(scopeDirectory, "outside.png"),
    403,
    403_0005,
  );
  await assertImageFailure("symlink escape", "outputs/escape.png", 403, 403_0005);
  await assertImageFailure("file over 100 MiB", "outputs/large.png", 413, 413_0001);
  await assertImageFailure("unknown directory", "outputs/additional.svg", 404, 404_0007, {
    directoryId: randomUUID(),
  });
  await assertImageFailure(
    "directory from another project",
    "outputs/additional.svg",
    404,
    404_0007,
    {
      projectId: foreignProjectId,
      directoryId: extraDirectoryId,
    },
  );
  await assertImageFailure("unknown project", "outputs/absolute.png", 404, 404_0007, {
    projectId: randomUUID(),
  });
}

async function assertImageResponse(
  path: string,
  mimeType: string,
  expectedBytes: Uint8Array,
  directoryId?: string,
): Promise<void> {
  const response = await requestImage(path, { directoryId });
  assert.equal(response.status, 200, `Image request returned ${response.status} for ${path}`);
  assert.equal(response.headers.get("content-type"), mimeType);
  assert.deepEqual(Buffer.from(await response.arrayBuffer()), Buffer.from(expectedBytes));
}

async function assertImageFailure(
  label: string,
  path: string,
  status: number,
  code: number,
  options: { projectId?: string; directoryId?: string } = {},
): Promise<void> {
  const response = await requestImage(path, options);
  assert.equal(response.status, status, label);
  const body = (await response.json()) as { code: number };
  assert.equal(body.code, code, label);
}

async function requestImage(
  path: string,
  options: { projectId?: string; directoryId?: string } = {},
): Promise<Response> {
  const query = new URLSearchParams({ projectId: options.projectId ?? projectId, path });
  if (options.directoryId) query.set("directoryId", options.directoryId);
  return fetch(`${apiOrigin}/api/files/image?${query}`, {
    headers: { Authorization: `Bearer ${apiToken}` },
  });
}

async function waitForServer(): Promise<void> {
  const deadline = Date.now() + 120_000;
  let lastError: unknown;
  while (Date.now() < deadline) {
    if (serverProcess?.exitCode !== null || serverProcess?.signalCode !== null) {
      throw new Error(`Standalone Host exited before becoming ready.\n${serverOutput}`);
    }
    try {
      const response = await fetch(`${apiOrigin}/api/server-info`);
      if (response.ok) return;
    } catch (error) {
      lastError = error;
    }
    await new Promise((done) => setTimeout(done, 250));
  }
  throw new Error(`Standalone Host did not become ready: ${String(lastError)}\n${serverOutput}`);
}

async function listen(server: Server): Promise<void> {
  await new Promise<void>((resolveListen, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolveListen);
  });
}

function getOrigin(server: Server): string {
  const address = server.address();
  assert.ok(address && typeof address === "object");
  return `http://127.0.0.1:${address.port}`;
}

async function reserveOrigin(): Promise<string> {
  const server = createServer();
  await listen(server);
  const origin = getOrigin(server);
  await closeServer(server);
  return origin;
}

async function closeServer(server: Server): Promise<void> {
  await new Promise<void>((resolveClose, reject) => {
    server.close((error) => (error ? reject(error) : resolveClose()));
  });
}

function appendServerOutput(chunk: Buffer): void {
  serverOutput = `${serverOutput}${chunk.toString()}`.slice(-30_000);
}

async function stopServer(): Promise<void> {
  if (!serverProcess || serverProcess.exitCode !== null || serverProcess.signalCode !== null)
    return;
  const exited = new Promise<void>((resolveExit) =>
    serverProcess!.once("exit", () => resolveExit()),
  );
  if (process.platform === "win32" || !serverProcess.pid) {
    serverProcess.kill("SIGTERM");
  } else {
    process.kill(-serverProcess.pid, "SIGTERM");
  }
  let timeout: NodeJS.Timeout | undefined;
  const completed = await Promise.race([
    exited.then(() => true),
    new Promise<boolean>((resolveTimeout) => {
      timeout = setTimeout(() => resolveTimeout(false), 10_000);
    }),
  ]);
  if (timeout) clearTimeout(timeout);
  if (!completed) {
    if (process.platform === "win32" || !serverProcess.pid) {
      serverProcess.kill("SIGKILL");
    } else {
      process.kill(-serverProcess.pid, "SIGKILL");
    }
    await exited;
  }
}
