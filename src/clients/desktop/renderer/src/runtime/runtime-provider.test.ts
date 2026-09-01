import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const RUNTIME_URL = new URL("./runtime-provider.tsx", import.meta.url);

test("Desktop runtime exposes renderer and platform markers on the document root", async () => {
  const source = await readFile(RUNTIME_URL, "utf8");

  assert.match(source, /document\.documentElement/);
  assert.match(source, /root\.dataset\.agwDesktop = String\(isDesktop\)/);
  assert.match(source, /root\.dataset\.agwPlatform = platform/);
  assert.match(source, /delete root\.dataset\.agwDesktop/);
  assert.match(source, /delete root\.dataset\.agwPlatform/);
});

test("Desktop runtime keeps its initial connection state stable during hydration", async () => {
  const source = await readFile(RUNTIME_URL, "utf8");

  assert.match(source, /useState<DesktopConnectionStatus>\("loading"\)/);
  assert.doesNotMatch(source, /isDesktop \? "loading" : "ready"/);
});

test("Desktop runtime opens HTTP links through the system-browser bridge", async () => {
  const source = await readFile(RUNTIME_URL, "utf8");

  assert.match(source, /document\.addEventListener\("click", openExternalLink, true\)/);
  assert.match(source, /closest<HTMLAnchorElement>\('a\[target="_blank"\]\[href\]'\)/);
  assert.match(source, /url\.protocol !== "http:" && url\.protocol !== "https:"/);
  assert.match(source, /bridge\.openExternal\(url\.toString\(\)\)/);
  assert.match(source, /event\.preventDefault\(\)/);
});

test("Desktop reconnects when the active profile token changes", async () => {
  const source = await readFile(RUNTIME_URL, "utf8");

  assert.match(source, /runtimeState\?\.activeToken === saved\.activeToken/);
});

test("Desktop publishes saved settings before reconnecting", async () => {
  const source = await readFile(RUNTIME_URL, "utf8");

  assert.match(
    source,
    /const saved = await bridge\.saveSettings\(settings\);[\s\S]*?setRuntimeState\(saved\);[\s\S]*?await connect\(saved\);/,
  );
});

test("Desktop refreshes an invalid local Bearer token before becoming ready", async () => {
  const source = await readFile(RUNTIME_URL, "utf8");

  assert.match(source, /\/api\/auth\/session/);
  assert.match(source, /body\.data\?\.accessMode === "bearer"/);
  assert.match(source, /profile\.kind === "local" && !token/);
  assert.match(source, /token = await bridge\.provisionLocalToken\(\)/);
});

test("Desktop isolates React Query caches per active Server", async () => {
  const source = await readFile(RUNTIME_URL, "utf8");

  assert.match(source, /ServerQueryClientRegistry/);
  assert.match(source, /createQueryClient/);
  assert.match(source, /queryClientRegistryRef\.current!\.get\(profile, token\)/);
  assert.match(source, /activateQueryClient\(profile, token\)/);
  assert.doesNotMatch(source, /hashToken|getServerCacheKey/);
});

test("Desktop keeps cache isolation before probing a newly selected Server", async () => {
  const source = await readFile(RUNTIME_URL, "utf8");
  const isolation = source.indexOf("activateQueryClient(profile, token);");
  const probe = source.indexOf("let info = await probeServer(profile, token");

  assert.notEqual(isolation, -1);
  assert.notEqual(probe, -1);
  assert.ok(isolation < probe);
});

test("Desktop prunes QueryClients for deleted profiles", async () => {
  const source = await readFile(RUNTIME_URL, "utf8");

  assert.match(source, /queryClientRegistryRef\.current!\.prune/);
  assert.match(source, /saved\.settings\.profiles\.map\(\(item\) => item\.id\)/);
});

test("Desktop cancels stale Server queries before activating the next cache", async () => {
  const source = await readFile(RUNTIME_URL, "utf8");

  assert.match(source, /queryClientRef\.current\?\.cancelQueries\(\)/);
});

test("Desktop aborts an in-flight connection when a newer connection starts", async () => {
  const source = await readFile(RUNTIME_URL, "utf8");

  assert.match(source, /connectGenerationRef\.current/);
  assert.match(source, /connectAbortRef\.current\?\.abort\(\)/);
  assert.match(source, /new AbortController\(\)/);
  assert.match(source, /generation !== connectGenerationRef\.current/);
  assert.match(source, /probeServer\(profile, token, abortController\.signal\)/);
});

test("Desktop ignores stale runtime state before mutating shared clients", async () => {
  const source = await readFile(RUNTIME_URL, "utf8");

  assert.match(
    source,
    /providedState \?\? \(await bridge\.getRuntimeState\(\)\);\s+if \(generation !== connectGenerationRef\.current\) return;\s+queryClientRegistryRef\.current!\.prune[\s\S]*?let profile/,
  );
  assert.match(
    source,
    /token = await bridge\.provisionLocalToken\(\);\s+if \(generation !== connectGenerationRef\.current\) return;/,
  );
  assert.match(
    source,
    /if \(generation !== connectGenerationRef\.current\) return;\s+configureClients\(profile, token\);\s+activateQueryClient/,
  );
});
