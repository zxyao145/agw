"use client";

import * as React from "react";
import { Group, Panel, Separator } from "react-resizable-panels";

import { cn } from "../../lib/cn";

function ResizablePanelGroup({ className, ...props }: React.ComponentProps<typeof Group>) {
  return (
    <Group
      data-slot="resizable-panel-group"
      className={cn("h-full w-full", className)}
      {...props}
    />
  );
}

function ResizablePanel(props: React.ComponentProps<typeof Panel>) {
  return <Panel data-slot="resizable-panel" {...props} />;
}

function ResizableHandle({ className, ...props }: React.ComponentProps<typeof Separator>) {
  return (
    <Separator
      data-slot="resizable-handle"
      className={cn(
        "relative z-20 w-px shrink-0 bg-border transition-colors hover:bg-primary focus-visible:bg-primary focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring aria-[orientation=horizontal]:h-px aria-[orientation=horizontal]:w-full",
        className,
      )}
      {...props}
    />
  );
}

export { ResizablePanelGroup, ResizablePanel, ResizableHandle };
