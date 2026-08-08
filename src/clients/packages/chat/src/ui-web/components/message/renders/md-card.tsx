import ReactMarkdown from "react-markdown";
import rehypeKatex from "rehype-katex";
import remarkGfm from "remark-gfm";
import remarkMath from "remark-math";
import { normalizeMathDelimiters } from "./math-markdown";

const MdCard = ({ mdText }: { mdText: string }) => {
  // console.log("MdCard mdText", typeof content, Array.isArray(content), content);
  // console.trace("MdCard stack");

  return (
    <ReactMarkdown
      remarkPlugins={[remarkGfm, remarkMath]}
      rehypePlugins={[rehypeKatex]}
      // ReactMarkdown  code-inspector-plugin 有冲突
      // 大概率是 code-inspector-plugin 在 dev 下改写了 JSX。
      children={normalizeMathDelimiters(mdText)}
      components={{
        pre: ({ children }) => (
          <pre className="msg-content-md-code overflow-x-auto agw-scrollbar">{children}</pre>
        ),
        code: ({ children }) => <code className="msg-content-md-code">{children}</code>,
        ol: ({ children }) => <ol className="msg-content-md-ol">{children}</ol>,
        ul: ({ children }) => <ul className="msg-content-md-ul">{children}</ul>,
        li: ({ children }) => <li className="msg-content-md-li">{children}</li>,
        p: ({ children }) => <p className="msg-content-md-p">{children}</p>,
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
};

export default MdCard;
