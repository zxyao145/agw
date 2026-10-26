import assert from "node:assert/strict";
import test, { afterEach, before } from "node:test";

import type { ExtensionHostSnapshot } from "../../extensions/extension-host";
import type { WorkspaceView } from "../../extensions/workspace-registry";

type DomEnvironment = Awaited<ReturnType<typeof import("@agw/test-harness").setupDomEnvironment>>;
type ViewModule = typeof import("./extension-view");
type SlotModule = typeof import("./extension-view-slot");

let environment: DomEnvironment;
let views: ViewModule;
let slots: SlotModule;
let host: import("../../extensions/extension-host").ExtensionHost;
let layout: import("../../extensions/extension-layout-sampler").ExtensionLayoutSampler;

before(async () => {
  const { setupDomEnvironment } = await import("@agw/test-harness");
  environment = await setupDomEnvironment();
  views = await import("./extension-view");
  slots = await import("./extension-view-slot");
  const { ExtensionHost } = await import("../../extensions/extension-host");
  const { HostOverlayTracker } = await import("../../extensions/host-overlay-tracker");
  const { createLayoutSampler } = await import("../../extensions/extension-runtime");
  // 普通浏览器模式的宿主：布局与视图组件照常工作，不连接主进程。A browser-mode host: layout and views work without main.
  host = new ExtensionHost({
    bridge: null,
    app: { version: "0.5.0", platform: "darwin", architecture: "arm64" },
    notify: () => undefined,
  });
  layout = createLayoutSampler(host, new HostOverlayTracker(document, () => undefined));
});
afterEach(() => environment.cleanup());

const view: WorkspaceView = {
  id: "sample:app",
  location: "main",
  title: "Greeting app",
  icon: { kind: "image", src: "data:image/svg+xml;base64,PHN2Zy8+" },
  extensionId: "sample",
  extensionName: "Sample",
  entry: "index.html",
  retainContextWhenHidden: false,
  failure: null,
  priority: "default",
};

function snapshot(overrides: Partial<ExtensionHostSnapshot> = {}): ExtensionHostSnapshot {
  return {
    started: true,
    safeMode: null,
    recoveryNotices: [],
    extensions: [],
    viewErrors: {},
    unsavedViewIds: [],
    saveStatuses: {},
    fileHeaderItems: {},
    imagePreview: null,
    ...overrides,
  };
}

function render(element: unknown) {
  return environment.render(element as Parameters<DomEnvironment["render"]>[0]);
}

test("the title bar names the view and its extension and reloads the view", () => {
  let reloads = 0;
  const result = render(
    environment.React.createElement(views.ExtensionViewHeader, {
      view,
      onReload: () => (reloads += 1),
    }),
  );
  const header = result.container.querySelector(".agw-extension-view-header")!;
  assert.match(header.textContent ?? "", /Greeting app/u);
  assert.match(header.textContent ?? "", /Sample/u);
  assert.equal(
    header.querySelector("img")?.getAttribute("src"),
    view.icon.kind === "image" ? view.icon.src : null,
  );
  environment.fireEvent.click(environment.screen.getByRole("button", { name: "Reload view" }));
  assert.equal(reloads, 1);
});

test("the file viewer info for the More menu names the viewer and its extension", () => {
  const { createElement } = environment.React;
  const result = render(
    createElement("div", null, createElement(views.ExtensionViewInfo, { view })),
  );
  const info = result.container.firstElementChild!;
  assert.equal(info.textContent, "Greeting appSample");
  assert.equal(
    info.querySelector("img")?.getAttribute("src"),
    view.icon.kind === "image" ? view.icon.src : null,
  );
});

test("the slot shows the snapshot while a host overlay covers the view", () => {
  const { createElement } = environment.React;
  const covered = render(
    createElement(slots.ExtensionViewSlot, {
      host,
      layout,
      frameId: "frame-1",
      snapshot: "data:image/jpeg;base64,AAAA",
    }),
  );
  const slot = covered.container.querySelector(".agw-extension-view-slot")!;
  assert.equal(slot.classList.contains("is-snapshot"), true);
  assert.equal(slot.querySelector("img")?.getAttribute("src"), "data:image/jpeg;base64,AAAA");
  covered.unmount();

  const shown = render(
    createElement(slots.ExtensionViewSlot, { host, layout, frameId: "frame-1", snapshot: null }),
  );
  assert.equal(shown.container.querySelector(".agw-extension-view-slot img"), null);
});

test("a view whose handshake failed shows an error panel with Reload view", () => {
  render(
    environment.React.createElement(views.ExtensionView, {
      host,
      layout,
      snapshot: snapshot({
        viewErrors: {
          "sample:app": {
            code: "HANDSHAKE_TIMEOUT",
            message: "The page did not connect within 15 seconds.",
          },
        },
      }),
      view,
      openRequest: undefined,
    }),
  );
  const alert = environment.screen.getByRole("alert");
  assert.match(alert.textContent ?? "", /Sample · Greeting app/u);
  assert.match(alert.textContent ?? "", /HANDSHAKE_TIMEOUT: The page did not connect/u);
  assert.ok(environment.within(alert).getByRole("button", { name: "Reload view" }));
});

test("the failed-extension panel shows the error and reloads the extension", () => {
  let reloads = 0;
  const failure = { code: "CRASHED", message: "The page process exited (crashed)." };
  render(
    environment.React.createElement(views.ExtensionErrorView, {
      view: { ...view, failure },
      error: failure,
      onReloadExtension: () => (reloads += 1),
    }),
  );
  const alert = environment.screen.getByRole("alert");
  assert.match(alert.textContent ?? "", /Sample · Greeting app/u);
  assert.match(alert.textContent ?? "", /CRASHED: The page process exited/u);
  environment.fireEvent.click(
    environment.within(alert).getByRole("button", { name: "Reload extension" }),
  );
  assert.equal(reloads, 1);
});

test("a failed view inside ExtensionView renders the failed-extension panel", () => {
  render(
    environment.React.createElement(views.ExtensionView, {
      host,
      layout,
      snapshot: snapshot(),
      view: { ...view, failure: { code: "UNRESPONSIVE", message: "The page stopped responding." } },
      openRequest: undefined,
    }),
  );
  assert.ok(environment.screen.getByRole("button", { name: "Reload extension" }));
});
