"use client";

import * as React from "react";
import { Info } from "lucide-react";
import { Input } from "@agw/components";
import { Label } from "@agw/components";
import { Tooltip, TooltipContent, TooltipTrigger } from "@agw/components";

interface GroupChatConfigProps {
  maximumIterationCount: number;
  onMaximumIterationCountChange: (value: number) => void;
}

/**
 * Configuration component for the Group Chat orchestration pattern.
 *
 * Group Chat pattern uses a round-robin manager where agents take turns
 * responding in a conversation. The max iterations limits how many
 * rounds of agent responses occur before termination.
 */
export function GroupChatConfig({
  maximumIterationCount,
  onMaximumIterationCountChange,
}: GroupChatConfigProps) {
  return (
    <div className="space-y-2">
      <Label>
        Max Iterations
        <Tooltip>
          <TooltipTrigger asChild>
            <Info size={16} className="text-muted-foreground ml-1 inline" />
          </TooltipTrigger>
          <TooltipContent side="top">
            <p>Maximum number of iterations for Group Chat pattern</p>
          </TooltipContent>
        </Tooltip>
      </Label>
      <Input
        type="number"
        min="1"
        max="100"
        value={maximumIterationCount}
        onChange={(e) => onMaximumIterationCountChange(Number(e.target.value))}
        className="w-[120px]"
      />
    </div>
  );
}
