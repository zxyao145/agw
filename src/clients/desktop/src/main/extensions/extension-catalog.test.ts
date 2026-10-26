import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdir, readFile, rm, symlink, writeFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";

import { createScratchDirectory } from "../scratch-directory.test-helper";
import {
  MAX_ICON_BYTES,
  mergeExtensionCatalogs,
  scanExtensionDirectory,
  toInstalledExtension,
} from "./extension-catalog";
import { testManifest, writeTestExtension } from "./extension-fixture.test-helper";

const APP_VERSION = "0.5.0";
const EXTENSION_ID = "01a173c6-4933-766f-8182-77f77ba5b946";

test("scanning lists valid extensions with icon data URLs matching the files", async () => {
  const scratch = await createScratchDirectory("catalog");
  try {
    await writeTestExtension(join(scratch.path, EXTENSION_ID), testManifest(EXTENSION_ID));
    await mkdir(join(scratch.path, ".staging", "abc"), { recursive: true });
    await writeFile(join(scratch.path, "notes.txt"), "not an extension");

    const entries = await scanExtensionDirectory(scratch.path, APP_VERSION, "user");

    assert.deepEqual(
      entries.map((entry) => [entry.id, entry.validation]),
      [[EXTENSION_ID, "valid"]],
    );
    const dataUrl = entries[0].icons["icon.svg"];
    assert.match(dataUrl, /^data:image\/svg\+xml;base64,/u);
    assert.deepEqual(
      Buffer.from(dataUrl.split(",")[1], "base64"),
      await readFile(join(scratch.path, EXTENSION_ID, "icon.svg")),
    );
  } finally {
    await scratch.dispose();
  }
});

test("a directory name that differs from the manifest id is an error", async () => {
  const scratch = await createScratchDirectory("catalog");
  try {
    await writeTestExtension(join(scratch.path, "renamed"), testManifest(EXTENSION_ID));
    const [entry] = await scanExtensionDirectory(scratch.path, APP_VERSION, "user");
    assert.equal(entry.id, "renamed");
    assert.equal(entry.validation, "invalid");
    assert.deepEqual(
      entry.errors.map((error) => error.code),
      ["DIRECTORY_NAME_MISMATCH"],
    );
  } finally {
    await scratch.dispose();
  }
});

test("a manifest that is not valid JSON has a null manifest", async () => {
  const scratch = await createScratchDirectory("catalog");
  try {
    await writeTestExtension(join(scratch.path, "broken"), "{ not json");
    const [entry] = await scanExtensionDirectory(scratch.path, APP_VERSION, "user");
    assert.equal(entry.manifest, null);
    assert.equal(entry.validation, "invalid");
    assert.equal(entry.errors[0].code, "MANIFEST_PARSE_FAILED");
  } finally {
    await scratch.dispose();
  }
});

test("missing package files and missing manifests are errors", async () => {
  const scratch = await createScratchDirectory("catalog");
  try {
    await writeTestExtension(
      join(scratch.path, EXTENSION_ID),
      testManifest(EXTENSION_ID, {
        contributes: {
          mainViews: [{ id: "app", title: "App", icon: "Box", entry: "missing.html" }],
        },
      }),
    );
    await mkdir(join(scratch.path, "empty"));
    const entries = await scanExtensionDirectory(scratch.path, APP_VERSION, "user");
    assert.deepEqual(
      entries.map((entry) => [entry.id, entry.errors.map((error) => [error.code, error.path])]),
      [
        [EXTENSION_ID, [["PACKAGE_FILE_NOT_FOUND", "contributes.mainViews[0].entry"]]],
        ["empty", [["MANIFEST_NOT_FOUND", null]]],
      ],
    );
  } finally {
    await scratch.dispose();
  }
});

