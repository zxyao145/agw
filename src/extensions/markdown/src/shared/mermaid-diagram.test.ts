import assert from "node:assert/strict";
import { after, test } from "node:test";

import { createEditHost, settle, until } from "../editor/test-edit-host";
import { diagramImage, MermaidDiagram, PREVIEW_IMAGE_NAME } from "./mermaid-diagram";
import { renderMermaid } from "./mermaid-render";
import { DARK_NODE, darkTheme, flowchart, LIGHT_NODE, svgId } from "./test-mermaid";

const host = createEditHost();
after(() => host.close());

const LIGHT = host.api.theme.get();

/** 图表区域中的 SVG 文字；没有时为 null。The SVG text in the diagram area, or null. */
function shownSvg(diagram: MermaidDiagram): string | null {
  return diagram.dom.querySelector(":scope > svg")?.outerHTML ?? null;
}

function track(): { states: string[]; diagram: MermaidDiagram } {
  const states: string[] = [];
  const diagram = new MermaidDiagram(host.api, () => states.push(diagram.state));
  return { states, diagram };
}

test("a diagram moves between empty, pending, rendered and failed with its source", async () => {
  const { states, diagram } = track();
  diagram.update("");
  assert.equal(diagram.state, "empty");
  assert.deepEqual(states, []);

  diagram.update(flowchart("Start", "End"));
  assert.equal(diagram.state, "pending");
  await until(() => diagram.state === "rendered", "the first diagram");
  assert.ok(shownSvg(diagram)!.includes("Start"));

  // 新的渲染完成之前保留当前图表。The current diagram stays until the new render finishes.
  diagram.update("flowchart LR\n  a -->");
  assert.equal(diagram.state, "rendered");
  assert.ok(shownSvg(diagram)!.includes("Start"));
  await until(() => diagram.state === "failed", "the syntax error");
  assert.equal(shownSvg(diagram), null);
  assert.match(diagram.dom.querySelector(".mermaid-diagram-error")!.textContent!, /Parse error/);

  diagram.update(flowchart("Fixed", "End"));
  await until(() => diagram.state === "rendered", "the fixed diagram");
  assert.ok(shownSvg(diagram)!.includes("Fixed"));

  diagram.update("  \n");
  assert.equal(diagram.state, "empty");
  assert.equal(diagram.dom.childNodes.length, 0);
  assert.deepEqual(states, ["pending", "rendered", "failed", "rendered", "empty"]);
  diagram.destroy();
});

test("updates during a render merge into one render of the latest source", async () => {
  const before = svgId(await renderMermaid(flowchart("A", "B"), LIGHT));
  const { states, diagram } = track();
  diagram.update(flowchart("First", "End"));
  diagram.update(flowchart("Second", "End"));
  diagram.update(flowchart("Third", "End"));
  await until(() => diagram.state === "rendered", "the merged render");
  const svg = shownSvg(diagram)!;
  // 第一次渲染的结果作废，第二次渲染使用最新的源码。The first render's result is dropped; the second uses the latest source.
  assert.equal(svgId(svg), before + 2);
  assert.ok(svg.includes("Third") && !svg.includes("Second") && !svg.includes("First"));
  assert.deepEqual(states, ["pending", "rendered"]);
  diagram.destroy();
});

test("a destroyed diagram ignores the render it started", async () => {
  const { states, diagram } = track();
  diagram.update(flowchart("Gone", "End"));
  diagram.destroy();
  // mermaid 依次执行渲染，后一次渲染完成时前一次已经完成。mermaid renders in order, so the earlier one is done first.
  await renderMermaid(flowchart("A", "B"), LIGHT);
  await settle();
  assert.equal(diagram.dom.childNodes.length, 0);
  assert.equal(diagram.state, "pending");
  assert.deepEqual(states, ["pending"]);
});

test("a theme change renders the diagram again in the new theme", async () => {
  const { diagram } = track();
  diagram.update(flowchart("Theme", "End"));
  await until(() => diagram.state === "rendered", "the light diagram");
  assert.match(shownSvg(diagram)!, LIGHT_NODE);
  try {
    host.changeTheme(darkTheme(LIGHT));
    await until(() => DARK_NODE.test(shownSvg(diagram)!), "the dark diagram");
  } finally {
    host.changeTheme(LIGHT);
  }
  await until(() => LIGHT_NODE.test(shownSvg(diagram)!), "the light diagram again");
  diagram.destroy();
});

