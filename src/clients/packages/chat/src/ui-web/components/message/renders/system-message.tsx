import { useMemo, useState } from "react";
import { MessageNode } from "../types";
import MdCard from "./md-card";
import { Button } from "@agw/components";
import { ChevronDown, ChevronUp } from "lucide-react";
import { formatContent } from "./parser";

const maxLength = 72;

export function getPreview(content: string): string {
  const firstLine = content.split(/\r?\n/, 1)[0] || "thinking";

  if (firstLine.length <= maxLength) {
    return firstLine;
  }

  return `${firstLine.slice(0, maxLength - 1).trimEnd()}…`;
}

export default function SystemMessage({ node }: { node: MessageNode }) {
  const [expanded, setExpanded] = useState(false);
  const renderContent = useMemo(() => {
    const renderContent = formatContent(node.content);
    // console.log("renderContent", renderContent);
    return renderContent;
  }, [node]);
  const preview = getPreview(renderContent);

  if (renderContent.length < maxLength) {
    return (
      <div className="msg-content text-muted-foreground ">
        <div className="flex justify-between items-start">
          <div className="flex flex-1 flex-col text-xs">
            <MdCard mdText={expanded ? renderContent : preview} />
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="msg-content text-muted-foreground ">
      <div className="flex justify-between items-start">
        <div>
          <Button
            type="button"
            variant="ghost"
            size="icon"
            className="w-[22] h-[22]"
            aria-expanded={expanded}
            aria-label={expanded ? "Collapse reasoning" : "Expand reasoning"}
            onClick={() => setExpanded((current) => !current)}
          >
            {expanded ? <ChevronUp size={4} /> : <ChevronDown size={4} />}
          </Button>
        </div>

        <div className="flex flex-1 flex-col">
          <MdCard mdText={expanded ? renderContent : preview} />
        </div>
      </div>
    </div>
  );
}
