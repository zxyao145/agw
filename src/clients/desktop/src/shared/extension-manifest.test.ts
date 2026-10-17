import assert from "node:assert/strict";
import test from "node:test";
import { Ajv2020 } from "ajv/dist/2020.js";
import {
  evaluateExtensionManifest,
  extensionManifestJsonSchema,
  manifestPackagePaths,
} from "./extension-manifest";
import { matchesConnectTarget, parseHostPermission } from "./extension-host-permissions";

const APP_VERSION = "0.5.0";

function sampleManifest(): Record<string, unknown> {
  return {
    $schema: "./agw/manifest.schema.json",
    manifestVersion: 1,
    id: "hello-world",
    name: "Hello World",
    version: "1.2.0",
    minAppVersion: "0.5.0",
    description: "Adds a greeting app.",
    author: "Agw",
    authorUrl: "https://example.com",
    icon: "icon.svg",
    background: "background.js",
    contributes: {
      commands: [{ id: "insert-greeting", title: "Insert greeting" }],
      ribbon: [
        {
          id: "open",
          title: "Hello",
          icon: "icon.svg",
          action: { type: "openMainView", view: "app" },
        },
        {
          id: "list",
          title: "Greetings",
          icon: "Hand",
          action: { type: "toggleSidebarView", view: "list" },
        },
      ],
      sidebarViews: [{ id: "list", title: "Greetings", icon: "Hand", entry: "sidebar.html" }],
      mainViews: [
        {
          id: "app",
          title: "Hello",
          icon: "icon.svg",
          entry: "index.html",
          retainContextWhenHidden: true,
        },
      ],
      settings: { entry: "settings.html" },
      chat: {
        composerActions: [
          {
            id: "greet",
            title: "Insert greeting",
            icon: "Hand",
            action: { type: "command", command: "insert-greeting" },
          },
        ],
        messageActions: [
          {
            id: "open-in-app",
            title: "Open in Hello",
            icon: "icon.svg",
            action: { type: "openMainView", view: "app" },
          },
        ],
        conversationActions: [],
      },
      explorerActions: [],
    },
    permissions: ["context.project", "fs.cloud.read", "chat.composer"],
    hostPermissions: ["https://api.example.com"],
  };
}

function withChange(change: (manifest: Record<string, unknown>) => void): Record<string, unknown> {
  const manifest = sampleManifest();
  change(manifest);
  return manifest;
}

function contributes(manifest: Record<string, unknown>): Record<string, unknown> {
  return manifest.contributes as Record<string, unknown>;
}

function errorCodes(raw: unknown): string[] {
  return evaluateExtensionManifest(raw, APP_VERSION).errors.map((error) => error.code);
}

test("a complete manifest passes validation", () => {
  const result = evaluateExtensionManifest(sampleManifest(), APP_VERSION);
  assert.deepEqual(result.errors, []);
  assert.equal(result.validation, "valid");
  assert.equal(result.manifest?.id, "hello-world");
});

test("extension ids must match the format and must not start with agw", () => {
  for (const id of ["Hello", "hello_world", "-hello", "hello-", "ab", "agw-tools", "agwx"]) {
    const result = evaluateExtensionManifest(
      withChange((manifest) => (manifest.id = id)),
      APP_VERSION,
    );
    assert.equal(result.validation, "invalid", id);
    assert.equal(result.errors[0]?.path, "id", id);
  }
});

test("unknown top-level and contributes fields are rejected with their paths", () => {
  const result = evaluateExtensionManifest(
    withChange((manifest) => {
      manifest.permision = [];
      contributes(manifest).views = [];
    }),
    APP_VERSION,
  );
  assert.equal(result.validation, "invalid");
  assert.deepEqual(result.errors.map((error) => [error.code, error.path]).sort(), [
    ["UNKNOWN_FIELD", "contributes.views"],
    ["UNKNOWN_FIELD", "permision"],
  ]);
});

test("package paths cannot leave the root or use the reserved __agw prefix", () => {
  for (const background of [
    "../background.js",
    "/background.js",
    "lib/../background.js",
    "./background.js",
    "__agw/background.js",
    "lib\\background.js",
    "lib//background.js",
  ]) {
    const result = evaluateExtensionManifest(
      withChange((manifest) => (manifest.background = background)),
      APP_VERSION,
    );
    assert.equal(result.validation, "invalid", background);
    assert.equal(result.errors[0]?.path, "background", background);
  }
  const entry = evaluateExtensionManifest(
    withChange((manifest) => {
      (contributes(manifest).mainViews as Record<string, unknown>[])[0].entry = "../index.html";
    }),
    APP_VERSION,
  );
  assert.equal(entry.errors[0]?.path, "contributes.mainViews[0].entry");
});

