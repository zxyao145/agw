"use client";

import { normalizeMathDelimiters } from "@agw/chat-core";
import React from "react";
import type { MarkdownRendererProps } from "../components/message/renders/markdown-renderer";
import { FootnoteIdPrefixContext, renderMarkdownJson } from "./markdown-node-mapping";
import { normalizeMarkdownDocument } from "./normalize-document";
import { parseMarkdown } from "./parse-markdown";

/** 用 Tiptap 解析并渲染 Markdown 的 MarkdownRenderer。A MarkdownRenderer that parses and renders Markdown with Tiptap. */
export const TiptapMarkdown = React.memo(function TiptapMarkdown({
  markdown,
}: MarkdownRendererProps) {
  // 同一页面有多条消息，脚注 id 需要前缀。A page shows many messages, so footnote ids need a prefix.
  const footnoteIdPrefix = `${React.useId()}-`;
  const document = React.useMemo(
    () => normalizeMarkdownDocument(parseMarkdown(normalizeMathDelimiters(markdown))),
    [markdown],
  );
  return (
    <FootnoteIdPrefixContext.Provider value={footnoteIdPrefix}>
      {renderMarkdownJson({ content: document })}
    </FootnoteIdPrefixContext.Provider>
  );
});
