import assert from "node:assert/strict";
import test, { afterEach, before } from "node:test";

import type { WorkspaceEntry } from "../../extensions/workspace-registry";

type DomEnvironment = Awaited<ReturnType<typeof import("@agw/test-harness").setupDomEnvironment>>;
let environment: DomEnvironment;
let Ribbon: typeof import("./ribbon").Ribbon;
let TooltipProvider: typeof import("./components.test-helper").TooltipProvider;

before(async () => {
  const { setupDomEnvironment } = await import("@agw/test-harness");
  environment = await setupDomEnvironment();
  ({ Ribbon } = await import("./ribbon"));
  ({ TooltipProvider } = await import("./components.test-helper"));
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
