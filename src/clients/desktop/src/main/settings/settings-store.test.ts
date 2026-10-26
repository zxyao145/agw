import assert from "node:assert/strict";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";

import { writeFile } from "node:fs/promises";
import { DEFAULT_APPEARANCE } from "../../shared/appearance";
import type { AppearanceSettings } from "../../shared/contracts";
import { createScratchDirectory } from "../scratch-directory.test-helper";
import { DesktopSettingsStore, type SecretCodec } from "./settings-store";

const codec: SecretCodec = {
  encrypt: (value) => Buffer.from(`encrypted:${value}`, "utf8"),
  decrypt: (value) => value.toString("utf8").replace(/^encrypted:/u, ""),
};

const THEME_EXTENSION_ID = "01a173b5-526c-74d8-864c-26b0968892f6";
const SOFT_THEME = `${THEME_EXTENSION_ID}:soft`;

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

test("file viewer defaults are set, replaced and removed per file type and kept by other saves", async () => {
  const scratch = await createScratchDirectory("settings-viewers");
  try {
    await writeFile(
      join(scratch.path, "settings.json"),
      JSON.stringify({ schemaVersion: 1, closeBehavior: "quit-desktop" }),
    );
    const store = new DesktopSettingsStore(scratch.path, "client", codec);
    assert.deepEqual((await store.load()).fileViewerDefaults, {});

    assert.deepEqual(await store.setFileViewerDefault("*.md", "sample:code"), {
      "*.md": "sample:code",
    });
    assert.deepEqual(await store.setFileViewerDefault("dockerfile", "source"), {
      "*.md": "sample:code",
      dockerfile: "source",
    });
    assert.deepEqual(await store.setFileViewerDefault("*.md", "sample:preview"), {
      "*.md": "sample:preview",
      dockerfile: "source",
    });
    await store.save({ closeBehavior: "minimize-to-tray" });
    await store.setExtensionEnabled("sample", true, { permissions: [], hostPermissions: [] });
    assert.deepEqual((await store.load()).fileViewerDefaults, {
      "*.md": "sample:preview",
      dockerfile: "source",
    });

    assert.deepEqual(await store.setFileViewerDefault("*.md", null), { dockerfile: "source" });
    assert.deepEqual(await store.setFileViewerDefault("*.txt", null), { dockerfile: "source" });
    const reloaded = await new DesktopSettingsStore(scratch.path, "client", codec).load();
    assert.deepEqual(reloaded.fileViewerDefaults, { dockerfile: "source" });
    assert.deepEqual(reloaded.extensions.sample?.enabled, true);
  } finally {
    await scratch.dispose();
  }
});