test("symbolic links that resolve outside the root are errors", async () => {
  const scratch = await createScratchDirectory("catalog");
  try {
    await writeFile(join(scratch.path, "outside.js"), "export {};");
    const directory = join(scratch.path, "extensions", EXTENSION_ID);
    await writeTestExtension(directory, testManifest(EXTENSION_ID));
    await rm(join(directory, "background.js"));
    await symlink(join(scratch.path, "outside.js"), join(directory, "background.js"));

    const [entry] = await scanExtensionDirectory(
      join(scratch.path, "extensions"),
      APP_VERSION,
      "user",
    );
    assert.equal(entry.validation, "invalid");
    assert.deepEqual(
      entry.errors.map((error) => [error.code, error.path]),
      [["PACKAGE_PATH_OUTSIDE_ROOT", "background"]],
    );
  } finally {
    await scratch.dispose();
  }
});

test("icons larger than 256 KB are errors", async () => {
  const scratch = await createScratchDirectory("catalog");
  try {
    await writeTestExtension(join(scratch.path, EXTENSION_ID), testManifest(EXTENSION_ID), {
      "icon.svg": `<svg xmlns="http://www.w3.org/2000/svg"><!--${"x".repeat(MAX_ICON_BYTES)}--></svg>`,
    });
    const [entry] = await scanExtensionDirectory(scratch.path, APP_VERSION, "user");
    assert.equal(entry.validation, "invalid");
    assert.deepEqual(
      entry.errors.map((error) => [error.code, error.path]),
      [
        ["ICON_TOO_LARGE", "icon"],
        ["ICON_TOO_LARGE", "contributes.ribbon[0].icon"],
      ],
    );
  } finally {
    await scratch.dispose();
  }
});

test("image icons become data URLs with the MIME type of their extension", async () => {
  const scratch = await createScratchDirectory("catalog");
  try {
    const images: [path: string, mimeType: string][] = [
      ["icon.png", "image/png"],
      ["photo.jpg", "image/jpeg"],
      ["photo.jpeg", "image/jpeg"],
      ["picture.webp", "image/webp"],
    ];
    await writeTestExtension(
      join(scratch.path, EXTENSION_ID),
      testManifest(EXTENSION_ID, {
        icon: "icon.png",
        contributes: {
          ribbon: images.slice(1).map(([icon], index) => ({
            id: `open-${index}`,
            title: "Open",
            icon,
            action: { type: "openMainView", view: "app" },
          })),
          mainViews: [{ id: "app", title: "App", icon: "Box", entry: "index.html" }],
        },
      }),
      Object.fromEntries(images.map(([path]) => [path, new TextEncoder().encode(path)])),
    );
    const [entry] = await scanExtensionDirectory(scratch.path, APP_VERSION, "user");
    assert.equal(entry.validation, "valid");
    assert.deepEqual(
      entry.icons,
      Object.fromEntries(
        images.map(([path, mimeType]) => [
          path,
          `data:${mimeType};base64,${Buffer.from(path).toString("base64")}`,
        ]),
      ),
    );
  } finally {
    await scratch.dispose();
  }
});

const THEME_MANIFEST = {
  manifestVersion: 1,
  id: EXTENSION_ID,
  name: "Themes",
  version: "1.0.0",
  contributes: {
    themes: [
      { id: "night", title: "Night", path: "themes/night.json" },
      { id: "day", title: "Day", path: "themes/day.json" },
    ],
  },
};

test("theme files are read after the package checks and merged into the entry", async () => {
  const scratch = await createScratchDirectory("catalog");
  try {
    await writeTestExtension(join(scratch.path, EXTENSION_ID), THEME_MANIFEST, {
      "themes/night.json": JSON.stringify({
        common: { "--radius": "0.75rem" },
        dark: { "--background": "#212121" },
      }),
      "themes/day.json": JSON.stringify({ light: { "--background": "#ffffff" } }),
    });
    const [entry] = await scanExtensionDirectory(scratch.path, APP_VERSION, "user");
    assert.deepEqual(entry.errors, []);
    assert.equal(entry.validation, "valid");
    assert.deepEqual(entry.themes, [
      {
        format: "json",
        id: "night",
        title: "Night",
        common: { "--radius": "0.75rem" },
        light: {},
        dark: { "--background": "#212121" },
      },
      {
        format: "json",
        id: "day",
        title: "Day",
        common: {},
        light: { "--background": "#ffffff" },
        dark: {},
      },
    ]);
    assert.deepEqual(toInstalledExtension(entry, undefined).themes, entry.themes);
  } finally {
    await scratch.dispose();
  }
});

