import assert from "node:assert/strict";
import { after, beforeEach, test } from "node:test";

import { createExtensionError, type PreviewImage, type TextDocument } from "@agw/extension-sdk";
import { JSDOM } from "jsdom";
import { act, createElement } from "react";

import { connectTestHost } from "../test-host";
import { LayoutFreeResizeObserver, outlineItems } from "../test-layout";
import { installMermaidDom } from "../test-mermaid-dom";

// React 在 jsdom 的 document 中渲染，Markdown 管线使用 window.DOMParser。
// React renders into jsdom's document, and the Markdown pipeline uses window.DOMParser.
const { window } = new JSDOM("<!doctype html><html><body></body></html>", {
  pretendToBeVisual: true,
});
Object.assign(globalThis, {
  window,
  document: window.document,
  DOMParser: window.DOMParser,
  MutationObserver: window.MutationObserver,
  ResizeObserver: LayoutFreeResizeObserver,
  getComputedStyle: window.getComputedStyle.bind(window),
  requestAnimationFrame: window.requestAnimationFrame.bind(window),
  cancelAnimationFrame: window.cancelAnimationFrame.bind(window),
  IS_REACT_ACT_ENVIRONMENT: true,
});
installMermaidDom(window);

const { createRoot } = await import("react-dom/client");
const { MarkdownDocument } = await import("./markdown-document");
const { createFindBarMemory } = await import("../shared/find-bar");

const ROOT = "agw-cloud://local/project-1/workspace/";
const ENCODINGS = [
  { id: "utf-8", label: "Unicode (UTF-8)" },
  { id: "windows-1252", label: "Western European (Windows 1252)" },
];

/** 宿主端的文件：文字，或读取时抛出的错误码；`windows-1252` 时可以读取的文字。Host-side files. */
type HostFile = { text: string } | { code: string; windows1252?: string };

const files = new Map<string, HostFile>();
const reads: { uri: string; encoding: string | null }[] = [];
// 设置后，下一次读取等它完成再回复。When set, the next read waits for it before replying.
let gate: Promise<void> | null = null;

function textDocument(text: string): TextDocument {
  return { text, encoding: "utf-8", bom: false, eol: "\n", sha256: "0".repeat(64) };
}

/** 每次 `ui.previewImage` 的图片；宿主收到后立即作为已关闭回答。Every `ui.previewImage` image, answered as closed. */
const previewImages: PreviewImage[] = [];

const host = connectTestHost(async (method, params) => {
  if (method === "fs.listTextEncodings") return ENCODINGS;
  if (method === "ui.previewImage") {
    previewImages.push(params as PreviewImage);
    return undefined;
  }
  assert.equal(method, "fs.readTextDocument");
  const { uri, encoding } = params as { uri: string; encoding?: string };
  reads.push({ uri, encoding: encoding ?? null });
  const waiting = gate;
  gate = null;
  if (waiting) await waiting;
  const file = files.get(uri);
  if (!file) throw createExtensionError("ENOENT", `${uri} does not exist.`);
  if ("text" in file) return textDocument(file.text);
  if (encoding === "windows-1252" && file.windows1252 !== undefined) {
    return textDocument(file.windows1252);
  }
  throw createExtensionError(file.code, `Cannot read ${uri}.`);
});
after(() => host.close());
beforeEach(() => {
  reads.length = 0;
  gate = null;
});

function viewerFile(name: string) {
  return { uri: `${ROOT}${name}`, name, projectId: "project-1" };
}

type Viewer = {
  container: HTMLElement;
  /** 像 `main.tsx` 一样显示文件：URI 作为 key，`shown` 每次加 1。Shows a file as `main.tsx` does. */
  show(name: string): Promise<void>;
  unmount(): Promise<void>;
};

function mountViewer(): Viewer {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  let shown = 0;
  const findMemory = createFindBarMemory();
  return {
    container,
    async show(name) {
      shown += 1;
      const element = createElement(MarkdownDocument, {
        key: name,
        api: host.api,
        file: viewerFile(name),
        shown,
        findMemory,
      });
      await act(async () => root.render(element));
    },
    async unmount() {
      await act(async () => root.unmount());
      container.remove();
    },
  };
}

