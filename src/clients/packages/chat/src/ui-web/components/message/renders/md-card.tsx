"use client";

import { Button } from "@agw/components";
import { Check, Copy, ExternalLink, FileText, WrapText } from "lucide-react";
import React from "react";
import ReactMarkdown from "react-markdown";
import rehypeKatex from "rehype-katex";
import remarkGfm from "remark-gfm";
import remarkMath from "remark-math";
import { toast } from "sonner";
import { normalizeMathDelimiters } from "./math-markdown";

type MdCardProps = {
  mdText: string;
  enableMath?: boolean;
};

type MarkdownCodeProps = {
  children?: React.ReactNode;
  className?: string;
};

const COPY_STATE_DURATION_MS = 2_000;
const LANGUAGE_CLASS_PATTERN = /(?:^|\s)language-([^\s]+)/;

function isHttpLink(href: string | undefined): href is string {
  if (!href) return false;
  try {
    const url = new URL(href);
    return url.protocol === "http:" || url.protocol === "https:";
  } catch {
    return false;
  }
}

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

function MarkdownCodeBlock({ children }: { children: React.ReactNode }) {
  const resetTimerRef = React.useRef<ReturnType<typeof setTimeout> | null>(null);
  const [isWrapped, setIsWrapped] = React.useState(true);
  const [copied, setCopied] = React.useState(false);
  const { code, language } = getCodeBlockDetails(children);
  const wrapLabel = isWrapped ? "Disable word wrap" : "Enable word wrap";
  const copyLabel = copied ? "Code copied" : "Copy code";

  React.useEffect(
    () => () => {
      if (resetTimerRef.current) {
        clearTimeout(resetTimerRef.current);
      }
    },
    [],
  );

  const handleCopy = async () => {
    try {
      await navigator.clipboard.writeText(code);
      setCopied(true);
      if (resetTimerRef.current) {
        clearTimeout(resetTimerRef.current);
      }
      resetTimerRef.current = setTimeout(() => setCopied(false), COPY_STATE_DURATION_MS);
    } catch {
      toast.error("Unable to copy code");
    }
  };

  return (
    <div className="msg-content-md-code-block">
      <div className="msg-content-md-code-header">
        <span className="min-w-0 truncate font-medium">{language}</span>
        <div className="flex shrink-0 items-center gap-0.5">
          <Button
            type="button"
            variant="ghost"
            size="icon-sm"
            className={
              "size-8 rounded-lg text-[#71757a] hover:bg-black/5 hover:text-[#17191d] dark:text-[#aeb2b8] dark:hover:bg-white/10 dark:hover:text-white" +
              (isWrapped ? " bg-black/5 text-[#17191d] dark:bg-white/10 dark:text-white" : "")
            }
            aria-label={wrapLabel}
            aria-pressed={isWrapped}
            title={wrapLabel}
            onClick={() => setIsWrapped((current) => !current)}
          >
            <WrapText className="size-4" aria-hidden="true" />
          </Button>
          <Button
            type="button"
            variant="ghost"
            size="icon-sm"
            className="size-8 rounded-lg text-[#71757a] hover:bg-black/5 hover:text-[#17191d] dark:text-[#aeb2b8] dark:hover:bg-white/10 dark:hover:text-white"
            aria-label={copyLabel}
            title={copyLabel}
            disabled={!code}
            onClick={handleCopy}
          >
            {copied ? (
              <Check className="size-4" aria-hidden="true" />
            ) : (
              <Copy className="size-4" aria-hidden="true" />
            )}
          </Button>
        </div>
      </div>
      <pre className="msg-content-md-code-block-body agw-scrollbar" data-wrap={isWrapped}>
        {children}
      </pre>
    </div>
  );
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
        pre: ({ children }) => <MarkdownCodeBlock>{children}</MarkdownCodeBlock>,
        code: ({ children, className }) => (
          <code className={"msg-content-md-code" + (className ? " " + className : "")}>
            {children}
          </code>
        ),
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
