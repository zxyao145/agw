"use client";

import React from "react";
import MdCard from "./md-card";

export type MarkdownRendererProps = { markdown: string };

/** 渲染一段 Markdown 的组件。A component that renders one Markdown text. */
export type MarkdownRenderer = React.ComponentType<MarkdownRendererProps>;

// 默认使用 MdCard；Desktop 注入 Tiptap 实现。MdCard by default; Desktop injects the Tiptap renderer.
const MarkdownRendererContext = React.createContext<MarkdownRenderer>(MdCard);

export function MarkdownRendererProvider({
  renderer,
  children,
}: React.PropsWithChildren<{ renderer: MarkdownRenderer }>) {
  return (
    <MarkdownRendererContext.Provider value={renderer}>{children}</MarkdownRendererContext.Provider>
  );
}

export function MarkdownView({ markdown }: MarkdownRendererProps) {
  const Renderer = React.useContext(MarkdownRendererContext);
  return <Renderer markdown={markdown} />;
}