async function until(viewer: Viewer, condition: () => boolean, what: string): Promise<void> {
  for (let attempt = 0; attempt < 200; attempt += 1) {
    if (condition()) return;
    await act(() => new Promise((resolve) => setTimeout(resolve, 10)));
  }
  assert.fail(`Timed out waiting for ${what}; the viewer shows "${viewer.container.textContent}".`);
}

async function click(viewer: Viewer, text: string): Promise<void> {
  const buttons = [...viewer.container.querySelectorAll("button")];
  const button = buttons.find((candidate) => candidate.textContent === text);
  assert.ok(button, `No button "${text}" among: ${buttons.map((b) => b.textContent).join(", ")}`);
  await act(async () => button.click());
}

function heading(viewer: Viewer): string | null {
  return viewer.container.querySelector("article.markdown-body h1")?.textContent ?? null;
}

function paragraph(viewer: Viewer): string | null {
  return viewer.container.querySelector("article.markdown-body p")?.textContent ?? null;
}

test("a document with level 1-4 headings shows the outline beside the article", async () => {
  files.set(`${ROOT}outline.md`, {
    text: "## Install\n\n### Requirements\n\n#### Versions\n\n##### Five\n\n## Usage\n",
  });
  const viewer = mountViewer();
  try {
    await viewer.show("outline.md");
    await until(viewer, () => outlineItems(viewer.container) !== null, "the outline");
    assert.deepEqual(outlineItems(viewer.container), [
      ["Install", "0"],
      ["Requirements", "1"],
      ["Versions", "2"],
      ["Usage", "0"],
    ]);
    const layout = viewer.container.querySelector(".preview-layout")!;
    assert.equal(layout.hasAttribute("data-outline"), true);
    assert.deepEqual(
      [...layout.children].map((child) => child.className),
      ["find-bar-anchor", "markdown-body agw-prose document-column", "outline"],
    );
    const nav = layout.querySelector("nav.outline")!;
    assert.equal(nav.getAttribute("aria-label"), "Outline");
    assert.equal(nav.querySelector(".outline-title")?.textContent, "Outline");
    for (const item of nav.querySelectorAll(".outline-item")) {
      assert.equal(item.getAttribute("type"), "button");
      assert.equal(item.getAttribute("title"), item.textContent);
    }
  } finally {
    await viewer.unmount();
  }
});

test("an outline can show a heading whose GitHub slug is empty", async () => {
  files.set(`${ROOT}punctuation.md`, { text: "## !!!\n" });
  const viewer = mountViewer();
  try {
    await viewer.show("punctuation.md");
    await until(viewer, () => outlineItems(viewer.container) !== null, "the punctuation outline");
    assert.equal(viewer.container.querySelector(".markdown-body h2")?.id, "");
    assert.deepEqual(outlineItems(viewer.container), [["!!!", "0"]]);
  } finally {
    await viewer.unmount();
  }
});

test("a document without level 1-4 headings shows no outline", async () => {
  files.set(`${ROOT}flat.md`, { text: "##### Five\n\nText.\n" });
  const viewer = mountViewer();
  try {
    await viewer.show("flat.md");
    await until(viewer, () => paragraph(viewer) === "Text.", "the content");
    assert.equal(outlineItems(viewer.container), null);
    assert.equal(
      viewer.container.querySelector(".preview-layout")?.hasAttribute("data-outline"),
      false,
    );
  } finally {
    await viewer.unmount();
  }
});

test("loading and failed reads show no outline", async () => {
  files.set(`${ROOT}slow.md`, { text: "# Slow\n" });
  files.set(`${ROOT}failed.md`, { code: "ERR_NOT_TEXT" });
  const viewer = mountViewer();
  try {
    let release!: () => void;
    gate = new Promise((resolve) => (release = resolve));
    await viewer.show("slow.md");
    await until(viewer, () => reads.length === 1, "the read");
    assert.match(viewer.container.textContent ?? "", /Loading/u);
    assert.equal(outlineItems(viewer.container), null);
    release();
    await until(
      viewer,
      () => outlineItems(viewer.container) !== null,
      "the outline after the read",
    );
    await viewer.show("failed.md");
    await until(
      viewer,
      () => viewer.container.querySelector(".document-error") !== null,
      "the error panel",
    );
    assert.equal(outlineItems(viewer.container), null);
  } finally {
    await viewer.unmount();
  }
});

