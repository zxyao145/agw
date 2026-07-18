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

test("settings store persists multiple remote profiles", async () => {
  const directory = await mkdtemp(join(tmpdir(), "agw-desktop-settings-"));
  try {
    const store = new DesktopSettingsStore(directory, "client", codec);
    const settings = await store.load();

    await store.save({
      ...settings,
      activeServerId: "remote-2",
      profiles: [
        ...settings.profiles,
        {
          id: "remote-1",
          kind: "remote",
          name: "Office",
          baseUrl: "https://office.example.test",
          apiMajorVersion: 1,
          allowInsecureHttp: false,
        },
        {
          id: "remote-2",
          kind: "remote",
          name: "Lab",
          baseUrl: "https://lab.example.test",
          apiMajorVersion: 1,
          allowInsecureHttp: false,
        },
      ],
    });

    const reloaded = await store.load();
    assert.deepEqual(
      reloaded.profiles.map((profile) => profile.id),
      ["local", "remote-1", "remote-2"],
    );
    assert.equal(reloaded.activeServerId, "remote-2");

    await store.saveToken("remote-1", "agw_office-token");
    await store.saveToken("remote-2", "agw_lab-token");
    assert.equal(await store.loadToken("remote-1"), "agw_office-token");
    assert.equal(await store.loadToken("remote-2"), "agw_lab-token");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
