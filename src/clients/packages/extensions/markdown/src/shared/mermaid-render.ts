import type { ThemeInfo } from "@agw/extension-sdk";

// Mermaid 源码的渲染（Mermaid 设计第 1 节），图表与查看层共用。mermaid 在第一次渲染时才加载，使用 strict 安全级别，
// 源码有错误时直接抛出错误。
// Rendering Mermaid source (Mermaid design section 1), shared by diagrams and the viewer. mermaid loads on the first
// render, runs at the strict security level, and throws on source errors.

type Mermaid = (typeof import("mermaid"))["default"];

let mermaidModule: Promise<Mermaid> | null = null;
/** 上一次 `initialize` 使用的 mermaid 主题。The mermaid theme of the last `initialize`. */
let initializedTheme: string | null = null;
let renderCount = 0;

/** 代码块的语言是否为 Mermaid（不区分大小写）。Whether a code block's language is Mermaid (case-insensitive). */
export function isMermaidLanguage(language: string | null | undefined): boolean {
  return language?.toLowerCase() === "mermaid";
}

/**
 * 按宿主主题渲染 Mermaid 源码，返回 SVG 文字。SVG 内的样式以 id 为选择器，每次渲染使用新的 id。
 * Renders Mermaid source for the host theme and returns the SVG text. The SVG's styles select by id, so every render
 * takes a new id.
 */
export async function renderMermaid(source: string, theme: ThemeInfo): Promise<string> {
  mermaidModule ??= import("mermaid").then((module) => module.default);
  const mermaid = await mermaidModule;
  const name = theme.kind === "dark" ? "dark" : "default";
  if (initializedTheme !== name) {
    // suppressErrorRendering：出错时不在 body 中留下错误图表，直接抛出。On errors, throw without leaving an error
    // diagram in the body.
    mermaid.initialize({
      startOnLoad: false,
      securityLevel: "strict",
      suppressErrorRendering: true,
      theme: name,
    });
    initializedTheme = name;
  }
  renderCount += 1;
  const { svg } = await mermaid.render(`mermaid-diagram-${renderCount}`, source);
  return svg;
}