const CSS_THEME_MANIFEST = {
  ...THEME_MANIFEST,
  contributes: { themes: [{ id: "paper", title: "Paper", path: "themes/paper.css" }] },
};

test("CSS themes carry only a descriptor of the processed style", async () => {
  const scratch = await createScratchDirectory("catalog");
  try {
    const css = ":root.dark { --background: #24211d; }";
    await writeTestExtension(join(scratch.path, EXTENSION_ID), CSS_THEME_MANIFEST, {
      "themes/paper.css": '@import "parts/base.css";',
      "themes/parts/base.css": css,
    });
    const [entry] = await scanExtensionDirectory(scratch.path, APP_VERSION, "user");
    assert.deepEqual(entry.errors, []);
    assert.deepEqual(entry.themes, [
      {
        format: "css",
        id: "paper",
        title: "Paper",
        styleId: createHash("sha256").update(css).digest("hex"),
        byteLength: Buffer.byteLength(css),
      },
    ]);
  } finally {
    await scratch.dispose();
  }
});

test("a CSS theme that fails its checks makes the extension invalid with the theme path", async () => {
  const scratch = await createScratchDirectory("catalog");
  try {
    await writeTestExtension(join(scratch.path, EXTENSION_ID), CSS_THEME_MANIFEST, {
      "themes/paper.css": ".hero { background: url(https://example.com/hero.png); }",
    });
    const [entry] = await scanExtensionDirectory(scratch.path, APP_VERSION, "user");
    assert.equal(entry.validation, "invalid");
    assert.deepEqual(entry.themes, []);
    assert.deepEqual(
      entry.errors.map((error) => [error.code, error.path]),
      [["THEME_RESOURCE_INVALID", "contributes.themes[0].path"]],
    );
    assert.match(entry.errors[0].message, /^themes\/paper\.css:1:9: /u);
  } finally {
    await scratch.dispose();
  }
});

test("invalid or missing theme files make the extension invalid without themes", async () => {
  const scratch = await createScratchDirectory("catalog");
  try {
    await writeTestExtension(join(scratch.path, EXTENSION_ID), THEME_MANIFEST, {
      "themes/night.json": JSON.stringify({ light: { "--background": "nope", "--bg": "#fff" } }),
    });
    const [missing] = await scanExtensionDirectory(scratch.path, APP_VERSION, "user");
    assert.equal(missing.validation, "invalid");
    assert.deepEqual(missing.themes, []);
    assert.deepEqual(
      missing.errors.map((error) => [error.code, error.path]),
      [
        ["PACKAGE_FILE_NOT_FOUND", "contributes.themes[1].path"],
        ["INVALID_THEME_VALUE", "contributes.themes[0].light.--background"],
        ["UNKNOWN_THEME_VARIABLE", "contributes.themes[0].light.--bg"],
      ],
    );

    await writeFile(join(scratch.path, EXTENSION_ID, "themes", "day.json"), "{ not json");
    const [broken] = await scanExtensionDirectory(scratch.path, APP_VERSION, "user");
    assert.deepEqual(
      broken.errors.map((error) => [error.code, error.path]),
      [
        ["INVALID_THEME_VALUE", "contributes.themes[0].light.--background"],
        ["UNKNOWN_THEME_VARIABLE", "contributes.themes[0].light.--bg"],
        ["THEME_PARSE_FAILED", "contributes.themes[1].path"],
      ],
    );
  } finally {
    await scratch.dispose();
  }
});

test("theme paths that resolve outside the root are errors", async () => {
  const scratch = await createScratchDirectory("catalog");
  try {
    await writeFile(join(scratch.path, "outside.json"), '{"light":{"--background":"#fff"}}');
    const directory = join(scratch.path, "extensions", EXTENSION_ID);
    await writeTestExtension(directory, {
      ...THEME_MANIFEST,
      contributes: { themes: [THEME_MANIFEST.contributes.themes[0]] },
    });
    await mkdir(join(directory, "themes"));
    await symlink(join(scratch.path, "outside.json"), join(directory, "themes", "night.json"));
    const [entry] = await scanExtensionDirectory(
      join(scratch.path, "extensions"),
      APP_VERSION,
      "user",
    );
    assert.deepEqual(
      entry.errors.map((error) => [error.code, error.path]),
      [["PACKAGE_PATH_OUTSIDE_ROOT", "contributes.themes[0].path"]],
    );
    assert.deepEqual(entry.themes, []);
  } finally {
    await scratch.dispose();
  }
});

