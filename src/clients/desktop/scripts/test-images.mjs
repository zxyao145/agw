import { mkdir } from "node:fs/promises";
import { resolve } from "node:path";
import { spawn } from "node:child_process";
import { build } from "esbuild";
import electron from "electron";

const directory = resolve(".scratch/image-tests");
await mkdir(directory, { recursive: true });
const entry = resolve(directory, "main.cjs");
await build({
  entryPoints: ["src/main/image-context-menu.integration.ts"],
  outfile: entry,
  bundle: true,
  platform: "node",
  format: "cjs",
  target: "node24",
  external: ["electron"],
});
await build({
  entryPoints: ["src/preload/index.ts"],
  outfile: resolve(directory, "preload.cjs"),
  bundle: true,
  platform: "node",
  format: "cjs",
  target: "node24",
  external: ["electron"],
});
const child = spawn(electron, [entry], { stdio: "inherit" });
child.on("error", (error) => {
  throw error;
});
child.on("exit", (code) => {
  process.exitCode = code ?? 1;
});
