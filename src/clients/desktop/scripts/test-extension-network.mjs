// extension 网络限制的集成测试：先构建主进程与 SDK，再在 Electron 中运行测试入口。
// Integration test for extension network limits: build main and the SDK, then run the entry in Electron.
import { spawn } from "node:child_process";
import { mkdir } from "node:fs/promises";
import { resolve } from "node:path";
import electron from "electron";
import { build } from "esbuild";

import { mainBuildOptions } from "./build-main.mjs";
import { buildExtensionSdk } from "./build-extension-sdk.mjs";

const directory = resolve(".scratch/extension-network-build");
await mkdir(directory, { recursive: true });
await build(mainBuildOptions);
await buildExtensionSdk();
const entry = resolve(directory, "main.cjs");
await build({
  entryPoints: ["src/main/extension-network.integration.ts"],
  outfile: entry,
  bundle: true,
  platform: "node",
  format: "cjs",
  target: "node24",
  external: ["electron"],
  logLevel: "warning",
});
const child = spawn(electron, [entry], { stdio: "inherit" });
child.on("error", (error) => {
  throw error;
});
child.on("exit", (code) => {
  process.exitCode = code ?? 1;
});