test("showing a changed file again updates the outline", async () => {
  files.set(`${ROOT}changing.md`, { text: "# First\n" });
  const viewer = mountViewer();
  try {
    await viewer.show("changing.md");
    await until(viewer, () => outlineItems(viewer.container)?.length === 1, "the first outline");
    files.set(`${ROOT}changing.md`, { text: "# First\n\n## Second\n" });
    await viewer.show("changing.md");
    await until(viewer, () => outlineItems(viewer.container)?.length === 2, "the updated outline");
    assert.deepEqual(outlineItems(viewer.container), [
      ["First", "0"],
      ["Second", "1"],
    ]);
  } finally {
    await viewer.unmount();
  }
});

test("the Markdown article uses the shared Markdown typography in document density", async () => {
  files.set(`${ROOT}typography.md`, { text: "# Title" });
  const viewer = mountViewer();
  try {
    await viewer.show("typography.md");
    await until(viewer, () => heading(viewer) === "Title", "the content");
    const article = viewer.container.querySelector("article.markdown-body")!;
    assert.ok(article.classList.contains("agw-prose"));
    assert.equal(article.getAttribute("data-density"), "document");
  } finally {
    await viewer.unmount();
  }
});

test("every show reads the file again, same file included, and keeps the content until the read ends", async () => {
  files.set(`${ROOT}notes.md`, { text: "# First" });
  files.set(`${ROOT}other.md`, { text: "# Other" });
  const viewer = mountViewer();
  try {
    await viewer.show("notes.md");
    await until(viewer, () => heading(viewer) === "First", "the first content");

    files.set(`${ROOT}notes.md`, { text: "# Second" });
    let release!: () => void;
    gate = new Promise((resolve) => (release = resolve));
    await viewer.show("notes.md");
    await until(viewer, () => reads.length === 2, "the second read");
    assert.equal(heading(viewer), "First");
    assert.doesNotMatch(viewer.container.textContent ?? "", /Loading/u);
    release();
    await until(viewer, () => heading(viewer) === "Second", "the content saved by another viewer");

    await viewer.show("other.md");
    await until(viewer, () => heading(viewer) === "Other", "the other file");
    await viewer.show("notes.md");
    await until(viewer, () => reads.length === 4, "the read after switching back");
    await until(viewer, () => heading(viewer) === "Second", "the file after switching back");
    assert.deepEqual(reads, [
      { uri: `${ROOT}notes.md`, encoding: null },
      { uri: `${ROOT}notes.md`, encoding: null },
      { uri: `${ROOT}other.md`, encoding: null },
      { uri: `${ROOT}notes.md`, encoding: null },
    ]);
  } finally {
    await viewer.unmount();
  }
});

test("a binary file shows the shared panel without Reopen with Encoding…", async () => {
  files.set(`${ROOT}data.md`, { code: "ERR_NOT_TEXT" });
  const viewer = mountViewer();
  try {
    await viewer.show("data.md");
    await until(
      viewer,
      () => viewer.container.querySelector(".document-error") !== null,
      "the error panel",
    );
    assert.equal(
      viewer.container.querySelector('.document-error [role="alert"]')?.textContent,
      "Could not read data.mdThis file is binary, so it can't be edited as text.",
    );
    assert.equal(viewer.container.querySelectorAll("button").length, 0);
  } finally {
    await viewer.unmount();
  }
});

