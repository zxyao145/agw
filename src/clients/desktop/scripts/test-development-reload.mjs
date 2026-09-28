import assert from "node:assert/strict";
import { cp, mkdir, readFile, rm, stat, utimes, writeFile } from "node:fs/promises";
import { join, resolve } from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import { build } from "esbuild";
import electron from "electron";

import { watchMain } from "./watch-main.mjs";

const directory = resolve(".scratch/development-reload");
await cp("renderer/out", "resources/renderer", { recursive: true });
await mkdir(join(directory, "user-data"), { recursive: true });
await rm(join(directory, "ready.json"), { force: true });
await writeFile(
  join(directory, "user-data", "settings.json"),
  JSON.stringify({
    schemaVersion: 1,
    activeServerId: "image-menu-test",
    profiles: [
      {
        id: "local",
        kind: "local",
        name: "Local",
        baseUrl: "http://127.0.0.1:30816",
        apiMajorVersion: 1,
        allowInsecureHttp: true,
      },
      {
        id: "image-menu-test",
        kind: "remote",
        name: "Image menu test",
        baseUrl: "http://127.0.0.1:1",
        apiMajorVersion: 1,
        allowInsecureHttp: true,
      },
    ],
  }),
);
const entry = join(directory, "main.cjs");
await build({
  entryPoints: ["src/main/development-reload.integration.ts"],
  outfile: entry,
  bundle: true,
  platform: "node",
  format: "cjs",
  target: "node24",
  external: ["electron"],
});

async function waitForReady(previousPid) {
  const deadline = Date.now() + 30_000;
  while (Date.now() < deadline) {
    try {
      const { pid } = JSON.parse(await readFile(join(directory, "ready.json"), "utf8"));
      if (pid !== previousPid) return pid;
    } catch (error) {
      if (error.code !== "ENOENT") throw error;
    }
    await delay(100);
  }
  throw new Error("Electron did not register the image menu after rebuilding.");
}

const watcher = await watchMain({
  command: electron,
  args: [entry],
  options: { stdio: "inherit" },
});
const exited = watcher.closed.then(() => {
  throw new Error("Electron exited before the development reload checks completed.");
});
const touched = [];
try {
  let pid = await Promise.race([waitForReady(), exited]);
  console.log("PASS application startup registers the image menu through the production preload");
  for (const path of ["src/main/image-context-menu.ts", "src/preload/index.ts"]) {
    const original = await stat(path);
    touched.push({ path, original });
    await utimes(path, original.atime, new Date());
    const nextPid = await Promise.race([waitForReady(pid), exited]);
    assert.notEqual(nextPid, pid);
    assert.throws(() => process.kill(pid, 0), { code: "ESRCH" });
    pid = nextPid;
    console.log(`PASS ${path} rebuilds both entry points and restarts Electron with working IPC`);
  }
} finally {
  await watcher.dispose();
  for (const { path, original } of touched) await utimes(path, original.atime, original.mtime);
}
