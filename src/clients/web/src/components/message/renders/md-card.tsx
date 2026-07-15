import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";

const MdCard = ({ mdText: content }: { mdText: string }) => {
  // console.log("MdCard mdText", typeof content, Array.isArray(content), content);
  // console.trace("MdCard stack");
  return (
    <ReactMarkdown
      remarkPlugins={[remarkGfm]}
      // ReactMarkdown  code-inspector-plugin 有冲突
      // 大概率是 code-inspector-plugin 在 dev 下改写了 JSX。
      children={content}
      components={{
        pre: ({ children }) => <pre className="msg-content-md-code">{children}</pre>,
        code: ({ children }) => <code className="msg-content-md-code">{children}</code>,
        ol: ({ children }) => <ol className="msg-content-md-ol">{children}</ol>,
        ul: ({ children }) => <ul className="msg-content-md-ul">{children}</ul>,
        li: ({ children }) => <li className="msg-content-md-li">{children}</li>,
        p: ({ children }) => <p className="msg-content-md-p">{children}</p>,
      }}
    />
  );
};

export default MdCard;
