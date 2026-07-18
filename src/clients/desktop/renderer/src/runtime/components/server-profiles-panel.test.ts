import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const PANEL_URL = new URL("./server-profiles-panel.tsx", import.meta.url);

test("Server profiles render as a list with add, edit, and delete actions", async () => {
  const source = await readFile(PANEL_URL, "utf8");

  assert.match(source, /settings\.profiles\.map\(\(profile\)/);
  assert.match(source, /aria-label="Add remote Server"/);
  assert.match(source, /openEditProfile\(profile\)/);
  assert.match(source, /deleteProfile\(profile\)/);
  assert.match(source, /profile\.kind === "remote" \? "Remote" : "Local"/);
});

test("Adding a remote Server uses a modal and a unique profile ID", async () => {
  const source = await readFile(PANEL_URL, "utf8");

  assert.match(source, /Configure a remote Server/);
  assert.match(source, /`remote-\$\{crypto\.randomUUID\(\)\}`/);
  assert.match(source, /\[\.\.\.settings\.profiles, profile\]/);
  assert.match(source, /saveToken\(profileId, draft\.token\.trim\(\)\)/);
});

test("Deleting the active remote Server falls back to local", async () => {
  const source = await readFile(PANEL_URL, "utf8");

  assert.match(source, /settings\.activeServerId === profile\.id \? "local"/);
  assert.match(source, /delete projectTabsByServer\[profile\.id\]/);
  assert.match(source, /deleteToken\(profile\.id\)/);
});
