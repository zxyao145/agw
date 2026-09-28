import { MessageNode } from "../types";
import { ConversationImage } from "../conversation-image";

const supportedImageDataUrl = /^data:image\/(?:jpeg|png|gif|webp);base64,/i;

export default function DataContent({ node }: { node: MessageNode }) {
  if (!supportedImageDataUrl.test(node.content)) {
    return null;
  }

  return (
    <div className="inline-block max-w-full align-top">
      <ConversationImage
        src={node.content}
        alt={node.name ?? "Image attachment"}
        name={node.name}
      />
    </div>
  );
}
