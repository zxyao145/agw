import assert from "node:assert/strict";
import test from "node:test";

import type { ExtensionFileViewerContribution } from "@agw/extension-sdk";
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
    priority: "default",
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
  themes: [],
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
    registry.fileViewersFor("README.md", {}).map((view) => view.id),
    ["alpha:md", "sample:markdown", "sample:readme"],
  );
  assert.deepEqual(
    registry.fileViewersFor("notes.md", {}).map((view) => view.id),
    ["alpha:md", "sample:markdown"],
  );
  assert.deepEqual(registry.fileViewersFor("diagram.excalidraw", {}), []);

  // 停用后不再匹配；失败时保留并标记。Unregistered viewers stop matching; failed ones stay marked.
  registry.unregisterExtension("alpha");
  registry.markExtensionFailed(
    "sample",
    { code: "UNRESPONSIVE", message: "gone" },
    extensionRegistration(sample).views,
  );
  const failed = registry.fileViewersFor("notes.md", {});
  assert.deepEqual(
    failed.map((view) => [view.id, view.failure?.code]),
    [["sample:markdown", "UNRESPONSIVE"]],
  );
});

/** 只声明给定文件查看器的 extension 的注册信息。The registration of an extension declaring only the given file viewers. */
function viewerRegistration(id: string, fileViewers: ExtensionFileViewerContribution[]) {
  return extensionRegistration({
    ...sample,
    id,
    manifest: { ...sample.manifest!, id, name: id, contributes: { fileViewers } },
  });
}

/**
 * `code`（alpha）匹配全部文件、声明为 option；sample 的两个查看器没有声明 priority；zeta 的 `md` 声明为 default。
 * `code` (alpha) matches every file as an option; sample's two viewers declare no priority; zeta's `md` is a default.
 */
function createPriorityRegistry(): WorkspaceRegistry {
  const registry = createRegistry();
  registry.registerExtension("sample", extensionRegistration(sample));
  registry.registerExtension(
    "alpha",
    viewerRegistration("alpha", [
      {
        id: "code",
        title: "Code",
        icon: "Code",
        patterns: ["*"],
        entry: "code.html",
        priority: "option",
      },
    ]),
  );
  registry.registerExtension(
    "zeta",
    viewerRegistration("zeta", [
      {
        id: "md",
        title: "Zeta MD",
        icon: "Box",
        patterns: ["*.md"],
        entry: "md.html",
        priority: "default",
      },
    ]),
  );
  return registry;
}

function viewerIds(
  registry: WorkspaceRegistry,
  fileName: string,
  defaults: Record<string, string>,
) {
  return registry.fileViewersFor(fileName, defaults).map((view) => view.id);
}

test("file viewers take their priority from the manifest, default when absent", () => {
  const registry = createPriorityRegistry();
  assert.equal(registry.view("alpha:code")?.priority, "option");
  assert.equal(registry.view("zeta:md")?.priority, "default");
  assert.equal(registry.view("sample:markdown")?.priority, "default");
});

test("the user's default comes first, then default viewers by extension id, then option viewers", () => {
  const registry = createPriorityRegistry();
  assert.deepEqual(viewerIds(registry, "notes.md", {}), [
    "sample:markdown",
    "zeta:md",
    "alpha:code",
  ]);
  assert.deepEqual(viewerIds(registry, "notes.md", { "*.md": "zeta:md" }), [
    "zeta:md",
    "sample:markdown",
    "alpha:code",
  ]);
  assert.deepEqual(viewerIds(registry, "notes.md", { "*.md": "alpha:code" }), [
    "alpha:code",
    "sample:markdown",
    "zeta:md",
  ]);
  // Source 由文件视图选中，查看器按 priority 排序。Source is chosen by the File view; viewers keep the priority order.
  assert.deepEqual(viewerIds(registry, "notes.md", { "*.md": "source" }), [
    "sample:markdown",
    "zeta:md",
    "alpha:code",
  ]);
  // 同一 extension 内按声明顺序。Declaration order within one extension.
  assert.deepEqual(viewerIds(registry, "README.md", {}), [
    "sample:markdown",
    "sample:readme",
    "zeta:md",
    "alpha:code",
  ]);
});

test("an option viewer comes first when it is the only match", () => {
  const registry = createPriorityRegistry();
  assert.deepEqual(viewerIds(registry, "app.ts", {}), ["alpha:code"]);
  assert.deepEqual(viewerIds(registry, "constructor", {}), ["alpha:code"]);
});

test("a default that does not match the file or whose extension is disabled falls back to priority order", () => {
  const registry = createPriorityRegistry();
  assert.deepEqual(viewerIds(registry, "notes.md", { "*.md": "sample:readme" }), [
    "sample:markdown",
    "zeta:md",
    "alpha:code",
  ]);
  assert.deepEqual(viewerIds(registry, "notes.md", { "*.md": "missing:viewer" }), [
    "sample:markdown",
    "zeta:md",
    "alpha:code",
  ]);

  const defaults = { "*.md": "zeta:md" };
  registry.unregisterExtension("zeta");
  assert.deepEqual(viewerIds(registry, "notes.md", defaults), ["sample:markdown", "alpha:code"]);
  // 重新启用后设置再次生效。The setting applies again once the extension is enabled again.
  registry.registerExtension(
    "zeta",
    viewerRegistration("zeta", [
      { id: "md", title: "Zeta MD", icon: "Box", patterns: ["*.md"], entry: "md.html" },
    ]),
  );
  assert.deepEqual(viewerIds(registry, "notes.md", defaults), [
    "zeta:md",
    "sample:markdown",
    "alpha:code",
  ]);
});

test("the file type is the lower-cased text after the last dot, or the whole lower-cased name", () => {
  const registry = createPriorityRegistry();
  assert.deepEqual(viewerIds(registry, "README.MD", { "*.md": "sample:readme" }), [
    "sample:readme",
    "sample:markdown",
    "zeta:md",
    "alpha:code",
  ]);
  assert.deepEqual(viewerIds(registry, "README.MD", { "*.MD": "sample:readme" }), [
    "sample:markdown",
    "sample:readme",
    "zeta:md",
    "alpha:code",
  ]);

  registry.registerExtension(
    "docker",
    viewerRegistration("docker", [
      { id: "file", title: "Docker", icon: "Box", patterns: ["Dockerfile"], entry: "d.html" },
    ]),
  );
  assert.deepEqual(viewerIds(registry, "Dockerfile", {}), ["docker:file", "alpha:code"]);
  assert.deepEqual(viewerIds(registry, "Dockerfile", { dockerfile: "alpha:code" }), [
    "alpha:code",
    "docker:file",
  ]);
  assert.deepEqual(viewerIds(registry, "Dockerfile", { Dockerfile: "alpha:code" }), [
    "docker:file",
    "alpha:code",
  ]);

  registry.registerExtension(
    "archive",
    viewerRegistration("archive", [
      { id: "tar", title: "Tar", icon: "Box", patterns: ["*.tar.gz"], entry: "t.html" },
    ]),
  );
  assert.deepEqual(viewerIds(registry, "a.tar.gz", { "*.gz": "alpha:code" }), [
    "alpha:code",
    "archive:tar",
  ]);
  assert.deepEqual(viewerIds(registry, "a.tar.gz", { "*.tar.gz": "alpha:code" }), [
    "archive:tar",
    "alpha:code",
  ]);
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
    kind: "svg",
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
