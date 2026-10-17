import vue from "@vitejs/plugin-vue";
import { defineConfig } from "vite";

export default defineConfig({
  base: "./",
  plugins: [vue()],
  build: {
    // SDK 由 Desktop 在运行时提供。Desktop serves the SDK at runtime.
    rollupOptions: { external: ["/__agw/sdk.js"] },
  },
});
