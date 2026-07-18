import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const BOUNDARY_URL = new URL("./desktop-workspace-boundary.tsx", import.meta.url);
const APP_LAYOUT_URL = new URL("../app/(app)/layout.tsx", import.meta.url);
const APP_ERROR_URL = new URL("../app/(app)/error.tsx", import.meta.url);
const LEGACY_CHAT_PAGE_URL = new URL("../app/(app)/chat/page.tsx", import.meta.url);

test("Desktop isolates route errors inside the workspace", async () => {
  const [boundarySource, layoutSource, errorSource] = await Promise.all([
    readFile(BOUNDARY_URL, "utf8"),
    readFile(APP_LAYOUT_URL, "utf8"),
    readFile(APP_ERROR_URL, "utf8"),
  ]);

  assert.match(layoutSource, /<AppShell>[\s\S]*?<DesktopWorkspaceBoundary>/);
  assert.match(
    boundarySource,
    /const routeKey = `\$\{pathname\}\?\$\{searchParams\.toString\(\)\}`/,
  );
  assert.match(boundarySource, /<ErrorBoundary resetKeys=\{\[routeKey\]\}/);
  assert.match(boundarySource, /<React\.Suspense fallback=\{<DesktopWorkspaceLoading \/>\}>/);
  assert.match(boundarySource, /resetQueryErrors\(\)/);
  assert.match(boundarySource, /router\.refresh\(\)/);
  assert.match(errorSource, /<DesktopWorkspaceErrorState error=\{error\} onRetry=\{reset\}/);
});

test("Desktop workspace errors keep recovery navigation available", async () => {
  const source = await readFile(BOUNDARY_URL, "utf8");

  assert.match(source, /Desktop is still running/);
  assert.match(source, /href="\/desktop\/chat\/"/);
  assert.match(source, /href="\/settings\/"/);
  assert.match(source, /Try again/);
});

test("Desktop exports the legacy Chat route instead of returning a document error", async () => {
  const source = await readFile(LEGACY_CHAT_PAGE_URL, "utf8");

  assert.match(source, /DesktopChatPage as default/);
});
