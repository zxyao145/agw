"use client";

import React from "react";
import ReactMarkdown from "react-markdown";
import rehypeKatex from "rehype-katex";
import remarkGfm from "remark-gfm";
import remarkMath from "remark-math";
import { normalizeMathDelimiters } from "./math-markdown";
import { MarkdownCodeBlock, MarkdownImage, MarkdownLink, MarkdownTable } from "./markdown-elements";

type MdCardProps = {
  markdown: string;
  enableMath?: boolean;
};

type MarkdownCodeProps = {
  children?: React.ReactNode;
  className?: string;
};

const LANGUAGE_CLASS_PATTERN = /(?:^|\s)language-([^\s]+)/;
// 未加围栏的 diff 等文本会把缩进行解析成缩进代码块，只对围栏块渲染 header
const FENCED_CODE_BLOCK_PATTERN = /^ {0,3}(`{3,}|~{3,})/;

function getTextContent(node: React.ReactNode): string {
  return React.Children.toArray(node)
    .map((child) => {
      if (typeof child === "string" || typeof child === "number") {
        return String(child);
      }
      if (React.isValidElement<MarkdownCodeProps>(child)) {
        return getTextContent(child.props.children);
      }
      return "";
    })
    .join("");
}

function getCodeBlockDetails(children: React.ReactNode) {
  const codeElement = React.Children.toArray(children).find(
    (child): child is React.ReactElement<MarkdownCodeProps> =>
      React.isValidElement<MarkdownCodeProps>(child),
  );
  const language = LANGUAGE_CLASS_PATTERN.exec(codeElement?.props.className ?? "")?.[1] ?? "plain";
  const code = getTextContent(codeElement?.props.children ?? children).replace(/\n$/, "");

  return { code, language };
}

const MdCard = React.memo(function MdCard({ markdown, enableMath = true }: MdCardProps) {
  const normalizedText = React.useMemo(
    () => (enableMath ? normalizeMathDelimiters(markdown) : markdown),
    [enableMath, markdown],
  );
  return (
    <ReactMarkdown
      remarkPlugins={enableMath ? [remarkGfm, remarkMath] : [remarkGfm]}
      rehypePlugins={enableMath ? [rehypeKatex] : []}
      // ReactMarkdown  code-inspector-plugin 有冲突
      // 大概率是 code-inspector-plugin 在 dev 下改写了 JSX。
      children={normalizedText}
      components={{
        img: ({ src, alt, title }) => <MarkdownImage src={src} alt={alt} title={title} />,
        pre: ({ children, node }) => {
          const offset = node?.position?.start?.offset;
          const isFenced =
            offset == null ||
            FENCED_CODE_BLOCK_PATTERN.test(normalizedText.slice(offset, offset + 16));
          return isFenced ? (
            <MarkdownCodeBlock {...getCodeBlockDetails(children)}>{children}</MarkdownCodeBlock>
          ) : (
            <pre className="msg-content-md-code overflow-x-auto agw-scrollbar">{children}</pre>
          );
        },
        code: ({ children, className }) => (
          <code className={"msg-content-md-code" + (className ? " " + className : "")}>
            {children}
          </code>
        ),
        ol: ({ children }) => <ol className="msg-content-md-ol">{children}</ol>,
        ul: ({ children }) => <ul className="msg-content-md-ul">{children}</ul>,
        li: ({ children }) => <li className="msg-content-md-li">{children}</li>,
        p: ({ children }) => <p className="msg-content-md-p">{children}</p>,
        a: ({ children, href, title }) => (
          <MarkdownLink href={href} title={title}>
            {children}
          </MarkdownLink>
        ),
        table: ({ children }) => <MarkdownTable>{children}</MarkdownTable>,
      }}
    />
  );
});

export default MdCard;
