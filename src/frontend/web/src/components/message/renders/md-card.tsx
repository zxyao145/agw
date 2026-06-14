import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";

const MdCard = ({ mdText: content }: { mdText: string }) => (
  <ReactMarkdown
    remarkPlugins={[remarkGfm]}
    components={{
      pre: ({ children }) => <pre className="msg-content-md-code">{children}</pre>,
      code: ({ children }) => <code className="msg-content-md-code">{children}</code>,
      ol: ({ children }) => <ol className="msg-content-md-ol">{children}</ol>,
      ul: ({ children }) => <ul className="msg-content-md-ul">{children}</ul>,
    }}
  >
    {content}
  </ReactMarkdown>
);

export default MdCard;
