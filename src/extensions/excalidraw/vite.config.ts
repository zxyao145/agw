import { readdir, readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import { dirname, join, relative, sep } from "node:path";
import { fileURLToPath } from "node:url";
import react from "@vitejs/plugin-react";
import { defineConfig, type Plugin } from "vite";

const require = createRequire(import.meta.url);
const manifestPath = fileURLToPath(new URL("./manifest.json", import.meta.url));
// Excalidraw 的 JS 按 `<EXCALIDRAW_ASSET_PATH>/fonts/<字体>/<文件>` 加载字体，这些路径不经过打包器。
// Excalidraw's JS loads fonts from `<EXCALIDRAW_ASSET_PATH>/fonts/<family>/<file>`, paths the bundler does not see.
const fontsDirectory = join(dirname(require.resolve("@excalidraw/excalidraw")), "fonts");

/**
 * 把 manifest.json 与 Excalidraw 字体（`fonts/`）放进产物，`dist/` 是完整的 extension 包。
 * Emits manifest.json and the Excalidraw fonts (`fonts/`), so `dist/` is a complete extension package.
 */
function extensionPackage(): Plugin {
  return {
    name: "agw-extension-package",
    async generateBundle() {
      this.emitFile({
        type: "asset",
        fileName: "manifest.json",
        source: await readFile(manifestPath),
      });
      const entries = await readdir(fontsDirectory, { recursive: true, withFileTypes: true });
      for (const entry of entries.filter((item) => item.isFile())) {
        const path = join(entry.parentPath, entry.name);
        this.emitFile({
          type: "asset",
          fileName: `fonts/${relative(fontsDirectory, path).split(sep).join("/")}`,
          source: await readFile(path),
        });
      }
    },
  };
}

export default defineConfig({
  base: "./",
  plugins: [react(), extensionPackage()],
  build: {
    outDir: "dist",
    emptyOutDir: true,
    rolldownOptions: {
      // SDK 由 Desktop 在 `/__agw/sdk.js` 提供。Desktop serves the SDK at `/__agw/sdk.js`.
      external: ["@agw/extension-sdk"],
      output: { paths: { "@agw/extension-sdk": "/__agw/sdk.js" } },
    },
  },
});
