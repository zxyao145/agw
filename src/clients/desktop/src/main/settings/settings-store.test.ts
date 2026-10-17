import assert from "node:assert/strict";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";

import { writeFile } from "node:fs/promises";
import { createScratchDirectory } from "../scratch-directory.test-helper";
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

test("concurrent token updates preserve both credentials", async () => {
  const directory = await mkdtemp(join(tmpdir(), "agw-desktop-settings-"));
  try {
    const store = new DesktopSettingsStore(directory, "client", codec);
    await Promise.all([
      store.saveToken("remote-1", "agw_first"),
      store.saveToken("remote-2", "agw_second"),
    ]);
    assert.equal(await store.loadToken("remote-1"), "agw_first");
    assert.equal(await store.loadToken("remote-2"), "agw_second");
    await Promise.all([store.deleteToken("remote-1"), store.saveToken("remote-3", "agw_third")]);
    assert.equal(await store.loadToken("remote-1"), null);
    assert.equal(await store.loadToken("remote-2"), "agw_second");
    assert.equal(await store.loadToken("remote-3"), "agw_third");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("concurrent settings saves complete in call order", async () => {
  const directory = await mkdtemp(join(tmpdir(), "agw-desktop-settings-"));
  try {
    const store = new DesktopSettingsStore(directory, "client", codec);
    const settings = await store.load();
    await Promise.all([
      store.save({ ...settings, closeBehavior: "minimize-to-tray" }),
      store.save({ ...settings, closeBehavior: "quit-desktop" }),
    ]);
    assert.equal((await store.load()).closeBehavior, "quit-desktop");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("a failed queued credential write does not block later updates", async () => {
  const directory = await mkdtemp(join(tmpdir(), "agw-desktop-settings-"));
  try {
    const store = new DesktopSettingsStore(directory, "client", {
      ...codec,
      encrypt: (value) => {
        if (value === "agw_rejected") throw new Error("Encryption unavailable");
        return codec.encrypt(value);
      },
    });
    await Promise.all([
      assert.rejects(store.saveToken("remote-1", "agw_rejected"), /Encryption unavailable/u),
      store.saveToken("remote-2", "agw_second"),
    ]);
    assert.equal(await store.loadToken("remote-1"), null);
    assert.equal(await store.loadToken("remote-2"), "agw_second");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("concurrent tab and active-server updates preserve both intents", async () => {
  const directory = await mkdtemp(join(tmpdir(), "agw-desktop-settings-"));
  try {
    const store = new DesktopSettingsStore(directory, "client", codec);
    const settings = await store.load();
    await store.save({
      ...settings,
      profiles: [
        ...settings.profiles,
        {
          id: "remote-1",
          kind: "remote",
          name: "Remote",
          baseUrl: "https://remote.example.test",
          apiMajorVersion: 1,
          allowInsecureHttp: false,
        },
      ],
    });
    await Promise.all([
      store.save({ projectTabsByServer: { local: ["project-a"] } }),
      store.save({ activeServerId: "remote-1" }),
      store.save({ projectTabsByServer: { "remote-1": ["project-b"] } }),
      store.save({ closeBehavior: "quit-desktop" }),
    ]);
    const saved = await store.load();
    assert.equal(saved.activeServerId, "remote-1");
    assert.deepEqual(saved.projectTabsByServer, {
      local: ["project-a"],
      "remote-1": ["project-b"],
    });
    assert.equal(saved.closeBehavior, "quit-desktop");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("profile deletion prunes its tabs without reverting a concurrent server selection", async () => {
  const directory = await mkdtemp(join(tmpdir(), "agw-desktop-settings-"));
  try {
    const store = new DesktopSettingsStore(directory, "client", codec);
    const defaults = await store.load();
    const remote = (id: string) => ({
      id,
      kind: "remote" as const,
      name: id,
      baseUrl: `https://${id}.example.test`,
      apiMajorVersion: 1 as const,
      allowInsecureHttp: false,
    });
    await store.save({
      profiles: [...defaults.profiles, remote("remote-1"), remote("remote-2")],
      activeServerId: "remote-1",
      projectTabsByServer: { "remote-1": ["project-a"], "remote-2": ["project-b"] },
    });
    await Promise.all([
      store.save({ activeServerId: "remote-2" }),
      store.save({ profiles: [...defaults.profiles, remote("remote-2")] }),
    ]);
    assert.equal((await store.load()).activeServerId, "remote-2");
    assert.deepEqual((await store.load()).projectTabsByServer, { "remote-2": ["project-b"] });
    await store.save({ profiles: defaults.profiles });
    assert.equal((await store.load()).activeServerId, "local");
    assert.deepEqual((await store.load()).projectTabsByServer, {});
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("OIDC credentials keep explicit-login mode after logout and are invalidated on URL changes", async () => {
  const directory = await mkdtemp(join(tmpdir(), "agw-desktop-oidc-"));
  try {
    const store = new DesktopSettingsStore(directory, "client", codec);
    const settings = await store.load();
    await store.saveToken("local", "agw_oidc-token", "oidc");
    assert.equal(await store.credentialSource("local"), "oidc");
    await store.deleteToken("local");
    assert.equal(await store.credentialSource("local"), "oidc");
    await store.saveToken("local", "agw_oidc-token", "oidc");
    await store.save({
      profiles: settings.profiles.map((profile) => ({
        ...profile,
        baseUrl: "http://127.0.0.1:40000",
      })),
    });
    assert.equal(await store.loadToken("local"), null);
    assert.equal(await store.credentialSource("local"), "oidc");
    await store.saveToken("local", "agw_manual-token");
    assert.equal(await store.credentialSource("local"), "manual");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("cancelled OIDC save leaves the previous token and credential source intact", async () => {
  const directory = await mkdtemp(join(tmpdir(), "agw-desktop-oidc-cancel-"));
  try {
    const store = new DesktopSettingsStore(directory, "client", codec);
    await store.saveToken("local", "agw_existing-token");
    await assert.rejects(
      store.saveToken("local", "agw_new-token", "oidc", async () => {
        throw new Error("cancelled");
      }),
      /cancelled/u,
    );
    assert.equal(await store.loadToken("local"), "agw_existing-token");
    assert.equal(await store.credentialSource("local"), "manual");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("settings written before extensions existed read extensions as empty", async () => {
  const scratch = await createScratchDirectory("settings-extensions");
  try {
    await writeFile(
      join(scratch.path, "settings.json"),
      JSON.stringify({ schemaVersion: 1, closeBehavior: "quit-desktop" }),
    );
    const settings = await new DesktopSettingsStore(scratch.path, "client", codec).load();
    assert.deepEqual(settings.extensions, {});
    assert.equal(settings.schemaVersion, 1);
  } finally {
    await scratch.dispose();
  }
});

test("extension settings with wrong field types are rejected", async () => {
  const scratch = await createScratchDirectory("settings-extensions");
  try {
    for (const entry of [
      {
        enabled: "yes",
        grantedPermissions: [],
        grantedHostPermissions: [],
        lastActivatedVersion: null,
      },
      {
        enabled: true,
        grantedPermissions: ["fs.everything"],
        grantedHostPermissions: [],
        lastActivatedVersion: null,
      },
      {
        enabled: true,
        grantedPermissions: [],
        grantedHostPermissions: ["https://a.test/x"],
        lastActivatedVersion: null,
      },
      {
        enabled: true,
        grantedPermissions: [],
        grantedHostPermissions: [],
        lastActivatedVersion: 1,
      },
      { enabled: true, grantedPermissions: [], grantedHostPermissions: [] },
    ]) {
      await writeFile(
        join(scratch.path, "settings.json"),
        JSON.stringify({ schemaVersion: 1, extensions: { sample: entry } }),
      );
      await assert.rejects(new DesktopSettingsStore(scratch.path, "client", codec).load());
    }
  } finally {
    await scratch.dispose();
  }
});

test("saving other settings keeps the extensions field", async () => {
  const scratch = await createScratchDirectory("settings-extensions");
  try {
    const store = new DesktopSettingsStore(scratch.path, "client", codec);
    await store.setExtensionEnabled("sample", true, {
      permissions: ["context.project"],
      hostPermissions: ["https://api.example.com"],
    });
    await store.save({ projectTabsByServer: { local: ["project-a"] } });
    await store.save({ closeBehavior: "quit-desktop" });
    const saved = await store.load();
    assert.deepEqual(saved.extensions, {
      sample: {
        enabled: true,
        grantedPermissions: ["context.project"],
        grantedHostPermissions: ["https://api.example.com"],
        lastActivatedVersion: null,
      },
    });
    assert.deepEqual(saved.projectTabsByServer, { local: ["project-a"] });
  } finally {
    await scratch.dispose();
  }
});

test("consecutive enable and activation records are both saved", async () => {
  const scratch = await createScratchDirectory("settings-extensions");
  try {
    const store = new DesktopSettingsStore(scratch.path, "client", codec);
    await Promise.all([
      store.setExtensionEnabled("first", true, { permissions: [], hostPermissions: [] }),
      store.setExtensionEnabled("second", true, {
        permissions: ["execution"],
        hostPermissions: [],
      }),
      store.recordExtensionActivation("first", "1.0.0"),
    ]);
    assert.equal((await store.load()).extensions.first.lastActivatedVersion, "1.0.0");
    await Promise.all([
      store.recordExtensionActivation("first", "1.1.0"),
      store.recordExtensionActivation("second", "2.0.0"),
      store.save({ closeBehavior: "quit-desktop" }),
    ]);
    const saved = await store.load();
    assert.equal(saved.extensions.first.lastActivatedVersion, "1.1.0");
    assert.equal(saved.extensions.second.lastActivatedVersion, "2.0.0");
    assert.deepEqual(saved.extensions.second.grantedPermissions, ["execution"]);
    assert.equal(saved.closeBehavior, "quit-desktop");

    await store.setExtensionEnabled("first", false);
    const disabled = (await store.load()).extensions.first;
    assert.equal(disabled.enabled, false);
    assert.equal(disabled.lastActivatedVersion, "1.1.0");
  } finally {
    await scratch.dispose();
  }
});

test("declaration changes narrow approved grants or disable until approved again", async () => {
  const scratch = await createScratchDirectory("settings-extensions");
  try {
    const store = new DesktopSettingsStore(scratch.path, "client", codec);
    await store.setExtensionEnabled("narrow", true, {
      permissions: ["execution", "a2a"],
      hostPermissions: ["https://a.example.com"],
    });
    await store.setExtensionEnabled("grow", true, { permissions: ["a2a"], hostPermissions: [] });
    await store.reconcileExtensionGrants(
      new Map([
        ["narrow", { permissions: ["a2a" as const], hostPermissions: [] }],
        ["grow", { permissions: ["a2a" as const, "execution" as const], hostPermissions: [] }],
      ]),
    );
    const saved = (await store.load()).extensions;
    assert.deepEqual(saved.narrow, {
      enabled: true,
      grantedPermissions: ["a2a"],
      grantedHostPermissions: [],
      lastActivatedVersion: null,
    });
    assert.equal(saved.grow.enabled, false);
    assert.deepEqual(saved.grow.grantedPermissions, ["a2a"]);
  } finally {
    await scratch.dispose();
  }
});
