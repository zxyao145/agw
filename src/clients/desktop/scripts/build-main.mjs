// Bundle the Electron main and preload entry points so the packaged application needs no
// runtime node_modules; workspace dependencies are hoisted to the monorepo root.
// 把 Electron 主进程与 preload 入口打包成单文件，使打包后的应用不依赖运行时 node_modules；
// 工作区依赖已提升到 monorepo 根目录。
import { build } from "esbuild";

await build({
  entryPoints: {
    "main/index": "src/main/index.ts",
    "preload/index": "src/preload/index.ts",
  },
  outdir: "dist",
  bundle: true,
  platform: "node",
  format: "cjs",
  target: "node24",
  external: ["electron"],
  logLevel: "info",
});
