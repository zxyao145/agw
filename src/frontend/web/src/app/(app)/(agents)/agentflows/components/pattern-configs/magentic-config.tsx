"use client";

import * as React from "react";
import { Info } from "lucide-react";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";

interface MagenticConfigProps {
  maxRounds: number;
  maxStallCount: number;
  maxResetCount: number;
  onMaxRoundsChange: (value: number) => void;
  onMaxStallCountChange: (value: number) => void;
  onMaxResetCountChange: (value: number) => void;
}

/**
 * Configuration component for the Magentic-One orchestration pattern.
 *
 * Magentic-One is a hierarchical multi-agent pattern where:
 * - The first agent acts as the orchestrator (coordinator)
 * - Remaining agents are workers that execute assigned tasks
 * - The orchestrator manages task distribution and monitors progress
 *
 * Parameters:
 * - maxRounds: Maximum collaboration rounds before forced termination
 * - maxStallCount: Consecutive rounds without progress before orchestrator intervention
 * - maxResetCount: Maximum plan resets allowed before termination
 */
export function MagenticConfig({
  maxRounds,
  maxStallCount,
  maxResetCount,
  onMaxRoundsChange,
  onMaxStallCountChange,
  onMaxResetCountChange,
}: MagenticConfigProps) {
  return (
    <>
      <div className="space-y-2">
        <Label>
          Max Rounds
          <Tooltip>
            <TooltipTrigger asChild>
              <Info size={16} className="text-muted-foreground ml-1 inline" />
            </TooltipTrigger>
            <TooltipContent side="top">
              <p>Maximum collaboration rounds before termination</p>
            </TooltipContent>
          </Tooltip>
        </Label>
        <Input
          type="number"
          min="1"
          max="100"
          value={maxRounds}
          onChange={(e) => onMaxRoundsChange(Number(e.target.value))}
          className="w-[120px]"
        />
      </div>
      <div className="space-y-2">
        <Label>
          Stall Count
          <Tooltip>
            <TooltipTrigger asChild>
              <Info size={16} className="text-muted-foreground ml-1 inline" />
            </TooltipTrigger>
            <TooltipContent side="top">
              <p>Rounds without progress before orchestrator intervention</p>
            </TooltipContent>
          </Tooltip>
        </Label>
        <Input
          type="number"
          min="1"
          max="10"
          value={maxStallCount}
          onChange={(e) => onMaxStallCountChange(Number(e.target.value))}
          className="w-[100px]"
        />
      </div>
      <div className="space-y-2">
        <Label>
          Max Resets
          <Tooltip>
            <TooltipTrigger asChild>
              <Info size={16} className="text-muted-foreground ml-1 inline" />
            </TooltipTrigger>
            <TooltipContent side="top">
              <p>Maximum allowed plan resets before termination</p>
            </TooltipContent>
          </Tooltip>
        </Label>
        <Input
          type="number"
          min="0"
          max="10"
          value={maxResetCount}
          onChange={(e) => onMaxResetCountChange(Number(e.target.value))}
          className="w-[100px]"
        />
      </div>
    </>
  );
}
