import { execFile, spawn } from "node:child_process";
import { mkdir } from "node:fs/promises";
import { resolve } from "node:path";
import { promisify } from "node:util";
import { build } from "esbuild";
import electron from "electron";

import { buildDesktopMain } from "./build-main.mjs";

const directory = resolve(".scratch/floating-chat-component");
await mkdir(directory, { recursive: true });
if (process.platform !== "darwin") {
  throw new Error("The native mouse component test requires macOS.");
}
await promisify(execFile)("xcrun", [
  "swiftc",
  "scripts/native-mouse.swift",
  "-o",
  resolve(directory, "native-mouse"),
]);
await buildDesktopMain();
const entry = resolve(directory, "main.cjs");
await build({
  entryPoints: ["src/main/floating-chat.integration.ts"],
  outfile: entry,
  bundle: true,
  platform: "node",
  format: "cjs",
  target: "node24",
  external: ["electron"],
});
await new Promise((resolveRun, reject) => {
  const child = spawn(electron, [entry], { stdio: "inherit" });
  child.once("error", reject);
  child.once("exit", (code) => {
    if (code === 0) resolveRun();
    else reject(new Error(`Floating Chat component tests exited with ${code}.`));
  });
});
