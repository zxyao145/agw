import assert from "node:assert/strict";
import test, { before } from "node:test";

type DomEnvironment = Awaited<ReturnType<typeof import("@agw/test-harness").setupDomEnvironment>>;
type Components = typeof import("@agw/components");
type Chat = typeof import("@agw/chat");
type Tracker = typeof import("./host-overlay-tracker");

let environment: DomEnvironment;
let components: Components;
let chat: Chat;
let tracker: Tracker;

// desktop 的测试按 CommonJS 编译，DOM 环境在 before 中建立。Desktop tests compile to CommonJS, so the DOM is set up in before.
before(async () => {
  const { setupDomEnvironment } = await import("@agw/test-harness");
  environment = await setupDomEnvironment();
  // Radix 用 DOMRect 描述指针位置。Radix describes pointer positions with DOMRect.
  Object.defineProperty(globalThis, "DOMRect", {
    configurable: true,
    value: environment.window.DOMRect,
  });
  // jsdom 没有布局；给每个元素一个可见的区域，让识别函数按"可见"判断。
  // jsdom has no layout; give every element a visible area so the finder sees it as visible.
  Object.defineProperty(environment.window.HTMLElement.prototype, "getBoundingClientRect", {
    configurable: true,
    value: () => ({
      x: 0,
      y: 0,
      left: 0,
      top: 0,
      right: 100,
      bottom: 100,
      width: 100,
      height: 100,
      toJSON: () => ({}),
    }),
  });
  components = await import("@agw/components");
  chat = await import("@agw/chat");
  tracker = await import("./host-overlay-tracker");
});

const h = (...args: unknown[]) =>
  (environment.React.createElement as (...values: unknown[]) => unknown)(...args);
const act = (callback: () => Promise<void> | void) =>
  environment.act(callback as () => Promise<void>);
const findOpenOverlays = (document: Document) => tracker.findOpenOverlays(document);
const coveredSlots = new Set<string>();

function openSlots(): string[] {
  const slots = findOpenOverlays(document).map((overlay) => overlay.slot);
  for (const slot of slots) coveredSlots.add(slot);
  return [...new Set(slots)].sort();
}

async function settle() {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 0));
  });
}

type Case = {
  name: string;
  element: () => unknown;
  open?: () => Promise<void> | void;
  expected: string[];
};

const PREVIEW_IMAGE = {
  src: "data:image/svg+xml,%3Csvg%20xmlns%3D%22http%3A%2F%2Fwww.w3.org%2F2000%2Fsvg%22%2F%3E",
  alt: "Diagram",
};

const cases: Case[] = [
  {
    name: "ImagePreview",
    element: () => h(chat.ImagePreview, { image: PREVIEW_IMAGE, onClose: () => undefined }),
    expected: ["image-preview"],
  },
  {
    name: "Dialog",
    element: () =>
      h(
        components.Dialog,
        { open: true },
        h(components.DialogContent, null, h(components.DialogTitle, null, "Title")),
      ),
    expected: ["dialog-content", "dialog-overlay"],
  },
  {
    name: "AlertDialog",
    element: () =>
      h(
        components.AlertDialog,
        { open: true },
        h(components.AlertDialogContent, null, h(components.AlertDialogTitle, null, "Confirm")),
      ),
    expected: ["alert-dialog-content", "alert-dialog-overlay"],
  },
  {
    name: "Sheet",
    element: () =>
      h(
        components.Sheet,
        { open: true },
        h(components.SheetContent, null, h(components.SheetTitle, null, "Sheet")),
      ),
    expected: ["sheet-content", "sheet-overlay"],
  },
  {
    name: "Drawer",
    element: () =>
      h(
        components.Drawer,
        { open: true },
        h(components.DrawerContent, null, h(components.DrawerTitle, null, "Drawer")),
      ),
    expected: ["drawer-content", "drawer-overlay"],
  },
  {
    name: "Popover",
    element: () =>
      h(
        components.Popover,
        { open: true },
        h(components.PopoverTrigger, null, "Open"),
        h(components.PopoverContent, null, "Popover body"),
      ),
    expected: ["popover-content"],
  },
  {
    name: "DropdownMenu with a submenu",
    element: () =>
      h(
        components.DropdownMenu,
        { open: true },
        h(components.DropdownMenuTrigger, null, "Menu"),
        h(
          components.DropdownMenuContent,
          null,
          h(components.DropdownMenuItem, null, "First"),
          h(
            components.DropdownMenuSub,
            { open: true },
            h(components.DropdownMenuSubTrigger, null, "More"),
            h(
              components.DropdownMenuSubContent,
              null,
              h(components.DropdownMenuItem, null, "Nested"),
            ),
          ),
        ),
      ),
    expected: ["dropdown-menu-content", "dropdown-menu-sub-content"],
  },
  {
    name: "ContextMenu with a submenu",
    element: () =>
      h(
        components.ContextMenu,
        null,
        h(components.ContextMenuTrigger, null, h("div", null, "Right-click target")),
        h(
          components.ContextMenuContent,
          null,
          h(components.ContextMenuItem, null, "Delete"),
          h(
            components.ContextMenuSub,
            { open: true },
            h(components.ContextMenuSubTrigger, null, "More"),
            h(
              components.ContextMenuSubContent,
              null,
              h(components.ContextMenuItem, null, "Nested"),
            ),
          ),
        ),
      ),
    open: () => {
      environment.fireEvent.contextMenu(environment.screen.getByText("Right-click target"), {
        clientX: 10,
        clientY: 10,
      });
    },
    expected: ["context-menu-content", "context-menu-sub-content"],
  },
  {
    name: "Select",
    element: () =>
      h(
        components.Select,
        { open: true, value: "a" },
        h(components.SelectTrigger, null, h(components.SelectValue, null)),
        h(components.SelectContent, null, h(components.SelectItem, { value: "a" }, "A")),
      ),
    expected: ["select-content"],
  },
  {
    name: "Tooltip",
    element: () =>
      h(
        components.TooltipProvider,
        null,
        h(
          components.Tooltip,
          { open: true },
          h(components.TooltipTrigger, null, "Hover"),
          h(components.TooltipContent, null, "Tip"),
        ),
      ),
    expected: ["tooltip-content"],
  },
  {
    name: "SearchableSelect",
    element: () =>
      h(components.SearchableSelect, {
        id: "choice",
        ariaLabel: "Choice",
        value: "",
        onValueChange: () => undefined,
        options: [
          { value: "a", title: "Alpha" },
          { value: "b", title: "Beta" },
        ],
        placeholder: "Pick one",
        searchPlaceholder: "Search",
      }),
    open: () => {
      environment.fireEvent.click(environment.screen.getByRole("combobox", { name: "Choice" }));
    },
    expected: ["combobox-content"],
  },
];

