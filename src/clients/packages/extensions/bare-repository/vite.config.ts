import { readFile } from "node:fs/promises";
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
      external: ["@agw/extension-sdk"],
      output: { paths: { "@agw/extension-sdk": "/__agw/sdk.js" } },
    },
  },
});
