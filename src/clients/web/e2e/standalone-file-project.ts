import { execFileSync, spawn, type ChildProcess } from "node:child_process";
import { randomUUID } from "node:crypto";
import { once } from "node:events";
import { access, mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { createServer } from "node:net";
import { resolve } from "node:path";
import { expect, test, type APIRequestContext, type Route } from "@playwright/test";

export const STAGED_FILE = "export const count = 2;\n";
export const WORKING_FILE = "export const count = 3;\n";
export const GUIDE_FILE = "export const guide = true;\n";
export const NOTES_FILE = "export const note = 'extra';\n";

type FileProject = { id: string; origin: string; extraDirectoryId: string };

const serverAssembly = resolve("../../server/Agw.Standalone.Host/bin/Debug/net10.0/agw-server.dll");
const scratch = resolve(".scratch/standalone-file-project");

export const fileProjectTest = test.extend<{ fileProject: FileProject }>({
  fileProject: async ({ page }, use) => {
    await access(serverAssembly);
    await mkdir(scratch, { recursive: true });
    const root = await mkdtemp(resolve(scratch, "run-"));
    try {
      const workspace = resolve(root, "workspace");
      const extra = resolve(root, "extra");
      await seedFileRepositories(workspace, extra);
      const origin = await reserveOrigin();
      const server = startServer(root, origin);
      try {
        await waitForServer(server, origin);
        const project = await createProject(page.request, origin, workspace, extra);
        // 转发真实的 Server 响应，Web 页面仍使用同源 API 地址。
        // Forward real Server responses while the Web page uses same-origin API URLs.
        await page.route("**/api/**", async (route) => {
          const requestUrl = new URL(route.request().url());
          if (requestUrl.pathname === "/api/notifications/stream") {
            return route.continue({ url: standaloneRequestUrl(requestUrl, origin) });
          }
          await route.fulfill({ response: await fetchFromStandaloneHost(route, origin) });
        });
        await use({
          id: project.id,
          origin,
          extraDirectoryId: project.additionalDirectories[0].id,
        });
      } finally {
        await stopServer(server);
      }
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  },
});

async function seedFileRepositories(workspace: string, extra: string) {
  await seedRepository(workspace, {
    "calc.ts": "export const count = 1;\n",
    "guide.ts": "export const guide = false;\n",
    "deleted.ts": "export const deleted = true;\n",
  });
  await writeFile(resolve(workspace, "calc.ts"), STAGED_FILE);
  git(workspace, "add", "calc.ts");
  await writeFile(resolve(workspace, "calc.ts"), WORKING_FILE);
  await writeFile(resolve(workspace, "guide.ts"), GUIDE_FILE);
  await rm(resolve(workspace, "deleted.ts"));

  await seedRepository(extra, { "notes.ts": "export const note = 'initial';\n" });
  await writeFile(resolve(extra, "notes.ts"), NOTES_FILE);
}

async function seedRepository(directory: string, files: Record<string, string>) {
  await mkdir(directory, { recursive: true });
  git(directory, "init", "--quiet");
  for (const [path, text] of Object.entries(files)) {
    await writeFile(resolve(directory, path), text);
  }
  git(directory, "add", "--", ...Object.keys(files));
  git(directory, "commit", "--quiet", "-m", "fixture");
}

function git(directory: string, ...args: string[]) {
  execFileSync(
    "git",
    [
      "-c",
      "core.autocrlf=false",
      "-c",
      "commit.gpgsign=false",
      "-c",
      "user.name=Agw Tests",
      "-c",
      "user.email=tests@agw.local",
      ...args,
    ],
    { cwd: directory },
  );
}

async function reserveOrigin() {
  const reservation = createServer().listen(0, "127.0.0.1");
  await once(reservation, "listening");
  const address = reservation.address();
  if (!address || typeof address === "string") throw new Error("The test port is unavailable.");
  const closed = once(reservation, "close");
  reservation.close();
  await closed;
  return `http://127.0.0.1:${address.port}`;
}

function startServer(root: string, origin: string) {
  return spawn(
    "dotnet",
    [
      serverAssembly,
      "--environment=Production",
      `--urls=${origin}`,
      `--AgwDataDir=${resolve(root, "data")}`,
      `--AgwLogDir=${resolve(root, "logs")}`,
      "--Database:Provider=sqlite",
      `--Database:ConnectionString=Data Source=${resolve(root, "agw.db")}`,
      "--Execution:Provider=InProcess",
      `--Setup:AdminPassword=${randomUUID()}`,
    ],
    { cwd: root, stdio: ["ignore", "pipe", "pipe"] },
  );
}

async function waitForServer(server: ChildProcess, origin: string) {
  let output = "";
  server.stdout?.on("data", (chunk: Buffer) => {
    output += chunk.toString();
  });
  server.stderr?.on("data", (chunk: Buffer) => {
    output += chunk.toString();
  });
  await once(server, "spawn");
  await expect
    .poll(
      async () => {
        if (server.exitCode !== null || server.signalCode !== null) {
          throw new Error(`Standalone Host stopped during startup.\n${output}`);
        }
        try {
          return (await fetch(`${origin}/api/server-info`)).ok;
        } catch (error) {
          if (error instanceof TypeError) return false;
          throw error;
        }
      },
      { timeout: 20_000, message: "Standalone Host readiness" },
    )
    .toBe(true);
}

async function createProject(
  request: APIRequestContext,
  origin: string,
  workspace: string,
  extra: string,
) {
  const antiforgery = await request.get(`${origin}/api/auth/antiforgery`);
  await expect(antiforgery).toBeOK();
  const token = (await antiforgery.json()).data.requestToken;
  const created = await request.post(`${origin}/api/projects`, {
    headers: { "X-CSRF-TOKEN": token },
    data: {
      name: "File preview test",
      workspace,
      additionalDirectories: [{ path: extra }],
    },
  });
  await expect(created).toBeOK();
  return (await created.json()).data as { id: string; additionalDirectories: { id: string }[] };
}

export function fetchFromStandaloneHost(route: Route, origin: string) {
  const requestUrl = new URL(route.request().url());
  return route.fetch({ url: standaloneRequestUrl(requestUrl, origin), maxRedirects: 0 });
}

function standaloneRequestUrl({ pathname, search }: URL, origin: string) {
  return `${origin}${pathname}${search}`;
}

async function stopServer(server: ChildProcess) {
  if (!server.pid || server.exitCode !== null || server.signalCode !== null) return;
  const exited = once(server, "exit");
  server.kill("SIGTERM");
  await exited;
}
