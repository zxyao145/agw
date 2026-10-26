import assert from "node:assert/strict";
import test, { afterEach, before } from "node:test";

type DomEnvironment = Awaited<ReturnType<typeof import("@agw/test-harness").setupDomEnvironment>>;
let environment: DomEnvironment;
let WorkspaceIconView: typeof import("./workspace-icon").WorkspaceIconView;

before(async () => {
  const { setupDomEnvironment } = await import("@agw/test-harness");
  environment = await setupDomEnvironment();
  ({ WorkspaceIconView } = await import("./workspace-icon"));
});
// 在 before 中注册的钩子不作用于之后的测试，这里在文件层清理渲染结果。
// Hooks registered inside before do not apply to later tests, so clean up renders at file level.
afterEach(() => environment.cleanup());

const SVG_SRC = "data:image/svg+xml;base64,PHN2Zy8+";
const PNG_SRC = "data:image/png;base64,AAAA";

test("an SVG icon is a mask filled with the text color", () => {
  const result = environment.render(
    environment.React.createElement(WorkspaceIconView, {
      icon: { kind: "svg", src: SVG_SRC },
      className: "size-5",
    }),
  );
  const icon = result.container.firstElementChild as HTMLElement;
  assert.equal(icon.tagName, "SPAN");
  assert.equal(icon.getAttribute("aria-hidden"), "true");
  assert.equal(result.container.querySelector("img"), null);
  assert.equal(icon.style.getPropertyValue("mask-image"), `url("${SVG_SRC}")`);
  for (const name of ["bg-current", "mask-contain", "mask-center", "mask-no-repeat", "size-5"]) {
    assert.ok(icon.classList.contains(name), name);
  }
  assert.equal(icon.classList.contains("size-4"), false);
});

test("an image icon is an img with the data URL", () => {
  const result = environment.render(
    environment.React.createElement(WorkspaceIconView, {
      icon: { kind: "image", src: PNG_SRC },
    }),
  );
  const image = result.container.firstElementChild as HTMLImageElement;
  assert.equal(image.tagName, "IMG");
  assert.equal(image.getAttribute("src"), PNG_SRC);
  assert.equal(image.style.getPropertyValue("mask-image"), "");
});
