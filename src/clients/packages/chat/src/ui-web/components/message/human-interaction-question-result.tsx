"use client";

import * as React from "react";
import { ChevronDown, ChevronUp, CircleHelp } from "lucide-react";

import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@agw/components";
import type { HumanInteractionQuestionResult } from "../../../services/human-interaction";

type HumanInteractionQuestionResultProps = {
  result: HumanInteractionQuestionResult;
};

export function HumanInteractionQuestionResultView({
  result,
}: HumanInteractionQuestionResultProps) {
  const questionCount = result.items.length;
  const [expanded, setExpanded] = React.useState(true);

  return (
    <Collapsible open={expanded} onOpenChange={setExpanded} className="max-w-full">
      <CollapsibleTrigger asChild>
        <button
          type="button"
          className="cursor-pointer flex items-center gap-2 rounded-md py-1 text-left text-[15px] font-normal text-muted-foreground transition-colors hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/50"
        >
          {expanded ? (
            <ChevronUp className="size-4 shrink-0" />
          ) : (
            <ChevronDown className="size-4 shrink-0" />
          )}
          <span className="text-sm">
            Asked {questionCount} {questionCount === 1 ? "question" : "questions"}
          </span>
          <CircleHelp className="size-3.5 shrink-0" strokeWidth={1.75} />
        </button>
      </CollapsibleTrigger>
      <CollapsibleContent className="overflow-hidden">
        <div className="space-y-4 pt-3 pl-6">
          {result.items.map((item) => (
            <div key={item.question} className="min-w-0">
              <p className="text-[15px] leading-relaxed text-foreground/75">{item.question}</p>
              <p className="mt-1 text-[15px] leading-relaxed text-muted-foreground/65">
                {result.cancelled ? "No answer — request cancelled" : item.answer}
              </p>
            </div>
          ))}
        </div>
      </CollapsibleContent>
    </Collapsible>
  );
}