test("entry, background and icon files need the allowed extensions", () => {
  assert.deepEqual(errorCodes(withChange((manifest) => (manifest.background = "background.ts"))), [
    "INVALID_FIELD",
  ]);
  assert.deepEqual(errorCodes(withChange((manifest) => (manifest.icon = "icon.jpg"))), [
    "INVALID_FIELD",
  ]);
  assert.deepEqual(
    errorCodes(withChange((manifest) => (manifest.background = "background.mjs"))),
    [],
  );
});

test("actions must reference declared views and commands", () => {
  const result = evaluateExtensionManifest(
    withChange((manifest) => {
      const ribbon = contributes(manifest).ribbon as Record<string, unknown>[];
      ribbon[0].action = { type: "openMainView", view: "missing" };
      ribbon[1].action = { type: "toggleSidebarView", view: "app" };
      const chat = contributes(manifest).chat as Record<string, Record<string, unknown>[]>;
      chat.composerActions[0].action = { type: "command", command: "missing" };
    }),
    APP_VERSION,
  );
  assert.deepEqual(
    result.errors.map((error) => [error.code, error.path]),
    [
      ["UNKNOWN_VIEW", "contributes.ribbon[0].action.view"],
      ["UNKNOWN_VIEW", "contributes.ribbon[1].action.view"],
      ["UNKNOWN_COMMAND", "contributes.chat.composerActions[0].action.command"],
    ],
  );
});

test("command actions require a background script", () => {
  const result = evaluateExtensionManifest(
    withChange((manifest) => delete manifest.background),
    APP_VERSION,
  );
  assert.deepEqual(
    result.errors.map((error) => [error.code, error.path]),
    [["BACKGROUND_REQUIRED", "contributes.chat.composerActions[0].action"]],
  );
});

test("duplicate ids and permissions are rejected", () => {
  const result = evaluateExtensionManifest(
    withChange((manifest) => {
      const commands = contributes(manifest).commands as Record<string, unknown>[];
      commands.push({ id: "insert-greeting", title: "Again" });
      manifest.permissions = ["execution", "execution"];
      manifest.hostPermissions = ["https://a.example.com", "https://a.example.com"];
    }),
    APP_VERSION,
  );
  assert.deepEqual(
    result.errors.map((error) => [error.code, error.path]),
    [
      ["DUPLICATE_ID", "contributes.commands[1].id"],
      ["DUPLICATE_VALUE", "permissions[1]"],
      ["DUPLICATE_VALUE", "hostPermissions[1]"],
    ],
  );
});

test("host permissions accept origins and reject paths, bare wildcards and IP wildcards", () => {
  for (const value of [
    "https://api.example.com",
    "http://localhost:3000",
    "wss://*.example.com",
    "ws://127.0.0.1:*",
    "https://intranet",
    "https://api.example.com:8443",
  ]) {
    assert.deepEqual(
      errorCodes(withChange((manifest) => (manifest.hostPermissions = [value]))),
      [],
      value,
    );
  }
  for (const value of [
    "https://api.example.com/path",
    "https://api.example.com/",
    "*",
    "https://*",
    "https://*.com",
    "https://*.1.2.3",
    "https://*.127.0.0.1",
    "ftp://example.com",
    "https://example.com:0",
    "https://example.com:65536",
    "https://Example.com",
    "https://[::1]",
  ]) {
    const result = evaluateExtensionManifest(
      withChange((manifest) => (manifest.hostPermissions = [value])),
      APP_VERSION,
    );
    assert.equal(result.validation, "invalid", value);
    assert.equal(result.errors[0]?.path, "hostPermissions[0]", value);
  }
});

test("host permissions match connect targets by host and port", () => {
  const permissions = ["https://api.example.com", "http://*.example.net:8080"].map((value) =>
    parseHostPermission(value)!,
  );
  assert.equal(matchesConnectTarget(permissions, "api.example.com", 443), true);
  assert.equal(matchesConnectTarget(permissions, "api.example.com", 80), false);
  assert.equal(matchesConnectTarget(permissions, "a.b.example.net", 8080), true);
  assert.equal(matchesConnectTarget(permissions, "example.net", 8080), false);
});

