import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const SETTINGS_PAGE_URL = new URL("./desktop-settings-page.tsx", import.meta.url);

test("Desktop settings renders a dedicated About section", async () => {
  const source = await readFile(SETTINGS_PAGE_URL, "utf8");

  assert.match(source, /function DesktopAboutSection\(\)/);
  assert.match(source, /<section id="about"/);
  assert.match(source, /<h1[^>]*>About Agw Desktop<\/h1>/);
  assert.match(source, /runtimeState\.appVersion/);
  assert.match(source, /runtimeState\.architecture/);
  assert.match(source, /bridge\.checkForUpdates\(\)/);
  assert.match(source, /void checkForUpdates\(\)/);
  assert.match(source, /Download update/);
  assert.match(source, /Latest stable/);
  assert.match(
    source,
    /<ServerProfilesPanel \/>[\s\S]*<DesktopSettingsPanel \/>[\s\S]*<DesktopAboutSection \/>/,
  );
});
