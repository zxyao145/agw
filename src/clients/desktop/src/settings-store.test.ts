import assert from "node:assert/strict";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";

import { DesktopSettingsStore, type SecretCodec } from "./settings-store";

const codec: SecretCodec = {
  encrypt: (value) => Buffer.from(`encrypted:${value}`, "utf8"),
  decrypt: (value) => value.toString("utf8").replace(/^encrypted:/u, ""),
};

test("settings store persists the default close behavior and local profile", async () => {
  const directory = await mkdtemp(join(tmpdir(), "agw-desktop-settings-"));
  try {
    const store = new DesktopSettingsStore(directory, "full", codec);

    const settings = await store.load();

    assert.equal(settings.closeBehavior, "minimize-to-tray");
    assert.equal(settings.packageFlavor, "full");
    assert.equal(settings.profiles[0]?.id, "local");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("settings store never writes a remote token as plaintext", async () => {
  const directory = await mkdtemp(join(tmpdir(), "agw-desktop-settings-"));
  try {
    const store = new DesktopSettingsStore(directory, "client", codec);

    await store.saveToken("remote-1", "agw_secret-token");

    const file = await readFile(join(directory, "secrets.json"), "utf8");
    assert.doesNotMatch(file, /agw_secret-token/u);
    assert.equal(await store.loadToken("remote-1"), "agw_secret-token");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