for (const { name, element, open, expected } of cases) {
  test(`${name} is found while open and not after it is removed`, async () => {
    const view = environment.render(element() as Parameters<DomEnvironment["render"]>[0]);
    await settle();
    if (open) {
      await act(async () => {
        await open();
      });
      await settle();
    }
    const found = openSlots();
    for (const slot of expected)
      assert.ok(found.includes(slot), `${name}: ${slot} not found in ${found.join(", ")}`);
    view.unmount();
    await settle();
    assert.deepEqual(openSlots(), [], name);
  });
}

test("the image preview covers the whole window, so every extension view gets a snapshot", async () => {
  const view = environment.render(
    h(chat.ImagePreview, { image: PREVIEW_IMAGE, onClose: () => undefined }) as Parameters<
      DomEnvironment["render"]
    >[0],
  );
  await settle();
  const preview = findOpenOverlays(document).find((overlay) => overlay.slot === "image-preview");
  assert.equal(preview?.modal, true);
  assert.equal(chat.IMAGE_PREVIEW_SLOT, "image-preview");
  view.unmount();
  await settle();
});

test("sonner toasts are found", async () => {
  const view = environment.render(
    h(components.Toaster, null) as Parameters<DomEnvironment["render"]>[0],
  );
  await act(async () => {
    components.toast("Saved");
    await new Promise((resolve) => setTimeout(resolve, 50));
  });
  const overlays = findOpenOverlays(document);
  assert.ok(overlays.some((overlay) => overlay.slot === "sonner-toast"));
  view.unmount();
});

test("ordinary elements whose slot ends with -content are ignored", () => {
  const view = environment.render(
    h(
      "div",
      null,
      ...["card-content", "accordion-content", "sidebar-content", "item-content"].map((slot) =>
        h("div", { key: slot, "data-slot": slot }, slot),
      ),
    ) as Parameters<DomEnvironment["render"]>[0],
  );
  assert.deepEqual(openSlots(), []);
  view.unmount();
});

test("hidden overlays are ignored", () => {
  const view = environment.render(
    h(
      "div",
      { "data-slot": "popover-content", style: { visibility: "hidden" } },
      "hidden",
    ) as Parameters<DomEnvironment["render"]>[0],
  );
  assert.deepEqual(findOpenOverlays(document), []);
  view.unmount();
});

test("every OVERLAY_SLOTS entry is covered by a case", () => {
  for (const slot of tracker.OVERLAY_SLOTS)
    assert.ok(coveredSlots.has(slot), `${slot} has no test case`);
});

test("overlapping overlays obscure only the frames they cover; modal overlays obscure all", () => {
  const frames = [
    { frameId: "left", left: 0, top: 0, right: 100, bottom: 100 },
    { frameId: "right", left: 200, top: 0, right: 300, bottom: 100 },
  ];
  const { computeObscuredFrames } = tracker;
  const menu = {
    left: 50,
    top: 50,
    right: 120,
    bottom: 80,
    modal: false,
    slot: "dropdown-menu-content",
  };
  assert.deepEqual([...computeObscuredFrames([menu], frames)], ["left"]);
  assert.deepEqual([...computeObscuredFrames([], frames)], []);
  const overlay = { left: 0, top: 0, right: 1, bottom: 1, modal: true, slot: "dialog-overlay" };
  assert.deepEqual([...computeObscuredFrames([overlay], frames)].sort(), ["left", "right"]);
  assert.deepEqual([...computeObscuredFrames([], frames, true)].sort(), ["left", "right"]);
});
