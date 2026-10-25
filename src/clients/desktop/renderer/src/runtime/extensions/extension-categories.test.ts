import assert from "node:assert/strict";
import test from "node:test";

import type { InstalledExtension } from "@desktop/shared/contracts";
import { extensionCategories, extensionsInCategory, themeTitles } from "./extension-categories";
import type { ExtensionState } from "./extension-host";
import { sampleExtension } from "./extension-host.test-helper";

function extension(installed: InstalledExtension): ExtensionState {
  return {
    id: installed.id,
    installed,
    status: "inactive",
    error: null,
    blockedRequests: [],
    frames: [],
  };
}

const viewer = extension(
  sampleExtension([
    { id: "markdown", title: "Markdown", icon: "FileText", patterns: ["*.md"], entry: "md.html" },
  ]),
);
const commandsOnly = extension({
  ...sampleExtension([]),
  id: "snippets",
  manifest: {
    manifestVersion: 1,
    id: "snippets",
    name: "Snippets",
    version: "1.0.0",
    contributes: {
      chat: {
        composerActions: [
          {
            id: "insert",
            title: "Insert",
            icon: "Plus",
            action: { type: "command", command: "insert" },
          },
        ],
      },
    },
  },
});
const themeOnly = extension({
  ...sampleExtension([]),
  id: "soft-theme",
  enabled: false,
  manifest: {
    manifestVersion: 1,
    id: "soft-theme",
    name: "Soft Theme",
    version: "1.0.0",
    contributes: { themes: [{ id: "soft", title: "Soft", path: "themes/soft.json" }] },
  },
});
const themeWithViews = extension({
  ...sampleExtension([]),
  id: "studio",
  validation: "invalid",
  manifest: {
    manifestVersion: 1,
    id: "studio",
    name: "Studio",
    version: "1.0.0",
    contributes: {
      mainViews: [{ id: "app", title: "App", icon: "Box", entry: "index.html" }],
      themes: [
        { id: "day", title: "Day", path: "themes/day.json" },
        { id: "night", title: "Night", path: "themes/night.json" },
      ],
    },
  },
});
const all = [themeOnly, viewer, themeWithViews, commandsOnly];

test("category=themes lists only extensions that contribute themes, enabled or not", () => {
  assert.deepEqual(
    extensionsInCategory(all, "themes")?.map((item) => item.id),
    ["soft-theme", "studio"],
  );
});

test("the file viewer, view and command categories follow the matching contributions", () => {
  assert.deepEqual(
    extensionsInCategory(all, "file-viewers")?.map((item) => item.id),
    ["sample"],
  );
  assert.deepEqual(
    extensionsInCategory(all, "views")?.map((item) => item.id),
    ["studio"],
  );
  assert.deepEqual(
    extensionsInCategory(all, "commands")?.map((item) => item.id),
    ["snippets"],
  );
});

test("an extension lists every category it belongs to, in category order", () => {
  assert.deepEqual(extensionCategories(themeWithViews), ["themes", "views"]);
  assert.deepEqual(extensionCategories(viewer), ["file-viewers"]);
  assert.deepEqual(extensionCategories(extension(sampleExtension([]))), []);
});

test("no category lists every extension and an unknown category lists none", () => {
  assert.deepEqual(extensionsInCategory(all, null), all);
  assert.equal(extensionsInCategory(all, "widgets"), null);
  assert.equal(extensionsInCategory(all, "toString"), null);
});

test("theme titles come from the manifest", () => {
  assert.deepEqual(themeTitles(themeWithViews.installed.manifest), ["Day", "Night"]);
  assert.deepEqual(themeTitles(viewer.installed.manifest), []);
  assert.deepEqual(themeTitles(null), []);
});
