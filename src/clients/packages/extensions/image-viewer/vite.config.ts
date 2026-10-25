import { readFile } from "node:fs/promises";

import react from "@vitejs/plugin-react";
import { defineConfig, type Plugin } from "vite";

const SDK_PACKAGE = "@agw/extension-sdk";

/** 把包根目录的 manifest.json 输出到 dist/。Emits the package-root manifest.json into dist/. */
function emitManifest(): Plugin {
  const manifestUrl = new URL("./manifest.json", import.meta.url);
  return {
    name: "agw-emit-manifest",
    apply: "build",
    async generateBundle() {
      this.emitFile({
        type: "asset",
        fileName: "manifest.json",
        source: await readFile(manifestUrl, "utf8"),
      });
    },
  };
}

export default defineConfig({
  base: "./",
  plugins: [react(), emitManifest()],
  build: {
    outDir: "dist",
    emptyOutDir: true,
    rolldownOptions: {
      // SDK 由 Desktop 在运行时提供。Desktop serves the SDK at runtime.
      external: [SDK_PACKAGE],
      output: { paths: { [SDK_PACKAGE]: "/__agw/sdk.js" } },
    },
  },
});