test("diagramImage writes a standalone SVG at natural size, padded and laid on the background", async () => {
  const container = document.createElement("div");
  container.innerHTML = await renderMermaid(flowchart("Image", "End"), LIGHT);
  const svg = container.querySelector("svg")!;
  svg.setAttribute("style", "max-width: 120px; color: red");
  const { x, y, width, height } = svg.viewBox.baseVal;

  const text = new TextDecoder().decode(diagramImage(svg, "rgb(250, 249, 245)"));
  const image = new window.DOMParser().parseFromString(text, "image/svg+xml").documentElement;
  assert.equal(image.namespaceURI, "http://www.w3.org/2000/svg");
  assert.equal(image.getAttribute("viewBox"), `${x - 16} ${y - 16} ${width + 32} ${height + 32}`);
  assert.equal(image.getAttribute("width"), String(width + 32));
  assert.equal(image.getAttribute("height"), String(height + 32));
  assert.equal(image.getAttribute("style"), "color: red;");
  const backdrop = image.firstElementChild!;
  assert.equal(backdrop.localName, "rect");
  assert.deepEqual(
    ["x", "y", "width", "height", "fill"].map((name) => backdrop.getAttribute(name)),
    [String(x - 16), String(y - 16), String(width + 32), String(height + 32), "rgb(250, 249, 245)"],
  );
  assert.ok(text.includes("Image") && text.includes("<style>"));
  // foreignObject 中的标签为 XHTML 元素。The labels in foreignObject are XHTML elements.
  const label = image.querySelector("foreignObject div")!;
  assert.equal(label.namespaceURI, "http://www.w3.org/1999/xhtml");
  assert.equal(label.querySelector("p")!.textContent, "Image");
  // 页面中的图表不变。The diagram in the page is unchanged.
  assert.equal(svg.getAttribute("style"), "max-width: 120px; color: red");
  assert.equal(svg.querySelector(":scope > rect"), null);
});

test("only a rendered diagram has the preview button, which asks the host to preview it on the theme background", async () => {
  document.documentElement.style.setProperty("--background", "rgb(250, 249, 245)");
  const { diagram } = track();
  assert.throws(() => diagram.openPreview(), /has not rendered/);
  diagram.update(flowchart("Preview", "End"));
  assert.equal(diagram.dom.querySelector(".mermaid-diagram-open"), null);
  await until(() => diagram.state === "rendered", "the diagram");
  const button = diagram.dom.querySelector<HTMLButtonElement>(".mermaid-diagram-open")!;
  assert.equal(button.getAttribute("aria-label"), "Preview diagram");

  // 使用方对图表区域的处理收不到按钮上的按下与点击。The owner's handling of the diagram area gets neither the press
  // nor the click on the button.
  const received: string[] = [];
  diagram.dom.addEventListener("mousedown", () => received.push("mousedown"));
  diagram.dom.addEventListener("click", () => received.push("click"));
  const press = new window.MouseEvent("mousedown", { bubbles: true, cancelable: true, button: 0 });
  button.dispatchEvent(press);
  assert.equal(press.defaultPrevented, true);
  host.previewImages.length = 0;
  button.click();
  assert.deepEqual(received, []);
  await until(() => host.previewImages.length === 1, "the preview request");
  const [request] = host.previewImages;
  assert.equal(request!.type, "image/svg+xml");
  assert.equal(request!.name, PREVIEW_IMAGE_NAME);
  const expected = diagramImage(
    diagram.dom.querySelector<SVGSVGElement>(":scope > svg")!,
    "rgb(250, 249, 245)",
  );
  assert.equal(new TextDecoder().decode(request!.data), new TextDecoder().decode(expected));

  diagram.update("flowchart LR\n  a -->");
  await until(() => diagram.state === "failed", "the syntax error");
  assert.equal(diagram.dom.querySelector(".mermaid-diagram-open"), null);
  diagram.destroy();
});

test("a theme without --background cannot preview a diagram", async () => {
  document.documentElement.style.removeProperty("--background");
  const { diagram } = track();
  diagram.update(flowchart("Plain", "End"));
  await until(() => diagram.state === "rendered", "the diagram");
  assert.throws(() => diagram.openPreview(), /no --background/);
  diagram.destroy();
});
