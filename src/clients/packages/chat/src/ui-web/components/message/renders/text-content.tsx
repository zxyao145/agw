import { MessageNode } from "../types";
import { MarkdownView } from "./markdown-renderer";

export default function TextContent({ node }: { node: MessageNode }) {
  if (!node.content) {
    return null;
  }
  return (
    <div className="msg-content">
      <MarkdownView markdown={node.content} />
    </div>
  );
}
