import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const APP_SHELL_URL = new URL("./app-shell.tsx", import.meta.url);

test("Desktop static-export routes treat dedicated and legacy Chat paths as the workspace", async () => {
  const source = await readFile(APP_SHELL_URL, "utf8");

  assert.ok(source.includes(String.raw`pathname.replace(/\/+$/u, "")`));
  assert.match(source, /CHAT_PATHS\.has\(normalizedPathname\)/);
  assert.match(source, /new Set\(\["\/chat", "\/desktop\/chat"\]\)/);
});

test("Desktop shell opens Projects through the title-bar picker", async () => {
  const source = await readFile(APP_SHELL_URL, "utf8");

  assert.match(source, /<DesktopProjectPicker/);
  assert.match(source, /rounded-xl bg-muted\/70 p-1/);
  assert.match(source, /onSelect=\{openProject\}/);
  assert.match(source, /normalizeProjectTabs\(tabs, projectIds, projectId\)/);
  assert.match(source, /persistTabs\(nextTabs\)/);
  assert.match(source, /router\.push\(buildChatHref\("\/desktop\/chat",/);
});

test("Desktop shell keeps every Chat action on the dedicated route", async () => {
  const source = await readFile(APP_SHELL_URL, "utf8");

  assert.match(source, /buildChatHref\("\/desktop\/chat"/);
  assert.match(source, /href="\/desktop\/chat\/"/);
  assert.doesNotMatch(source, /href="\/chat\/"/);
  assert.doesNotMatch(source, /router\.(?:push|replace)\(`\/chat/);
});

test("Desktop shell presents active executions as background conversations", async () => {
  const source = await readFile(APP_SHELL_URL, "utf8");

  assert.match(
    source,
    /<LoaderCircle className=\{cn\(hasBackgroundConversations && "animate-spin"\)\}/,
  );
  assert.match(source, /<span>Conversations<\/span>/);
  assert.match(
    source,
    /activity\.activeCount > 0 \? <span>\{activity\.activeCount\}<\/span> : null/,
  );
  assert.match(source, />Background conversations<\/DropdownMenuLabel>/);
  assert.match(source, />No background conversations<\/DropdownMenuItem>/);
});

test("Desktop shell switches Server profiles from the title bar", async () => {
  const source = await readFile(APP_SHELL_URL, "utf8");

  assert.match(source, /queryKey: \["projects", serverId\]/);
  assert.match(source, /<Popover open=\{serverPickerOpen\}/);
  assert.match(source, /aria-label="Switch server"/);
  assert.match(source, /serverProfiles\.map\(\(profile\)/);
  assert.match(source, /max-h-80 overflow-y-auto/);
  assert.match(source, /aria-selected=\{active\}/);
  assert.match(source, /activeServerId: nextServerId/);
});
