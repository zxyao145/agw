import { MessageNode } from "../types";

const supportedImageDataUrl = /^data:image\/(?:jpeg|png|gif|webp);base64,/i;

export default function DataContent({ node }: { node: MessageNode }) {
  if (!supportedImageDataUrl.test(node.content)) {
    return null;
  }

  return (
    <div className="mb-2 inline-block max-w-full align-top last:mb-0">
      <img
        src={node.content}
        alt={node.name ?? "Image attachment"}
        className="max-h-[300px] max-w-full rounded-lg border bg-muted object-contain shadow-xs"
        loading="lazy"
        decoding="async"
      />
    </div>
  );
}
