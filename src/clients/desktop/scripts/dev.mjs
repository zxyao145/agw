import { spawn } from "node:child_process";
import { setTimeout as delay } from "node:timers/promises";

const pnpmCommand = process.platform === "win32" ? "pnpm.cmd" : "pnpm";
const rendererUrl = process.env.AGW_RENDERER_URL ?? "http://localhost:3000";
const children = new Set();
let stopping = false;

function run(args, options = {}) {
  return spawn(pnpmCommand, args, { stdio: "inherit", ...options });
}

function waitForExit(child) {
  return new Promise((resolvePromise, reject) => {
    child.once("error", reject);
    child.once("exit", (code, signal) => {
      if (code === 0 || stopping) resolvePromise();
      else reject(new Error(`${pnpmCommand} exited with ${signal ?? `code ${code}`}`));
    });
  });
}

async function waitForRenderer() {
  const deadline = Date.now() + 60_000;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(rendererUrl);
      if (response.ok) return;
    } catch {
      // The development server is still starting.
    }
    await delay(250);
  }
  throw new Error(`Desktop renderer did not become ready at ${rendererUrl}.`);
}

function stopChildren() {
  stopping = true;
  for (const child of children) child.kill();
}

process.once("SIGINT", stopChildren);
process.once("SIGTERM", stopChildren);

await waitForExit(run(["build:main"]));

const renderer = run(["dev:renderer"]);
children.add(renderer);

try {
  await waitForRenderer();
  const electron = run(["exec", "electron", "."], {
    env: { ...process.env, AGW_RENDERER_URL: rendererUrl },
  });
  children.add(electron);
  await waitForExit(electron);
} finally {
  stopChildren();
}