test("incompatible versions keep the manifest and report the reason", async () => {
  const scratch = await createScratchDirectory("catalog");
  try {
    await writeTestExtension(
      join(scratch.path, EXTENSION_ID),
      testManifest(EXTENSION_ID, { minAppVersion: "9.0.0" }),
    );
    const [entry] = await scanExtensionDirectory(scratch.path, APP_VERSION, "user");
    assert.equal(entry.validation, "incompatible");
    assert.equal(entry.manifest?.id, EXTENSION_ID);
    assert.equal(entry.errors[0].code, "APP_VERSION_TOO_OLD");
  } finally {
    await scratch.dispose();
  }
});

test("installed extensions report when the declaration exceeds the approved grants", async () => {
  const scratch = await createScratchDirectory("catalog");
  try {
    await writeTestExtension(join(scratch.path, EXTENSION_ID), testManifest(EXTENSION_ID));
    const [entry] = await scanExtensionDirectory(scratch.path, APP_VERSION, "user");
    assert.equal(toInstalledExtension(entry, undefined).permissionApprovalRequired, false);
    assert.equal(
      toInstalledExtension(entry, {
        enabled: false,
        grantedPermissions: [],
        grantedHostPermissions: ["https://api.example.com"],
        lastActivatedVersion: "0.9.0",
      }).permissionApprovalRequired,
      true,
    );
    const approved = toInstalledExtension(entry, {
      enabled: true,
      grantedPermissions: ["context.project"],
      grantedHostPermissions: ["https://api.example.com"],
      lastActivatedVersion: null,
    });
    assert.equal(approved.permissionApprovalRequired, false);
    assert.equal(approved.enabled, true);
  } finally {
    await scratch.dispose();
  }
});

test("bundled and user directories merge with user copies winning and uninstalled bundled ones left out", async () => {
  const scratch = await createScratchDirectory("catalog");
  try {
    const bundledDirectory = join(scratch.path, "bundled");
    const userDirectory = join(scratch.path, "user");
    for (const id of ["shared", "kept", "removed"]) {
      await writeTestExtension(join(bundledDirectory, id), testManifest(id));
    }
    await writeTestExtension(join(userDirectory, "shared"), testManifest("shared"));
    await writeTestExtension(join(userDirectory, "added"), testManifest("added"));

    const merged = mergeExtensionCatalogs(
      await scanExtensionDirectory(bundledDirectory, APP_VERSION, "bundled"),
      await scanExtensionDirectory(userDirectory, APP_VERSION, "user"),
      ["removed"],
    );

    assert.deepEqual(
      merged.map((entry) => [entry.id, entry.source, entry.directory]),
      [
        ["added", "user", join(userDirectory, "added")],
        ["kept", "bundled", join(bundledDirectory, "kept")],
        ["shared", "user", join(userDirectory, "shared")],
      ],
    );
    assert.equal(toInstalledExtension(merged[1], undefined).source, "bundled");

    // 卸载用户副本后内置副本重新出现。Removing the user copy brings the bundled copy back.
    await rm(join(userDirectory, "shared"), { recursive: true });
    const afterUninstall = mergeExtensionCatalogs(
      await scanExtensionDirectory(bundledDirectory, APP_VERSION, "bundled"),
      await scanExtensionDirectory(userDirectory, APP_VERSION, "user"),
      ["removed"],
    );
    assert.equal(
      afterUninstall.find((entry) => entry.id === "shared")?.directory,
      join(bundledDirectory, "shared"),
    );
  } finally {
    await scratch.dispose();
  }
});

test("a missing bundled directory scans as empty", async () => {
  const scratch = await createScratchDirectory("catalog");
  try {
    assert.deepEqual(
      await scanExtensionDirectory(join(scratch.path, "absent"), APP_VERSION, "bundled"),
      [],
    );
  } finally {
    await scratch.dispose();
  }
});
