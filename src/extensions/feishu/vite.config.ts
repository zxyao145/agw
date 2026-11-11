import { resolve } from "node:path";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

const packageRoot = import.meta.dirname;

export default defineConfig({
  root: resolve(packageRoot, "desktop"),
  base: "./",
  plugins: [react()],
  build: {
    outDir: resolve(packageRoot, "dist/desktop"),
    emptyOutDir: true,
    rolldownOptions: {
      external: ["@agw/extension-sdk"],
      output: { paths: { "@agw/extension-sdk": "/__agw/sdk.js" } },
    },
  },
});