test("file viewer defaults with invalid keys or values are rejected", async () => {
  const scratch = await createScratchDirectory("settings-viewers");
  try {
    for (const fileViewerDefaults of [
      { "*.md": 1 },
      { "*.md": "" },
      { "": "source" },
      ["source"],
    ]) {
      await writeFile(
        join(scratch.path, "settings.json"),
        JSON.stringify({ schemaVersion: 1, fileViewerDefaults }),
      );
      await assert.rejects(new DesktopSettingsStore(scratch.path, "client", codec).load());
    }
    await writeFile(join(scratch.path, "settings.json"), JSON.stringify({ schemaVersion: 1 }));
    const store = new DesktopSettingsStore(scratch.path, "client", codec);
    await assert.rejects(store.setFileViewerDefault("", "sample:code"));
    await assert.rejects(store.setFileViewerDefault("*.md", ""));
    assert.deepEqual((await store.load()).fileViewerDefaults, {});
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

test("bundled extensions are enabled by default with their declaration approved, including later additions", async () => {
  const scratch = await createScratchDirectory("settings-extensions");
  try {
    const store = new DesktopSettingsStore(scratch.path, "client", codec);
    await store.applyBundledExtensions(
      new Map([["viewer", { permissions: ["fs.cloud.read" as const], hostPermissions: [] }]]),
    );
    assert.deepEqual((await store.load()).extensions.viewer, {
      enabled: true,
      grantedPermissions: ["fs.cloud.read"],
      grantedHostPermissions: [],
      lastActivatedVersion: null,
    });

    // 用户停用后保持停用；升级新增的权限自动批准。Disabled stays disabled; permissions added by an upgrade are approved.
    await store.recordExtensionActivation("viewer", "1.0.0");
    await store.setExtensionEnabled("viewer", false);
    await store.applyBundledExtensions(
      new Map([
        [
          "viewer",
          {
            permissions: ["fs.cloud.read" as const, "fs.cloud.write" as const],
            hostPermissions: [],
          },
        ],
      ]),
    );
    assert.deepEqual((await store.load()).extensions.viewer, {
      enabled: false,
      grantedPermissions: ["fs.cloud.read", "fs.cloud.write"],
      grantedHostPermissions: [],
      lastActivatedVersion: "1.0.0",
    });
  } finally {
    await scratch.dispose();
  }
});

test("uninstalling a bundled extension records it and keeps it from coming back", async () => {
  const scratch = await createScratchDirectory("settings-extensions");
  try {
    const store = new DesktopSettingsStore(scratch.path, "client", codec);
    const declared = new Map([["viewer", { permissions: [], hostPermissions: [] }]]);
    await store.applyBundledExtensions(declared);
    await store.removeBundledExtension("viewer");
    await store.removeBundledExtension("viewer");
    await store.applyBundledExtensions(declared);
    await store.save({ closeBehavior: "quit-desktop" });
    const saved = await store.load();
    assert.deepEqual(saved.extensions, {});
    assert.deepEqual(saved.removedBundledExtensions, ["viewer"]);
  } finally {
    await scratch.dispose();
  }
});

async function loadWithStoredAppearance(appearance: unknown): Promise<AppearanceSettings> {
  const scratch = await createScratchDirectory("settings-appearance");
  try {
    if (appearance !== undefined) {
      await writeFile(join(scratch.path, "settings.json"), JSON.stringify({ appearance }));
    }
    return (await new DesktopSettingsStore(scratch.path, "client", codec).load()).appearance;
  } finally {
    await scratch.dispose();
  }
}

async function rejectsStoredAppearance(appearance: unknown): Promise<void> {
  const scratch = await createScratchDirectory("settings-appearance");
  try {
    await writeFile(join(scratch.path, "settings.json"), JSON.stringify({ appearance }));
    await assert.rejects(new DesktopSettingsStore(scratch.path, "client", codec).load());
  } finally {
    await scratch.dispose();
  }
}

test("load returns the default appearance when the file has none", async () => {
  assert.deepEqual(await loadWithStoredAppearance(undefined), DEFAULT_APPEARANCE);
});

test("load accepts the appearance range boundaries", async () => {
  const small = { baseFontSize: 12, chatFontSize: 12, codeFontSize: 12, theme: "default" };
  const large = { baseFontSize: 20, chatFontSize: 24, codeFontSize: 28, theme: "default" };
  assert.deepEqual(await loadWithStoredAppearance(small), small);
  assert.deepEqual(await loadWithStoredAppearance(large), large);
});

test("load reads a missing theme as default, a missing code size as null and keeps a missing mode missing", async () => {
  const appearance = await loadWithStoredAppearance({ baseFontSize: 18, chatFontSize: null });
  assert.deepEqual(appearance, {
    baseFontSize: 18,
    chatFontSize: null,
    codeFontSize: null,
    theme: "default",
  });
  assert.equal("mode" in appearance, false);
});

test("load accepts extension theme ids and the three modes", async () => {
  for (const mode of ["system", "light", "dark"] as const) {
    const stored = {
      baseFontSize: 16,
      chatFontSize: null,
      codeFontSize: null,
      theme: SOFT_THEME,
      mode,
    };
    assert.deepEqual(await loadWithStoredAppearance(stored), stored);
  }
});

test("load rejects an appearance outside the allowed values", async () => {
  const valid = { baseFontSize: 16, chatFontSize: null };
  for (const invalid of [
    { ...valid, baseFontSize: 21 },
    { ...valid, baseFontSize: 11 },
    { ...valid, chatFontSize: 25 },
    { ...valid, chatFontSize: 11 },
    { ...valid, codeFontSize: 29 },
    { ...valid, codeFontSize: 11 },
    { ...valid, codeFontSize: 14.5 },
    { ...valid, documentFontSize: null },
    { ...valid, documentFontSize: 18 },
    { ...valid, baseFontSize: 16.5 },
    { ...valid, theme: "soft" },
    { ...valid, theme: "soft-theme:soft" },
    { ...valid, theme: `${THEME_EXTENSION_ID.toUpperCase()}:soft` },
    { ...valid, theme: `${THEME_EXTENSION_ID}:Soft` },
    { ...valid, theme: `${SOFT_THEME}:x` },
    { ...valid, theme: `${THEME_EXTENSION_ID}:` },
    { ...valid, theme: THEME_EXTENSION_ID },
    { ...valid, theme: "" },
    { ...valid, theme: null },
    { ...valid, mode: "auto" },
    { ...valid, mode: null },
  ]) {
    await rejectsStoredAppearance(invalid);
  }
});

test("save stores the appearance and keeps the other settings", async () => {
  const scratch = await createScratchDirectory("settings-appearance");
  try {
    const store = new DesktopSettingsStore(scratch.path, "client", codec);
    await store.save({ closeBehavior: "quit-desktop" });
    const before = await store.load();
    const appearance = {
      baseFontSize: 18,
      chatFontSize: 15,
      codeFontSize: 20,
      theme: SOFT_THEME,
      mode: "dark",
    } as const;
    await store.save({ appearance });
    const saved = await store.load();
    assert.deepEqual(saved.appearance, appearance);
    assert.equal(saved.closeBehavior, "quit-desktop");
    assert.deepEqual(saved.profiles, before.profiles);
  } finally {
    await scratch.dispose();
  }
});

test("save without an appearance keeps the stored appearance", async () => {
  const scratch = await createScratchDirectory("settings-appearance");
  try {
    const store = new DesktopSettingsStore(scratch.path, "client", codec);
    const appearance = {
      baseFontSize: 18,
      chatFontSize: 15,
      codeFontSize: null,
      theme: SOFT_THEME,
      mode: "light",
    } as const;
    await store.save({ appearance });
    await store.save({ closeBehavior: "quit-desktop" });
    assert.deepEqual((await store.load()).appearance, appearance);
  } finally {
    await scratch.dispose();
  }
});

test("writes that do not change the appearance keep a missing mode missing", async () => {
  const scratch = await createScratchDirectory("settings-appearance");
  try {
    await writeFile(
      join(scratch.path, "settings.json"),
      JSON.stringify({ appearance: { baseFontSize: 17, chatFontSize: null } }),
    );
    const store = new DesktopSettingsStore(scratch.path, "client", codec);
    await store.applyBundledExtensions(
      new Map([["viewer", { permissions: [], hostPermissions: [] }]]),
    );
    await store.setFileViewerDefault("*.md", "viewer:preview");
    await store.save({ closeBehavior: "quit-desktop" });

    const file = JSON.parse(await readFile(join(scratch.path, "settings.json"), "utf8"));
    // 缺失的 Code 字号读作 null。A missing code size reads as null.
    assert.deepEqual(file.appearance, {
      baseFontSize: 17,
      chatFontSize: null,
      codeFontSize: null,
      theme: "default",
    });
    assert.equal(file.extensions.viewer.enabled, true);
    assert.deepEqual(file.fileViewerDefaults, { "*.md": "viewer:preview" });
  } finally {
    await scratch.dispose();
  }
});

test("save rejects sizes below 12 and invalid themes or modes, keeping the stored appearance", async () => {
  const scratch = await createScratchDirectory("settings-appearance");
  try {
    const store = new DesktopSettingsStore(scratch.path, "client", codec);
    const stored = {
      baseFontSize: 18,
      chatFontSize: null,
      codeFontSize: null,
      theme: "default",
      mode: "system",
    } as const;
    await store.save({ appearance: stored });
    await assert.rejects(store.save({ appearance: { ...stored, baseFontSize: 11 } }));
    await assert.rejects(store.save({ appearance: { ...stored, chatFontSize: 11 } }));
    await assert.rejects(store.save({ appearance: { ...stored, codeFontSize: 11 } }));
    await assert.rejects(store.save({ appearance: { ...stored, codeFontSize: 29 } }));
    await assert.rejects(store.save({ appearance: { ...stored, theme: "soft" } }));
    await assert.rejects(
      store.save({ appearance: { ...stored, mode: "auto" as unknown as "system" } }),
    );
    assert.deepEqual((await store.load()).appearance, stored);
  } finally {
    await scratch.dispose();
  }
});
