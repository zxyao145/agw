import { spawn, type ChildProcess } from "node:child_process";
import { randomUUID } from "node:crypto";
import { once } from "node:events";
import { access, mkdir, mkdtemp, rm } from "node:fs/promises";
import { createServer } from "node:net";
import { resolve } from "node:path";
import { expect, test, type Route } from "@playwright/test";

type StandaloneHost = { root: string; origin: string };

const serverAssembly = resolve("../../server/Agw.Standalone.Host/bin/Debug/net10.0/agw-server.dll");
const scratch = resolve(".scratch/standalone-host");

export const standaloneHostTest = test.extend<{ standaloneHost: StandaloneHost }>({
  standaloneHost: async ({ page }, use) => {
    await access(serverAssembly);
    await mkdir(scratch, { recursive: true });
    const root = await mkdtemp(resolve(scratch, "run-"));
    try {
      const origin = await reserveOrigin();
      const server = startServer(root, origin);
      try {
        await waitForServer(server, origin);
        // 转发实际的 Server 响应，Web 页面使用同源 API 地址。
        // Forward real Server responses while the Web page uses same-origin API URLs.
        await page.route("**/api/**", async (route) => {
          const requestUrl = new URL(route.request().url());
          if (requestUrl.pathname === "/api/notifications/stream") {
            return route.continue({ url: standaloneRequestUrl(requestUrl, origin) });
          }
          await route.fulfill({ response: await fetchFromStandaloneHost(route, origin) });
        });
        await use({ root, origin });
      } finally {
        try {
          // 关闭页面会终止通知流，然后 Host 可以结束运行。
          // Closing the page ends the notification stream before the Host shuts down.
          await page.close();
        } finally {
          await stopServer(server);
        }
      }
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  },
});

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
