import assert from "node:assert/strict";
import test, { afterEach, before } from "node:test";

import type { WorkspaceEntry } from "../../extensions/workspace-registry";
import { BUILT_IN_VIEWS } from "../../extensions/workspace-ids";
import { WorkspaceLayoutStore } from "../../extensions/workspace-layout-store";
import { activeViewIdOf } from "../../extensions/workspace-tab";
import { activeTabOf } from "../../extensions/workspace-tabs-store";
import { createTestTabs } from "../../extensions/workspace-tabs-store.test-helper";

type DomEnvironment = Awaited<ReturnType<typeof import("@agw/test-harness").setupDomEnvironment>>;
let environment: DomEnvironment;
let Ribbon: typeof import("./ribbon").Ribbon;
let TooltipProvider: typeof import("./components.test-helper").TooltipProvider;
let ExtensionHost: typeof import("../../extensions/extension-host").ExtensionHost;

before(async () => {
  const { setupDomEnvironment } = await import("@agw/test-harness");
  environment = await setupDomEnvironment();
  ({ Ribbon } = await import("./ribbon"));
  ({ TooltipProvider } = await import("./components.test-helper"));
  ({ ExtensionHost } = await import("../../extensions/extension-host"));
});
// 在 before 中注册的钩子不作用于之后的测试，这里在文件层清理渲染结果。
// Hooks registered inside before do not apply to later tests, so clean up renders at file level.
afterEach(() => environment.cleanup());

const entries: WorkspaceEntry[] = [
  {
    id: "agw.ribbon.conversations",
    extensionId: null,
    extensionName: null,
    title: "Conversations",
    icon: { kind: "lucide", name: "MessagesSquare" },
    action: { type: "toggleSidebarView", view: "agw.conversations" },
  },
  {
    id: "agw.ribbon.files",
    extensionId: null,
    extensionName: null,
    title: "Files",
    icon: { kind: "lucide", name: "FolderTree" },
    action: { type: "toggleSidebarView", view: "agw.files" },
  },
  {
    id: "sample:open",
    extensionId: "sample",
    extensionName: "Sample",
    title: "Open",
    icon: { kind: "image", src: "data:image/svg+xml;base64,PHN2Zy8+" },
    action: { type: "openMainView", view: "sample:app" },
  },
];

function renderRibbon(options: {
  sidebarVisible: boolean;
  activeSidebarViewId: string;
  activeMainViewId?: string | null;
  selected: WorkspaceEntry[];
}) {
  const { createElement } = environment.React;
  return environment.render(
    createElement(
      TooltipProvider,
      null,
      createElement(Ribbon, {
        entries,
        sidebarVisible: options.sidebarVisible,
        activeSidebarViewId: options.activeSidebarViewId,
        activeMainViewId:
          options.activeMainViewId === undefined ? BUILT_IN_VIEWS.chat : options.activeMainViewId,
        onSelect: (entry: WorkspaceEntry) => options.selected.push(entry),
      }),
    ) as Parameters<DomEnvironment["render"]>[0],
  );
}

test("clicking an item runs its action", () => {
  const selected: WorkspaceEntry[] = [];
  renderRibbon({ sidebarVisible: true, activeSidebarViewId: "agw.conversations", selected });
  environment.fireEvent.click(environment.screen.getByRole("button", { name: "Files" }));
  environment.fireEvent.click(environment.screen.getByRole("button", { name: "Open (Sample)" }));
  assert.deepEqual(
    selected.map((entry) => entry.id),
    ["agw.ribbon.files", "sample:open"],
  );
});

