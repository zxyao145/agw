"use client";

import type { PropsWithChildren, ReactNode } from "react";
import { ResizableHandle, ResizablePanel, ResizablePanelGroup } from "@agw/components";

type AgentflowBuilderLayoutProps = PropsWithChildren<{
  library: ReactNode;
  canvas: ReactNode;
  bottomPanel: ReactNode;
  inspector: ReactNode;
}>;

export function AgentflowBuilderLayout({
  library,
  canvas,
  bottomPanel,
  inspector,
  children,
}: AgentflowBuilderLayoutProps) {
  return (
    <ResizablePanelGroup id="agentflow-builder" orientation="horizontal" className="h-full min-w-0">
      <ResizablePanel id="agentflow-node-library" defaultSize="280px" minSize="220px">
        {library}
      </ResizablePanel>
      <ResizableHandle aria-label="Resize node library" />
      <ResizablePanel id="agentflow-workspace" minSize={320}>
        <ResizablePanelGroup orientation="vertical" className="h-full min-h-0">
          <ResizablePanel id="agentflow-canvas" minSize={160}>
            {canvas}
          </ResizablePanel>
          <ResizableHandle aria-label="Resize bottom panel" />
          {bottomPanel}
        </ResizablePanelGroup>
      </ResizablePanel>
      <ResizableHandle aria-label="Resize inspector" />
      <ResizablePanel id="agentflow-inspector" defaultSize="340px" minSize="280px">
        {inspector}
      </ResizablePanel>
      {children}
    </ResizablePanelGroup>
  );
}