test("Reopen with Encoding… reads with the chosen encoding, and later shows of that file keep it", async () => {
  files.set(`${ROOT}legacy.md`, { code: "ERR_UNKNOWN_ENCODING", windows1252: "café menu" });
  files.set(`${ROOT}other.md`, { text: "# Other" });
  const viewer = mountViewer();
  try {
    await viewer.show("legacy.md");
    await until(
      viewer,
      () => viewer.container.querySelector(".document-error") !== null,
      "the error panel",
    );
    assert.equal(
      viewer.container.querySelector('.document-error [role="alert"]')?.textContent,
      "Could not read legacy.mdThe encoding of this file couldn't be detected.",
    );

    await click(viewer, "Reopen with Encoding…");
    await until(
      viewer,
      () => viewer.container.querySelector("[data-encoding]") !== null,
      "the encoding list",
    );
    await click(viewer, "Western European (Windows 1252)");
    await until(viewer, () => paragraph(viewer) === "café menu", "the reopened content");

    await viewer.show("legacy.md");
    await until(viewer, () => reads.length === 3, "the read on the next show");
    await until(viewer, () => paragraph(viewer) === "café menu", "the content on the next show");

    // 另一个文件重新挂载，回到这个文件时重新识别编码。Another file remounts, so coming back detects again.
    await viewer.show("other.md");
    await until(viewer, () => heading(viewer) === "Other", "the other file");
    await viewer.show("legacy.md");
    await until(
      viewer,
      () => viewer.container.querySelector(".document-error") !== null,
      "the error panel after switching back",
    );
    assert.deepEqual(reads, [
      { uri: `${ROOT}legacy.md`, encoding: null },
      { uri: `${ROOT}legacy.md`, encoding: "windows-1252" },
      { uri: `${ROOT}legacy.md`, encoding: "windows-1252" },
      { uri: `${ROOT}other.md`, encoding: null },
      { uri: `${ROOT}legacy.md`, encoding: null },
    ]);
  } finally {
    await viewer.unmount();
  }
});

test("choosing an encoding that still fails shows the panel again, and choosing it again reads again", async () => {
  files.set(`${ROOT}unknown.md`, { code: "ERR_UNKNOWN_ENCODING" });
  const viewer = mountViewer();
  try {
    await viewer.show("unknown.md");
    for (const attempt of [1, 2]) {
      await until(
        viewer,
        () => viewer.container.querySelector(".document-error") !== null,
        `the error panel before attempt ${attempt}`,
      );
      await click(viewer, "Reopen with Encoding…");
      await until(
        viewer,
        () => viewer.container.querySelector("[data-encoding]") !== null,
        "the encoding list",
      );
      await click(viewer, "Unicode (UTF-8)");
      await until(viewer, () => reads.length === attempt + 1, `read ${attempt + 1}`);
    }
    await until(
      viewer,
      () => viewer.container.querySelector(".document-error") !== null,
      "the error panel after the last attempt",
    );
    assert.deepEqual(reads, [
      { uri: `${ROOT}unknown.md`, encoding: null },
      { uri: `${ROOT}unknown.md`, encoding: "utf-8" },
      { uri: `${ROOT}unknown.md`, encoding: "utf-8" },
    ]);
  } finally {
    await viewer.unmount();
  }
});

/** Mermaid 代码块与它的图表 SVG 文字。The Mermaid block and its diagram's SVG text. */
function mermaidBlock(viewer: Viewer): { block: HTMLElement | null; svg: string } {
  const block = viewer.container.querySelector<HTMLElement>("article.markdown-body .mermaid-block");
  return { block, svg: block?.querySelector(".mermaid-diagram > svg")?.outerHTML ?? "" };
}

test("clicking a rendered Mermaid diagram asks the host to preview it as an SVG image", async () => {
  document.documentElement.style.setProperty("--background", "rgb(255, 255, 255)");
  files.set(`${ROOT}preview.md`, {
    text: '```mermaid\nflowchart LR\n  a["Previewed"] --> b["End"]\n```\n',
  });
  previewImages.length = 0;
  const viewer = mountViewer();
  try {
    await viewer.show("preview.md");
    await until(viewer, () => mermaidBlock(viewer).svg.includes("Previewed"), "the diagram");
    await act(async () => {
      (
        mermaidBlock(viewer).block!.querySelector(".mermaid-diagram > svg") as SVGElement
      ).dispatchEvent(new window.MouseEvent("click", { bubbles: true }));
    });
    await until(viewer, () => previewImages.length === 1, "the preview request");
    const [image] = previewImages;
    assert.equal(image!.type, "image/svg+xml");
    assert.equal(image!.name, "mermaid-diagram.svg");
    const svg = new window.DOMParser().parseFromString(
      new TextDecoder().decode(image!.data),
      "image/svg+xml",
    ).documentElement;
    assert.equal(svg.localName, "svg");
    assert.equal(svg.firstElementChild!.getAttribute("fill"), "rgb(255, 255, 255)");
    assert.ok(svg.textContent!.includes("Previewed"));
  } finally {
    await viewer.unmount();
  }
});

