import { MessageNode } from "../types";
import Image from "next/image";

export default function UriContent({ node }: { node: MessageNode }) {
  return (
    <div className="msg-content">
      <Image src={node.content} alt="Image content" />
    </div>
  );
}
