import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

export default defineConfig({
  base: "./",
  plugins: [
    react(),
    {
      name: "agw-extension-package",
      async generateBundle() {
        this.emitFile({
          type: "asset",
          fileName: "manifest.json",
          source: await readFile(new URL("./manifest.json", import.meta.url), "utf8"),
        });
      },
    },
  ],
  build: {
    outDir: "dist",
    emptyOutDir: true,
    rolldownOptions: {
      input: {
        index: fileURLToPath(new URL("./index.html", import.meta.url)),
        sidebar: fileURLToPath(new URL("./sidebar.html", import.meta.url)),
        file: fileURLToPath(new URL("./file.html", import.meta.url)),
      },
      external: ["@agw/extension-sdk"],
      output: { paths: { "@agw/extension-sdk": "/__agw/sdk.js" } },
    },
  },
});
