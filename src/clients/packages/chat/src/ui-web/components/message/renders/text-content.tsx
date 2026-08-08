import { MessageNode } from "../types";
import MdCard from "./md-card";

export default function TextContent({ node }: { node: MessageNode }) {
  if (!node.content) {
    return null;
  }
  return (
    <div className="msg-content">
      <MdCard mdText={node.content} />
    </div>
  );
}
