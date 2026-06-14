import { MessageNode } from "../types";
import MdCard from "./md-card";

export default function TextContent({ node }: { node: MessageNode }) {
  return (
    <div className="msg-content">
      <MdCard mdText={node.content} />
    </div>
  );
}
