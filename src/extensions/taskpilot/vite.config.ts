import { fileURLToPath } from "node:url";
import { resolve } from "node:path";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

const packageRoot = fileURLToPath(new URL(".", import.meta.url));

export default defineConfig({
  root: resolve(packageRoot, "desktop"),
  base: "./",
  plugins: [react()],
  build: {
    outDir: resolve(packageRoot, "dist/desktop"),
    emptyOutDir: true,
    rolldownOptions: {
      input: { main: resolve(packageRoot, "desktop/main.html") },
      external: ["@agw/extension-sdk"],
      output: { paths: { "@agw/extension-sdk": "/__agw/sdk.js" } },
    },
  },
});
