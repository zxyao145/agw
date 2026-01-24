"use client";

import { Info } from "lucide-react";
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { InitMessageContent } from "../types";

interface ChatInfoPopoverProps {
  initContent: InitMessageContent | null;
  createArr: (key: string, value: string[] | undefined) => React.ReactNode;
}

export function ChatInfoPopover({ initContent, createArr }: ChatInfoPopoverProps) {
  if (!initContent) {
    return (
      <Button variant="ghost" className="cursor-pointer" disabled>
        <Info className="h-4 w-4" />
      </Button>
    );
  }

  return (
    <Popover>
      <PopoverTrigger asChild>
        <Button variant="ghost" className="cursor-pointer">
          <Info className="h-4 w-4" />
        </Button>
      </PopoverTrigger>
      <PopoverContent className="w-120">
        <div className="grid gap-4">
          <div className="space-y-2">
            <h4 className="leading-none font-medium">Claude Code Info</h4>
            <p className="text-muted-foreground text-sm">Claude Code meta info</p>
          </div>
          <div className="grid max-h-80 overflow-auto">
            <div className="grid grid-cols-3 items-center py-2 border-b">
              <Label>claudeCodeVersion</Label>
              <div className="col-span-2">{initContent.claudeCodeVersion}</div>
            </div>
            <div className="grid grid-cols-3 items-center py-2 border-b">
              <Label>permissionMode</Label>
              <div className="col-span-2">{initContent.permissionMode}</div>
            </div>
            <div className="grid grid-cols-3 items-center py-2 border-b">
              <Label>model</Label>
              <div className="col-span-2">{initContent.model}</div>
            </div>
            {createArr("tools", initContent.tools)}
            {createArr("slashCommands", initContent.slashCommands)}
            {createArr("agents", initContent.agents)}
            {createArr("plugins", initContent.plugins)}
            {createArr("mcpServers", initContent.mcpServers)}
          </div>
        </div>
      </PopoverContent>
    </Popover>
  );
}
