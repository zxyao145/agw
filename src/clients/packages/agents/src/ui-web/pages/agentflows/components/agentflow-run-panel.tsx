"use client";

import { Chat } from "@agw/chat";
import { Button } from "@agw/components";
import { LocateFixed } from "lucide-react";
import { toast } from "sonner";
import { useAgentflowEditorStore, useAgentflowEditorStoreApi } from "./agentflow-editor-store";
import { AGENTFLOW_RUN_PROJECT_ID } from "./utils";

type AgentflowRunPanelProps = {
  runSeed: number;
  onSave?(): void;
  onShowNode(nodeId: string): void;
};

export function AgentflowRunPanel(props: AgentflowRunPanelProps) {
  const { runSeed, onSave, onShowNode } = props;
  const agentflowId = useAgentflowEditorStore((state) => state.agentflowId);
  const revision = useAgentflowEditorStore((state) => state.baseRevision);
  if (!agentflowId) return null;
  return (
    <div className="flex h-full min-h-0 flex-col gap-2">
      <DraftRunNotice revision={revision} onSave={onSave} />
      <AgentflowRunChat
        agentflowId={agentflowId}
        revision={revision}
        runSeed={runSeed}
        onShowNode={onShowNode}
      />
    </div>
  );
}

function DraftRunNotice({ revision, onSave }: { revision: number; onSave?(): void }) {
  const isDirty = useAgentflowEditorStore((state) => state.isDirty);
  if (!isDirty) return null;
  return (
    <div className="flex shrink-0 items-center justify-between rounded-md bg-muted px-2 py-1 text-xs text-muted-foreground">
      <span>Unsaved changes are not part of this run. It runs v{revision}.</span>
      <Button type="button" size="sm" variant="outline" onClick={() => onSave?.()}>
        Save
      </Button>
    </div>
  );
}

function AgentflowRunChat({
  agentflowId,
  revision,
  runSeed,
  onShowNode,
}: Omit<AgentflowRunPanelProps, "onSave"> & { agentflowId: string; revision: number }) {
  const store = useAgentflowEditorStoreApi();
  const active = useAgentflowEditorStore(
    (state) => state.panelExpanded && state.panelTab === "run",
  );
  const applyRunStatus = useAgentflowEditorStore((state) => state.applyRunStatus);
  const applyRunMessage = useAgentflowEditorStore((state) => state.applyRunMessage);
  const showMessageNode = ({ messageId }: { messageId: string }) => {
    const { document, runState } = store.getState();
    const nodeId = runState.nodeIdByMessageId[messageId];
    if (nodeId && document.nodes.some((node) => node.nodeId === nodeId)) onShowNode(nodeId);
    else toast.error("This message does not belong to a node.");
  };
  return (
    <Chat
      className="min-h-0 flex-1"
      target={{ id: agentflowId, type: "agentflow", agentflowRevision: revision }}
      projectId={AGENTFLOW_RUN_PROJECT_ID}
      conversationId={null}
      active={active}
      sessionSeed={{
        revision: `${agentflowId}:${runSeed}`,
        conversationId: null,
        messages: [],
        historyTurns: [],
        olderMessagesCursor: null,
        hasOlderMessages: false,
        agentMode: null,
      }}
      placeholder="Send a message to this Agentflow..."
      onExecutionStatus={applyRunStatus}
      onExecutionMessage={applyRunMessage}
      messageActions={[
        {
          id: "show-agentflow-node",
          title: "Show node on canvas",
          icon: <LocateFixed className="h-4 w-4" />,
          onSelect: showMessageNode,
        },
      ]}
    />
  );
}
