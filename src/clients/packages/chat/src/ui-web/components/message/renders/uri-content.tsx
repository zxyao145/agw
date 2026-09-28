import { MessageNode } from "../types";
import { ConversationImage } from "../conversation-image";

export default function UriContent({ node }: { node: MessageNode }) {
  return (
    <div className="msg-content">
      <ConversationImage src={node.content} alt={node.name ?? "Image content"} name={node.name} />
    </div>
  );
}
