// Bundle the Electron main and preload entry points so the packaged application needs no
// runtime node_modules; workspace dependencies are hoisted to the monorepo root.
// 把 Electron 主进程与 preload 入口打包成单文件，使打包后的应用不依赖运行时 node_modules；
// 工作区依赖已提升到 monorepo 根目录。
import { build } from "esbuild";
import { pathToFileURL } from "node:url";

export const mainBuildOptions = {
  entryPoints: {
    "main/index": "src/main/index.ts",
    "preload/index": "src/preload/index.ts",
    "preload/extension": "src/preload/extension.ts",
    "preload/confirmation": "src/preload/confirmation.ts",
  },
  outdir: "dist",
  bundle: true,
  platform: "node",
  format: "cjs",
  target: "node24",
  external: ["electron"],
  logLevel: "info",
};

// 确认窗口的页面脚本与样式：浏览器环境，由主进程的专用本地资源入口提供。
// The confirmation window's page script and styles: a browser bundle served by the main process's dedicated local
// resource entry.
export const confirmationBuildOptions = {
  entryPoints: ["src/confirmation/confirmation.ts", "src/confirmation/confirmation.css"],
  outdir: "dist/confirmation",
  bundle: true,
  platform: "browser",
  format: "iife",
  target: "chrome140",
  logLevel: "info",
};

/** 构建主进程、preload 与确认页面。Builds the main process, the preloads and the confirmation page. */
export async function buildDesktopMain() {
  await Promise.all([build(mainBuildOptions), build(confirmationBuildOptions)]);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  await buildDesktopMain();
}