test("Jobs opens a tab and is highlighted when active; full views hide the sidebar", async (t) => {
  const host = new ExtensionHost({
    bridge: null,
    app: { version: "0.5.0", platform: "darwin", architecture: "arm64" },
    notify: () => undefined,
  });
  const tabs = await createTestTabs(t);
  const workspace = new WorkspaceLayoutStore(host.registry, tabs);
  const render = () => {
    const layout = workspace.getSnapshot();
    const tab = activeTabOf(tabs.getSnapshot().group);
    const { createElement } = environment.React;
    return createElement(
      TooltipProvider,
      null,
      createElement(Ribbon, {
        entries: host.registry.getSnapshot().ribbon,
        sidebarVisible: layout.sidebarVisible,
        activeSidebarViewId: layout.activeSidebarViewId,
        activeMainViewId: layout.fullWorkspaceViewId ?? (tab ? activeViewIdOf(tab) : null),
        onSelect: ({ action }: WorkspaceEntry) => {
          if (action.type === "openMainView") workspace.openMainView(action.view);
          if (action.type === "toggleSidebarView") workspace.toggleSidebarView(action.view);
        },
      }),
    );
  };
  const view = environment.render(render());
  const buttons = environment
    .within(environment.screen.getByRole("navigation"))
    .getAllByRole("button");
  assert.deepEqual(
    buttons.map((button) => button.getAttribute("aria-label")),
    ["Conversations", "Files", "Jobs", "Events", "Notifications"],
  );
  environment.fireEvent.click(environment.screen.getByRole("button", { name: "Jobs" }));
  assert.equal(activeTabOf(tabs.getSnapshot().group)?.kind, "jobs");
  assert.equal(host.registry.view("agw.jobs")?.location, "main");
  view.rerender(render());
  const jobs = environment.screen.getByRole("button", { name: "Jobs" });
  assert.equal(jobs.classList.contains("is-active"), true);
  assert.equal(jobs.getAttribute("aria-pressed"), "true");
  assert.equal(workspace.getSnapshot().sidebarVisible, true);
  environment.fireEvent.click(environment.screen.getByRole("button", { name: "Events" }));
  view.rerender(render());
  assert.equal(workspace.getSnapshot().sidebarVisible, false);
  assert.equal(
    environment.screen.getByRole("button", { name: "Jobs" }).getAttribute("aria-pressed"),
    "false",
  );
  environment.fireEvent.click(environment.screen.getByRole("button", { name: "Files" }));
  view.rerender(render());
  assert.equal(workspace.getSnapshot().fullWorkspaceViewId, null);
  assert.equal(
    environment.screen.getByRole("button", { name: "Jobs" }).getAttribute("aria-pressed"),
    "true",
  );
});

test("empty tabs do not highlight a main-view Ribbon item", () => {
  renderRibbon({
    sidebarVisible: false,
    activeSidebarViewId: "agw.files",
    activeMainViewId: null,
    selected: [],
  });
  assert.equal(
    environment.screen.getByRole("button", { name: "Open (Sample)" }).getAttribute("aria-pressed"),
    "false",
  );
});

test("the item of the shown sidebar view is highlighted", () => {
  renderRibbon({ sidebarVisible: true, activeSidebarViewId: "agw.files", selected: [] });
  const files = environment.screen.getByRole("button", { name: "Files" });
  const conversations = environment.screen.getByRole("button", { name: "Conversations" });
  assert.equal(files.classList.contains("is-active"), true);
  assert.equal(files.getAttribute("aria-pressed"), "true");
  assert.equal(conversations.classList.contains("is-active"), false);
});

test("no item is highlighted while the sidebar is hidden", () => {
  renderRibbon({ sidebarVisible: false, activeSidebarViewId: "agw.files", selected: [] });
  assert.equal(
    environment.screen.getByRole("button", { name: "Files" }).classList.contains("is-active"),
    false,
  );
});

test("built-in items come first, then a separator and extension items with package icons", () => {
  const view = renderRibbon({
    sidebarVisible: true,
    activeSidebarViewId: "agw.conversations",
    selected: [],
  });
  const children = [...view.container.querySelector("nav")!.children];
  assert.deepEqual(
    children.map((child) => child.getAttribute("data-entry-id") ?? child.getAttribute("role")),
    ["agw.ribbon.conversations", "agw.ribbon.files", "separator", "sample:open"],
  );
  const image = environment.screen
    .getByRole("button", { name: "Open (Sample)" })
    .querySelector("img");
  assert.equal(image?.getAttribute("src"), "data:image/svg+xml;base64,PHN2Zy8+");
});
