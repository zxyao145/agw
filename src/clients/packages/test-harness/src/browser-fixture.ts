// 浏览器测试的测试页：用 Vite 构建（处理 CSS、字体与 `new Worker(new URL(...))`），用 Vite preview 提供，Playwright 的
// Chromium 打开。供 jsdom 不能运行的组件（例如 Monaco）测试使用。
// Browser-test pages: built with Vite (which handles CSS, fonts and `new Worker(new URL(...))`), served by Vite preview
// and opened in Playwright's Chromium. For components jsdom cannot run, such as Monaco.
import react from "@vitejs/plugin-react";
import { type Browser, chromium, type ConsoleMessage, type Page } from "playwright";
import { build, preview, type PreviewServer } from "vite";

export interface BrowserFixture {
  /** 测试页的地址，没有末尾的 `/`。The page origin without a trailing `/`. */
  origin: string;
  /**
   * 打开一个新页面；页面错误与控制台错误收集在 `errors` 中，测试结束时检查。
   * Opens a new page; page errors and console errors are collected in `errors` for the test to check.
   */
  newPage(): Promise<{ page: Page; errors: string[]; console: string[] }>;
  close(): Promise<void>;
}

export interface BrowserFixtureOptions {
  /** 含有 `index.html` 的测试页目录。The test page directory holding `index.html`. */
  root: string;
  /** 构建输出目录，应位于被 git 忽略的 `.scratch` 中。The build output, inside a git-ignored `.scratch`. */
  outDir: string;
}

/**
 * 构建并提供测试页。测试页目录中的 `postcss.config.mjs`（例如 Tailwind）由 Vite 读取。
 * Builds and serves the test page. Vite reads a `postcss.config.mjs` in the page directory, such as Tailwind's.
 */
export async function startBrowserFixture(options: BrowserFixtureOptions): Promise<BrowserFixture> {
  await build({
    root: options.root,
    logLevel: "warn",
    plugins: [react()],
    build: { outDir: options.outDir, emptyOutDir: true },
    worker: { format: "es" },
  });
  const server: PreviewServer = await preview({
    root: options.root,
    logLevel: "warn",
    build: { outDir: options.outDir },
    preview: { host: "127.0.0.1", port: 0, strictPort: false },
  });
  const url = server.resolvedUrls?.local[0];
  if (url === undefined) throw new Error("Vite preview has no local URL.");
  let browser: Browser;
  try {
    browser = await chromium.launch({ headless: true });
  } catch (error) {
    await server.close();
    throw error;
  }
  return {
    origin: url.replace(/\/$/u, ""),
    async newPage() {
      const page = await browser.newPage({ viewport: { width: 1200, height: 800 } });
      page.setDefaultTimeout(10_000);
      const errors: string[] = [];
      const console: string[] = [];
      page.on("pageerror", (error) => errors.push(error.stack ?? error.message));
      page.on("console", (message: ConsoleMessage) => {
        console.push(message.text());
        if (message.type() === "error") errors.push(message.text());
      });
      return { page, errors, console };
    },
    async close() {
      await browser.close();
      await server.close();
    },
  };
}
