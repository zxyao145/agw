import { fileURLToPath } from "node:url";

import { defineConfig } from "vite";

export default defineConfig({
  base: "./",
  build: {
    rollupOptions: {
      // 每个文件查看器是一个 HTML 入口。Each file viewer is an HTML entry.
      input: {
        editor: fileURLToPath(new URL("./editor.html", import.meta.url)),
        outline: fileURLToPath(new URL("./outline.html", import.meta.url)),
      },
      // SDK 由 Desktop 在运行时提供。Desktop serves the SDK at runtime.
      external: ["/__agw/sdk.js"],
    },
  },
});
