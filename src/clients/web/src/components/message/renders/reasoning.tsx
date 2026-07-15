import { Button } from "@/components/ui/button";
import { ChevronDown, ChevronUp } from "lucide-react";
import MdCard from "./md-card";
import { useState } from "react";
import { MessageNode } from "../types";

const Reasoning = ({ node }: { node: MessageNode }) => {
  const [expanded, setExpanded] = useState(false);
  const preview = node.content.split("\n")[0] || "thinking";

  return (
    <div className="msg-content text-muted-foreground ">
      <div className="flex justify-between items-start">
        <div>
          <Button
            variant="ghost"
            size="icon"
            className="w-[22] h-[22]"
            onClick={() => setExpanded(!expanded)}
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
