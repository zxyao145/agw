import assert from "node:assert/strict";
import test from "node:test";

import type { InstalledExtension } from "@desktop/shared/contracts";
import { extensionRegistration, extensionViews } from "./extension-contributions";
import { BUILT_IN_VIEWS } from "./workspace-ids";
import { WorkspaceLayoutStore } from "./workspace-layout-store";
import { type WorkspaceView, WorkspaceRegistry } from "./workspace-registry";

function builtIn(id: string, location: "sidebar" | "main"): WorkspaceView {
  return {
    id,
    location,
    title: id,
    icon: { kind: "lucide", name: "Box" },
    extensionId: null,
    extensionName: null,
    entry: null,
    retainContextWhenHidden: false,
    failure: null,
  };
}

function createRegistry(): WorkspaceRegistry {
  return new WorkspaceRegistry({
    views: [
      builtIn(BUILT_IN_VIEWS.conversations, "sidebar"),
      builtIn(BUILT_IN_VIEWS.files, "sidebar"),
      builtIn(BUILT_IN_VIEWS.chat, "main"),
      builtIn(BUILT_IN_VIEWS.file, "main"),
    ],
    entries: {
      ribbon: [
        {
          id: "agw.ribbon.files",
          extensionId: null,
          extensionName: null,
          title: "Files",
          icon: { kind: "lucide", name: "FolderTree" },
          action: { type: "toggleSidebarView", view: BUILT_IN_VIEWS.files },
        },
      ],
    },
  });
}

const sample: InstalledExtension = {
  id: "sample",
  source: "user",
  directory: "/extensions/sample",
  validation: "valid",
  errors: [],
  enabled: true,
  permissionApprovalRequired: false,
  grantedPermissions: [],
  grantedHostPermissions: [],
  lastActivatedVersion: null,
  icons: { "icon.svg": "data:image/svg+xml;base64,PHN2Zy8+" },
  manifest: {
    manifestVersion: 1,
    id: "sample",
    name: "Sample",
    version: "1.0.0",
    background: "background.js",
    contributes: {
      commands: [{ id: "greet", title: "Greet" }],
      ribbon: [
        {
          id: "open",
          title: "Open",
          icon: "icon.svg",
          action: { type: "openMainView", view: "app" },
        },
      ],
      sidebarViews: [{ id: "list", title: "List", icon: "Hand", entry: "sidebar.html" }],
      mainViews: [
        {
          id: "app",
          title: "App",
          icon: "icon.svg",
          entry: "index.html",
          retainContextWhenHidden: true,
        },
      ],
      chat: {
        composerActions: [
          {
            id: "insert",
            title: "Insert",
            icon: "Hand",
            action: { type: "command", command: "greet" },
          },
        ],
        messageActions: [
          {
            id: "show",
            title: "Show",
            icon: "icon.svg",
            action: { type: "openMainView", view: "app" },
          },
        ],
      },
      explorerActions: [
        {
          id: "explore",
          title: "Explore",
          icon: "Hand",
          action: { type: "toggleSidebarView", view: "list" },
        },
      ],
      fileViewers: [
        {
          id: "markdown",
          title: "Markdown",
          icon: "BookOpen",
          patterns: ["*.md"],
          entry: "md.html",
        },
        {
          id: "readme",
          title: "Readme",
          icon: "Book",
          patterns: ["README*"],
          entry: "readme.html",
        },
      ],
    },
  },
};

/** 另一个声明 `*.md` 查看器的 extension，id 排在 sample 之前。Another extension with a `*.md` viewer, sorted before sample. */
function alphaRegistration() {
  return extensionRegistration({
    ...sample,
    id: "alpha",
    manifest: {
      ...sample.manifest!,
      id: "alpha",
      name: "Alpha",
      contributes: {
        fileViewers: [
          { id: "md", title: "Alpha MD", icon: "Box", patterns: ["*.MD"], entry: "index.html" },
        ],
      },
    },
  });
}

