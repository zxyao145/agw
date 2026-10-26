import { defineConfig } from "@playwright/test";

import base from "./playwright.config";

// 在生产静态导出（Turbopack）上运行 Files 代码查看器的检查：Monaco 的 CSS、字体与 worker 由 Turbopack 打包。
// Runs the Files code view checks against the production static export (Turbopack), which bundles Monaco's CSS,
// fonts and workers.
export default defineConfig({
  ...base,
  testMatch: "file-code-view.spec.ts",
  use: { ...base.use, baseURL: "http://127.0.0.1:3102" },
  webServer: {
    // AGW_E2E 把 distDir 设为 .next-e2e，静态导出写在 distDir 中，不影响开发服务器的 .next。AGW_E2E sets distDir to
    // .next-e2e, where the static export is written, leaving the dev server's .next alone.
    command: "pnpm exec next build && pnpm exec serve .next-e2e --listen tcp://127.0.0.1:3102",
    env: { NEXT_OUTPUT_MODE: "export", AGW_E2E: "1" },
    url: "http://127.0.0.1:3102/chat/",
    reuseExistingServer: false,
    timeout: 600_000,
  },
});
