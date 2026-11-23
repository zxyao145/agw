"use client";

import * as React from "react";
import {
  Button,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@agw/components";
import { useReactFlow } from "reactflow";
import type { AgentflowPosition } from "../../../../types/agentflow";
import type { FlowEdge, FlowNode } from "./agentflow-canvas-view";
import { copyNodes, deleteEdge } from "./agentflow-clipboard";
import { useAgentflowEditorStore, useAgentflowEditorStoreApi } from "./agentflow-editor-store";
import { AGENTFLOW_TEMPLATES } from "./agentflow-templates";
import { useAgentflowTemplates } from "./use-agentflow-canvas-commands";
import type { useAgentflowArrangement } from "./use-agentflow-arrangement";
import type { useAgentflowNodePicker } from "./use-agentflow-node-picker";

type CanvasMenuState =
  | { type: "templates" }
  | { type: "node"; screen: AgentflowPosition; nodeId: string; isInput: boolean }
  | { type: "edge"; screen: AgentflowPosition; edgeId: string; position?: AgentflowPosition }
  | { type: "pane"; screen: AgentflowPosition; position: AgentflowPosition };

type ContextOptions = Pick<ReturnType<typeof useAgentflowNodePicker>, "openAdd" | "openInsert"> & {
  arrange: ReturnType<typeof useAgentflowArrangement>["arrange"];
  deleteNodes(ids: string[]): void;
  duplicate(ids: string[]): void;
  paste(position: AgentflowPosition): void;
};

type MenuItem = { label: string; action(): void; opensPicker?: boolean };

export function useAgentflowCanvasContextMenu(options: ContextOptions) {
  const store = useAgentflowEditorStoreApi();
  const flow = useReactFlow();
  const hasClipboard = useAgentflowEditorStore((state) => Boolean(state.clipboard));
  const [menu, setMenu] = React.useState<CanvasMenuState | null>(null);
  const close = () => {
    setMenu(null);
    return menu !== null;
  };
  const fitView = () => void flow.fitView({ padding: 0.2, maxZoom: 1 });
  const applyTemplate = useAgentflowTemplates(options.arrange, close);
  const handlers = createContextHandlers(store.getState, setMenu, flow.screenToFlowPosition);
  const content =
    menu?.type === "templates" ? (
      <TemplatesMenu onClose={close} onChoose={(templateId) => void applyTemplate(templateId)} />
    ) : menu ? (
      <CanvasContextMenu
        screen={menu.screen}
        close={close}
        items={getContextMenuItems(menu, { ...options, store, fitView, hasClipboard })}
      />
    ) : null;
  return {
    ...handlers,
    close,
    content,
    applyTemplate,
    openTemplates: () => setMenu({ type: "templates" }),
  };
}

type EditorStore = ReturnType<typeof useAgentflowEditorStoreApi>;
type ContextActions = ContextOptions & {
  store: EditorStore;
  fitView(): void;
  hasClipboard: boolean;
};

function createContextHandlers(
  getState: EditorStore["getState"],
  setMenu: (menu: CanvasMenuState) => void,
  screenToFlow: (position: AgentflowPosition) => AgentflowPosition,
) {
  return {
    onNodeContextMenu: (event: React.MouseEvent, node: FlowNode) => {
      const screen = readScreenPosition(event);
      setMenu({ type: "node", screen, nodeId: node.id, isInput: node.data.node.kind === "input" });
      getState().selectNodes([node.id]);
    },
    onEdgeContextMenu: (event: React.MouseEvent, edge: FlowEdge) => {
      const screen = readScreenPosition(event);
      setMenu({ type: "edge", screen, edgeId: edge.id, position: edge.data?.route?.label });
      getState().selectEdge(edge.id);
    },
    onPaneContextMenu: (event: React.MouseEvent) => {
      const screen = readScreenPosition(event);
      setMenu({ type: "pane", screen, position: screenToFlow(screen) });
    },
  };
}

function readScreenPosition(event: React.MouseEvent): AgentflowPosition {
  event.preventDefault();
  return { x: event.clientX, y: event.clientY };
}

function getContextMenuItems(
  menu: Exclude<CanvasMenuState, { type: "templates" }>,
  actions: ContextActions,
): MenuItem[] {
  switch (menu.type) {
    case "node":
      return getNodeMenuItems(menu, actions);
    case "edge":
      return [
        ...(menu.position
          ? [
              {
                label: "Insert a node",
                opensPicker: true,
                action: () => actions.openInsert(menu.edgeId, menu.position!),
              },
            ]
          : []),
        {
          label: "Delete connection",
          action: () =>
            actions.store
              .getState()
              .updateDocument((document) => deleteEdge(document, menu.edgeId)),
        },
      ];
    case "pane":
      return [
        {
          label: "Add a node here",
          opensPicker: true,
          action: () => actions.openAdd(menu.position),
        },
        ...(actions.hasClipboard
          ? [{ label: "Paste", action: () => actions.paste(menu.position) }]
          : []),
        { label: "Tidy up", action: () => void actions.arrange() },
        { label: "Fit view", action: actions.fitView },
      ];
  }
}

function getNodeMenuItems(
  menu: Extract<CanvasMenuState, { type: "node" }>,
  actions: ContextActions,
): MenuItem[] {
  if (menu.isInput) return [{ label: "Fit view", action: actions.fitView }];
  const { nodeId } = menu;
  const copy = () => {
    const { document, setClipboard } = actions.store.getState();
    setClipboard(copyNodes(document, [nodeId]));
  };
  return [
    { label: "Duplicate", action: () => actions.duplicate([nodeId]) },
    { label: "Copy", action: copy },
    { label: "Delete", action: () => actions.deleteNodes([nodeId]) },
  ];
}

function CanvasContextMenu({
  screen,
  items,
  close,
}: {
  screen: AgentflowPosition;
  items: MenuItem[];
  close(): void;
}) {
  const openingPicker = React.useRef(false);
  const restoreFocus = (event: Event) => {
    event.preventDefault();
    if (openingPicker.current) return;
    const dialog = document.getElementById("agentflow-builder")?.closest('[role="dialog"]');
    if (dialog instanceof HTMLElement) dialog.focus();
  };
  return (
    <DropdownMenu
      open
      onOpenChange={(open) => {
        if (!open) close();
      }}
    >
      <DropdownMenuTrigger asChild>
        <button
          type="button"
          tabIndex={-1}
          aria-hidden="true"
          className="fixed h-px w-px opacity-0"
          style={{ left: screen.x, top: screen.y }}
        />
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" className="min-w-40" onCloseAutoFocus={restoreFocus}>
        {items.map((item) => (
          <DropdownMenuItem
            key={item.label}
            onSelect={() => {
              openingPicker.current = Boolean(item.opensPicker);
              close();
              item.action();
            }}
          >
            {item.label}
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

function TemplatesMenu({ onClose, onChoose }: { onClose(): void; onChoose(id: string): void }) {
  return (
    <div className="fixed inset-0 z-50" onClick={onClose}>
      <div
        className="absolute left-1/2 top-1/2 grid max-w-xl -translate-x-1/2 -translate-y-1/2 gap-2 rounded-xl border bg-popover p-4 shadow-lg"
        onClick={(event) => event.stopPropagation()}
      >
        <h2 className="text-sm font-semibold">Templates</h2>
        {AGENTFLOW_TEMPLATES.map((template) => (
          <Button key={template.id} variant="outline" onClick={() => onChoose(template.id)}>
            {template.name}
          </Button>
        ))}
      </div>
    </div>
  );
}