test("manifest versions outside the supported range are incompatible", () => {
  const old = evaluateExtensionManifest(
    withChange((manifest) => (manifest.manifestVersion = 0)),
    APP_VERSION,
  );
  assert.equal(old.validation, "incompatible");
  assert.equal(old.errors[0].code, "MANIFEST_VERSION_UNSUPPORTED");
  assert.equal(
    old.errors[0].message,
    "This extension targets Extension API version 0. This Desktop supports version 1 and later.",
  );

  const newer = evaluateExtensionManifest(
    withChange((manifest) => {
      manifest.manifestVersion = 2;
      manifest.futureField = true;
    }),
    APP_VERSION,
  );
  assert.equal(newer.validation, "incompatible");
  assert.deepEqual(
    newer.errors.map((error) => error.code),
    ["MANIFEST_VERSION_TOO_NEW"],
  );
  assert.equal(newer.manifest, null);
});

test("minAppVersion above the running Desktop is incompatible, including preview builds", () => {
  const newer = evaluateExtensionManifest(
    withChange((manifest) => (manifest.minAppVersion = "0.6.0")),
    APP_VERSION,
  );
  assert.equal(newer.validation, "incompatible");
  assert.equal(newer.errors[0].code, "APP_VERSION_TOO_OLD");
  assert.equal(newer.manifest?.id, "hello-world");

  const preview = evaluateExtensionManifest(
    withChange((manifest) => (manifest.minAppVersion = "1.2.0")),
    "1.2.0-preview.1",
  );
  assert.equal(preview.validation, "incompatible");

  const release = evaluateExtensionManifest(
    withChange((manifest) => (manifest.minAppVersion = "1.2.0-preview.1")),
    "1.2.0",
  );
  assert.equal(release.validation, "valid");
  assert.deepEqual(errorCodes(withChange((manifest) => (manifest.minAppVersion = "1.2"))), [
    "INVALID_FIELD",
  ]);
});

test("version must be a semantic version", () => {
  assert.deepEqual(errorCodes(withChange((manifest) => (manifest.version = "1.2"))), [
    "INVALID_FIELD",
  ]);
  assert.deepEqual(errorCodes(withChange((manifest) => (manifest.version = "1.2.0-beta.1"))), []);
});

test("package paths referenced by the manifest are listed with their fields", () => {
  const manifest = evaluateExtensionManifest(sampleManifest(), APP_VERSION).manifest!;
  assert.deepEqual(manifestPackagePaths(manifest), [
    { path: "icon.svg", field: "icon", kind: "icon" },
    { path: "background.js", field: "background", kind: "background" },
    { path: "icon.svg", field: "contributes.ribbon[0].icon", kind: "icon" },
    { path: "sidebar.html", field: "contributes.sidebarViews[0].entry", kind: "entry" },
    { path: "icon.svg", field: "contributes.mainViews[0].icon", kind: "icon" },
    { path: "index.html", field: "contributes.mainViews[0].entry", kind: "entry" },
    { path: "settings.html", field: "contributes.settings.entry", kind: "entry" },
    { path: "icon.svg", field: "contributes.chat.messageActions[0].icon", kind: "icon" },
  ]);
});

test("the generated JSON Schema validates the same samples", () => {
  const ajv = new Ajv2020({ allErrors: true, strict: false, validateFormats: false });
  const validate = ajv.compile(extensionManifestJsonSchema());
  const samples: [string, Record<string, unknown>, boolean][] = [
    ["complete", sampleManifest(), true],
    ["unknown field", withChange((manifest) => (manifest.permision = [])), false],
    [
      "unknown contributes field",
      withChange((manifest) => (contributes(manifest).views = [])),
      false,
    ],
    ["id format", withChange((manifest) => (manifest.id = "Hello")), false],
    ["agw prefix", withChange((manifest) => (manifest.id = "agw-tools")), false],
    ["path escape", withChange((manifest) => (manifest.background = "../background.js")), false],
    ["absolute path", withChange((manifest) => (manifest.background = "/background.js")), false],
    [
      "reserved path",
      withChange((manifest) => (manifest.background = "__agw/background.js")),
      false,
    ],
    [
      "host path",
      withChange((manifest) => (manifest.hostPermissions = ["https://a.example.com/x"])),
      false,
    ],
    ["bare wildcard", withChange((manifest) => (manifest.hostPermissions = ["*"])), false],
    [
      "IP wildcard",
      withChange((manifest) => (manifest.hostPermissions = ["https://*.1.2.3"])),
      false,
    ],
    [
      "unknown permission",
      withChange((manifest) => (manifest.permissions = ["fs.everything"])),
      false,
    ],
    [
      "action type",
      withChange((manifest) => {
        (contributes(manifest).ribbon as Record<string, unknown>[])[0].action = { type: "open" };
      }),
      false,
    ],
  ];
  for (const [name, sample, valid] of samples) {
    assert.equal(validate(sample), valid, name);
    assert.equal(
      evaluateExtensionManifest(sample, APP_VERSION).validation === "valid",
      valid,
      name,
    );
  }
});