test("a Mermaid code block shows its diagram, and a changed file keeps the diagram until the new one renders", async () => {
  const diagram = (label: string) =>
    `# Diagram\n\n\`\`\`mermaid\nflowchart LR\n  a["${label}"] --> b["End"]\n\`\`\`\n`;
  files.set(`${ROOT}diagram.md`, { text: diagram("Start") });
  const viewer = mountViewer();
  try {
    await viewer.show("diagram.md");
    await until(viewer, () => mermaidBlock(viewer).svg.includes("Start"), "the diagram");
    const { block } = mermaidBlock(viewer);
    assert.equal(block!.dataset.diagram, "rendered");
    // 源码仍在，由样式隐藏。The source stays and the styles hide it.
    assert.equal(
      block!.querySelector("pre > code.language-mermaid")?.textContent?.startsWith("flowchart LR"),
      true,
    );
    const element = block!.querySelector(".mermaid-diagram");

    files.set(`${ROOT}diagram.md`, { text: diagram("Changed") });
    await viewer.show("diagram.md");
    await until(viewer, () => mermaidBlock(viewer).svg.includes("Changed"), "the changed diagram");
    assert.equal(mermaidBlock(viewer).block!.querySelector(".mermaid-diagram"), element);
  } finally {
    await viewer.unmount();
  }
});

test("a Mermaid syntax error shows the error below the source", async () => {
  files.set(`${ROOT}broken.md`, { text: "```mermaid\nflowchart LR\n  a -->\n```\n" });
  const viewer = mountViewer();
  try {
    await viewer.show("broken.md");
    await until(
      viewer,
      () => mermaidBlock(viewer).block?.dataset.diagram === "failed",
      "the error",
    );
    const { block, svg } = mermaidBlock(viewer);
    assert.equal(svg, "");
    assert.match(block!.querySelector(".mermaid-diagram-error")!.textContent!, /Parse error/);
    assert.ok(block!.querySelector("pre > code.language-mermaid"));
  } finally {
    await viewer.unmount();
  }
});

/** 测试宿主的平台为 darwin，Mod 为 Meta。The test host's platform is darwin, so Mod is Meta. */
async function pressShortcut(init: KeyboardEventInit): Promise<void> {
  const event = new window.KeyboardEvent("keydown", { bubbles: true, cancelable: true, ...init });
  await act(async () => document.body.dispatchEvent(event));
}

test("Preview's find bar has no replace row and stays open across a file switch", async () => {
  files.set(`${ROOT}find-one.md`, { text: "# One\n\nText.\n" });
  files.set(`${ROOT}find-two.md`, { text: "# Two\n\nMore text.\n" });
  const viewer = mountViewer();
  try {
    await viewer.show("find-one.md");
    await until(viewer, () => heading(viewer) === "One", "the first file");
    // 替换快捷键在 Preview 中只打开搜索栏。The replace shortcut only opens the find bar in Preview.
    await pressShortcut({ key: "ƒ", code: "KeyF", metaKey: true, altKey: true });
    const bar = () => viewer.container.querySelector(".find-bar");
    assert.ok(bar());
    assert.equal(bar()!.querySelector('button[aria-label="Toggle Replace"]'), null);
    assert.equal(bar()!.querySelector('input[aria-label="Replace"]'), null);
    assert.equal(document.activeElement, bar()!.querySelector('input[aria-label="Find"]'));

    await viewer.show("find-two.md");
    await until(viewer, () => heading(viewer) === "Two", "the second file");
    assert.ok(bar(), "the find bar stays open after the file switch");
    await act(async () =>
      bar()!
        .querySelector("input")!
        .dispatchEvent(new window.KeyboardEvent("keydown", { key: "Escape", bubbles: true })),
    );
    assert.equal(bar(), null);
  } finally {
    await viewer.unmount();
  }
});
