import { Button } from "@agw/components";
import { ChevronDown, ChevronUp } from "lucide-react";
import MdCard from "./md-card";
import { useState } from "react";
import { MessageNode } from "../types";

export function getReasoningPreview(content: string): string {
  const firstLine = content.split(/\r?\n/, 1)[0] || "thinking";
  const maxLength = 72;

  if (firstLine.length <= maxLength) {
    return firstLine;
  }

  return `${firstLine.slice(0, maxLength - 1).trimEnd()}…`;
}

const Reasoning = ({ node }: { node: MessageNode }) => {
  const [expanded, setExpanded] = useState(false);
  const preview = getReasoningPreview(node.content);

  return (
    <div className="msg-content text-muted-foreground ">
      <div className="flex justify-between items-start gap-2">
        <div className="h-5 flex items-center">
          <Button
            type="button"
            variant="ghost"
            size="icon"
            className="w-fit bg-none hover:bg-transparent dark:hover:bg-transparent"
            aria-expanded={expanded}
            aria-label={expanded ? "Collapse reasoning" : "Expand reasoning"}
            onClick={() => setExpanded((current) => !current)}
          >
            {expanded ? <ChevronUp size={4} /> : <ChevronDown size={4} />}
          </Button>
        </div>

        <div className="flex flex-1 flex-col">
          <MdCard mdText={expanded ? node.content : preview} />
        </div>
      </div>
    </div>
  );
};

export default Reasoning;
