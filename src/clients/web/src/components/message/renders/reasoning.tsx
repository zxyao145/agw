import { Button } from "@/components/ui/button";
import { ChevronDown, ChevronUp } from "lucide-react";
import MdCard from "./md-card";
import { useMemo, useRef, useState } from "react";
import { layoutWithLines, prepareWithSegments } from "@chenglou/pretext";
import { MessageNode } from "../types";

const Reasoning = ({ node }: { node: MessageNode }) => {
  const [expanded, setExpanded] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);

  const preview = useMemo(() => {
    console.debug("preview containerRef", containerRef);
    if (!containerRef || !containerRef.current) {
      return "...";
    }
    {
      const lines = node.content.split("\n");
      const firstLine = lines[0];
      return firstLine;
    }
    const el = containerRef.current;
    const rect = el.getBoundingClientRect();
    const style = getComputedStyle(el);

    const paddingLeft = parseFloat(style.paddingLeft);
    const paddingRight = parseFloat(style.paddingRight);

    const contentWidth = rect.width - paddingLeft - paddingRight;

    const maxWidth = contentWidth;

    const lines = node.content.split("\n");
    const firstLine = lines[0];
    const prepared = prepareWithSegments(firstLine, "16px Arial");
    console.debug("prepared", maxWidth, firstLine, JSON.stringify(prepared));
    const result = layoutWithLines(prepared, maxWidth, 22);

    //  const prepared = prepare(lines[0], "16px Inter");
    //  const { height, lineCount } = layout(prepared, maxWidth, 20);
    // console.log("prepared height, lineCount", height, lineCount);
    console.log("prepared result", result.lines[0].text);
    return result.lines[0].text;
  }, [node.content, containerRef]);


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
            {expanded ? <ChevronDown size={4} /> : <ChevronUp size={4} />}
          </Button>
        </div>

        <div ref={containerRef} className="flex flex-1 flex-col">
          <MdCard mdText={expanded ? node.content : preview} />
        </div>
      </div>
    </div>
  );
};


export default Reasoning;