// 测试用：Mermaid 源码与渲染结果的辅助函数。For tests: helpers for Mermaid sources and render results.
import type { ThemeInfo } from "@agw/extension-sdk";

// mermaid 的 default 与 dark 主题中节点的背景色。The node backgrounds of mermaid's default and dark themes.
export const LIGHT_NODE = /#ECECFF/i;
export const DARK_NODE = /#1f2020/i;

export function darkTheme(light: ThemeInfo): ThemeInfo {
  return { ...light, id: "dark", title: "Dark", kind: "dark" };
}

/** 依次连接各个标签的 flowchart。A flowchart linking the labels in order. */
export function flowchart(...labels: string[]): string {
  return [
    "flowchart LR",
    ...labels
      .slice(1)
      .map((label, index) => `  n${index}["${labels[index]}"] --> n${index + 1}["${label}"]`),
  ].join("\n");
}

/** SVG 文字中的渲染序号（`mermaid-diagram-{n}`）。The render number in SVG text (`mermaid-diagram-{n}`). */
export function svgId(svg: string): number {
  return Number(/<svg id="mermaid-diagram-(\d+)"/.exec(svg)![1]);
}
