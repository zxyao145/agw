import { ExternalLink, FileText } from "lucide-react";
import React from "react";
import ReactMarkdown from "react-markdown";
import rehypeKatex from "rehype-katex";
import remarkGfm from "remark-gfm";
import remarkMath from "remark-math";
import { normalizeMathDelimiters } from "./math-markdown";

type MdCardProps = {
  mdText: string;
  enableMath?: boolean;
};

function isHttpLink(href: string | undefined): href is string {
  if (!href) return false;
  try {
    const url = new URL(href);
    return url.protocol === "http:" || url.protocol === "https:";
  } catch {
    return false;
  }
}

const MdCard = React.memo(function MdCard({ mdText, enableMath = true }: MdCardProps) {
  const normalizedText = React.useMemo(
    () => (enableMath ? normalizeMathDelimiters(mdText) : mdText),
    [enableMath, mdText],
  );
  return (
    <ReactMarkdown
      remarkPlugins={enableMath ? [remarkGfm, remarkMath] : [remarkGfm]}
      rehypePlugins={enableMath ? [rehypeKatex] : []}
      // ReactMarkdown  code-inspector-plugin 有冲突
      // 大概率是 code-inspector-plugin 在 dev 下改写了 JSX。
      children={normalizedText}
      components={{
        pre: ({ children }) => (
          <pre className="msg-content-md-code overflow-x-auto agw-scrollbar">{children}</pre>
        ),
        code: ({ children }) => <code className="msg-content-md-code">{children}</code>,
        ol: ({ children }) => <ol className="msg-content-md-ol">{children}</ol>,
        ul: ({ children }) => <ul className="msg-content-md-ul">{children}</ul>,
        li: ({ children }) => <li className="msg-content-md-li">{children}</li>,
        p: ({ children }) => <p className="msg-content-md-p">{children}</p>,
        a: ({ children, href, title }) =>
          isHttpLink(href) ? (
            <a
              href={href}
              title={title}
              target="_blank"
              rel="noreferrer"
              className="text-[#2e82d2] underline-offset-2 hover:underline focus-visible:underline dark:text-[#74b8f7]"
            >
              {children}
              <ExternalLink
                aria-hidden="true"
                className="ml-1 inline-block size-[0.85em] align-[-0.06em]"
              />
            </a>
          ) : (
            <span title={title ?? href} className="text-[#2e82d2] dark:text-[#74b8f7]">
              <FileText
                aria-hidden="true"
                className="mr-1 inline-block size-[0.9em] align-[-0.08em]"
              />
              {children}
            </span>
          ),
        table: ({ children }) => (
          <div
            className="msg-content-md-table-wrap agw-scrollbar"
            role="region"
            aria-label="Scrollable table"
            tabIndex={0}
          >
            <table className="msg-content-md-table">{children}</table>
          </div>
        ),
      }}
    />
  );
});

export default MdCard;