test("file viewers match by name, ordered by extension id and then declaration order", () => {
  const registry = createRegistry();
  registry.registerExtension("sample", extensionRegistration(sample));
  registry.registerExtension("alpha", alphaRegistration());

  const viewer = registry.view("sample:markdown")!;
  assert.equal(viewer.location, "file");
  assert.equal(viewer.retainContextWhenHidden, true);
  assert.deepEqual(viewer.patterns, ["*.md"]);
  assert.deepEqual(
    registry.fileViewersFor("README.md").map((view) => view.id),
    ["alpha:md", "sample:markdown", "sample:readme"],
  );
  assert.deepEqual(
    registry.fileViewersFor("notes.md").map((view) => view.id),
    ["alpha:md", "sample:markdown"],
  );
  assert.deepEqual(registry.fileViewersFor("diagram.excalidraw"), []);

  // 停用后不再匹配；失败时保留并标记。Unregistered viewers stop matching; failed ones stay marked.
  registry.unregisterExtension("alpha");
  registry.markExtensionFailed(
    "sample",
    { code: "UNRESPONSIVE", message: "gone" },
    extensionRegistration(sample).views,
  );
  const failed = registry.fileViewersFor("notes.md");
  assert.deepEqual(
    failed.map((view) => [view.id, view.failure?.code]),
    [["sample:markdown", "UNRESPONSIVE"]],
  );
});

test("registering an extension adds its views and entries and notifies subscribers", () => {
  const registry = createRegistry();
  let notifications = 0;
  registry.subscribe(() => (notifications += 1));
  registry.registerExtension("sample", extensionRegistration(sample));

  const snapshot = registry.getSnapshot();
  assert.equal(notifications, 1);
  assert.equal(snapshot.views.get("sample:app")?.retainContextWhenHidden, true);
  assert.equal(snapshot.views.get("sample:list")?.location, "sidebar");
  assert.deepEqual(
    snapshot.ribbon.map((entry) => entry.id),
    ["agw.ribbon.files", "sample:open"],
  );
  assert.deepEqual(snapshot.ribbon[1].action, { type: "openMainView", view: "sample:app" });
  assert.deepEqual(snapshot.ribbon[1].icon, {
    kind: "image",
    src: "data:image/svg+xml;base64,PHN2Zy8+",
  });
  assert.deepEqual(snapshot.composerActions[0].action, { type: "command", command: "greet" });
  assert.deepEqual(snapshot.composerActions[0].icon, { kind: "lucide", name: "Hand" });
  assert.equal(snapshot.messageActions.length, 1);
  assert.deepEqual(snapshot.explorerActions[0].action, {
    type: "toggleSidebarView",
    view: "sample:list",
  });
});

test("duplicate view ids and repeated registrations throw", () => {
  const registry = createRegistry();
  registry.registerExtension("sample", extensionRegistration(sample));
  assert.throws(
    () => registry.registerExtension("sample", extensionRegistration(sample)),
    /already registered/u,
  );
  assert.throws(
    () =>
      registry.registerExtension("other", {
        views: [{ ...builtIn("sample:app", "main"), extensionId: "other" }],
        entries: {},
      }),
    /View sample:app is already registered/u,
  );
});

test("unregistering removes entries and views at once and the layout falls back to the defaults", () => {
  const registry = createRegistry();
  const layout = new WorkspaceLayoutStore(registry);
  registry.registerExtension("sample", extensionRegistration(sample));
  layout.openMainView("sample:app");
  layout.showSidebarView("sample:list");
  assert.equal(layout.getSnapshot().activeMainViewId, "sample:app");

  registry.unregisterExtension("sample");

  const snapshot = registry.getSnapshot();
  assert.equal(snapshot.views.has("sample:app"), false);
  assert.deepEqual(
    snapshot.ribbon.map((entry) => entry.id),
    ["agw.ribbon.files"],
  );
  assert.equal(snapshot.composerActions.length, 0);
  assert.equal(layout.getSnapshot().activeMainViewId, BUILT_IN_VIEWS.chat);
  assert.equal(layout.getSnapshot().activeSidebarViewId, BUILT_IN_VIEWS.conversations);
});

test("a failed extension loses its entries but keeps its views marked as failed until unregistered", () => {
  const registry = createRegistry();
  const layout = new WorkspaceLayoutStore(registry);
  registry.registerExtension("sample", extensionRegistration(sample));
  layout.openMainView("sample:app");

  const failure = { code: "CRASHED", message: "The page process exited (crashed)." };
  registry.markExtensionFailed("sample", failure, extensionViews(sample));

  const snapshot = registry.getSnapshot();
  assert.equal(
    snapshot.ribbon.some((entry) => entry.extensionId === "sample"),
    false,
  );
  assert.equal(snapshot.composerActions.length, 0);
  assert.deepEqual(snapshot.views.get("sample:app")?.failure, failure);
  assert.equal(layout.getSnapshot().activeMainViewId, "sample:app");

  registry.unregisterExtension("sample");
  assert.equal(registry.getSnapshot().views.has("sample:app"), false);
  assert.equal(layout.getSnapshot().activeMainViewId, BUILT_IN_VIEWS.chat);
});
