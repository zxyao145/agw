"use client";

import * as React from "react";
import {
  Button,
  Input,
  SearchableSelect,
  Switch,
  Textarea,
  type SearchableSelectOption,
} from "@agw/components";
import { ArrowDown, ArrowUp, Bot, Crown, GripVertical, Plus, Trash2, Workflow } from "lucide-react";

import type {
  AgentDto,
  AgentflowBlockMember,
  AgentflowDto,
  AgentflowEdge,
  AgentflowNode,
  AgentflowRoutingStrategy,
  ModelProviderDto,
} from "../../../../types/agentflow";
import { deleteEdge } from "./agentflow-clipboard";
import { describeAgentflowEdge } from "./agentflow-condition";
import { AgentflowConditionEditor } from "./agentflow-condition-editor";
import { createId, removeMember, type NodeReference } from "./agentflow-document";
import {
  clearSwitchDefault,
  getRegularOutgoingEdges,
  getSwitchCases,
  removeGateChoice,
  reorderSwitchCase,
  setFanInBarrier,
  setRoutingStrategy,
  setSwitchDefault,
} from "./agentflow-edge-routing";
import { AgentflowNodeMenu } from "./agentflow-node-menu";
import { AGENTFLOW_NODE_META } from "./agentflow-node-meta";
import { getNodeRunStatus } from "./agentflow-run-state";
import { useAgentflowEditorStore } from "./agentflow-editor-store";
import type { AgentflowEditorDocument } from "./agentflow-document";
import type { AgentflowEditorHistoryMode } from "./agentflow-editor-store";

const BRANCH_DRAG_TYPE = "application/x-agw-agentflow-branch";
const CHOICE_DRAG_TYPE = "application/x-agw-agentflow-choice";
const ROUTING_OPTIONS = {
  direct: {
    label: "Pass on",
    description: "Each connection passes the output on. A condition can hold one back.",
  },
  fanOut: {
    label: "Send to all",
    description: "Every connected node runs. A condition can leave one out.",
  },
  switch: {
    label: "Branch",
    description: "Only the first branch whose condition matches runs. Else runs when none match.",
  },
} as const;
const ROUTING_STRATEGIES = ["direct", "fanOut", "switch"] as const;

type AgentflowInspectorProps = {
  agents: AgentDto[];
  agentflows: AgentflowDto[];
  modelProviders: ModelProviderDto[];
  onAddMember(nodeId: string, reference: NodeReference): void;
  onDuplicateNodes(nodeIds: string[]): void;
  onDeleteNodes(nodeIds: string[]): void;
};

export function AgentflowInspector(props: AgentflowInspectorProps) {
  const document = useAgentflowEditorStore((state) => state.document);
  const selectedNodeIds = useAgentflowEditorStore((state) => state.selectedNodeIds);
  const selectedEdgeId = useAgentflowEditorStore((state) => state.selectedEdgeId);
  const selectedMember = useAgentflowEditorStore((state) => state.selectedMember);

  if (selectedMember) {
    const block = document.nodes.find((node) => node.nodeId === selectedMember.nodeId);
    if (block && "members" in block) {
      const member = block.members.find((item) => item.memberId === selectedMember.memberId);
      if (member) return <MemberInspector {...props} block={block} member={member} />;
    }
  }
  if (selectedNodeIds.length > 1) {
    return <MultiNodeInspector {...props} nodeIds={selectedNodeIds} />;
  }
  const node = document.nodes.find((item) => item.nodeId === selectedNodeIds[0]);
  if (node) return <NodeInspector {...props} node={node} document={document} />;
  const edge = document.edges.find((item) => item.edgeId === selectedEdgeId);
  if (edge) return <ConnectionInspector edge={edge} document={document} agents={props.agents} />;
  return <FlowInspector document={document} />;
}

function useInspectorChanges() {
  const updateDocument = useAgentflowEditorStore((state) => state.updateDocument);
  const commitHistoryGroup = useAgentflowEditorStore((state) => state.commitHistoryGroup);
  const selectNode = useAgentflowEditorStore((state) => state.selectNodes);
  const selectEdge = useAgentflowEditorStore((state) => state.selectEdge);
  const selectMember = useAgentflowEditorStore((state) => state.selectMember);

  const updateNode = React.useCallback(
    (nodeId: string, transform: (node: AgentflowNode) => AgentflowNode, group?: string) => {
      updateDocument(
        (current) => ({
          ...current,
          nodes: current.nodes.map((node) => (node.nodeId === nodeId ? transform(node) : node)),
        }),
        group ? { group } : "atomic",
      );
    },
    [updateDocument],
  );
  const updateEdge = React.useCallback(
    (edgeId: string, transform: (edge: AgentflowEdge) => AgentflowEdge, group?: string) => {
      updateDocument(
        (current) => ({
          ...current,
          edges: current.edges.map((edge) => (edge.edgeId === edgeId ? transform(edge) : edge)),
        }),
        group ? { group } : "atomic",
      );
    },
    [updateDocument],
  );
  return {
    updateDocument,
    commitHistoryGroup,
    selectNode,
    selectEdge,
    selectMember,
    updateNode,
    updateEdge,
  };
}

type MemberInspectorProps = Pick<AgentflowInspectorProps, "agents" | "agentflows"> & {
  block: Extract<AgentflowNode, { members: AgentflowBlockMember[] }>;
  member: AgentflowBlockMember;
};

function MemberInspector(props: MemberInspectorProps) {
  const { member } = props;
  return (
    <InspectorShell
      title={member.name}
      subtitle="Block member"
      icon={member.kind === "agent" ? Bot : Workflow}
    >
      <MemberFields {...props} />
      <MemberActions block={props.block} member={member} />
    </InspectorShell>
  );
}

function MemberFields({ block, member, agents, agentflows }: MemberInspectorProps) {
  const updateDocument = useAgentflowEditorStore((state) => state.updateDocument);
  const onBlur = useAgentflowEditorStore((state) => state.commitHistoryGroup);
  const update = (patch: Partial<AgentflowBlockMember>, group?: string) =>
    updateMember(updateDocument, block.nodeId, member.memberId, patch, group);
  return (
    <>
      <Field label="Name">
        <Input
          value={member.name}
          onChange={(event) => update({ name: event.target.value }, "member-name")}
          onBlur={onBlur}
        />
      </Field>
      <MemberReference
        member={member}
        agents={agents}
        agentflows={agentflows}
        onChange={(relateId, syncedName) => update({ relateId, name: syncedName ?? member.name })}
      />
      <Field label="Instructions">
        <Textarea
          value={member.instructions ?? ""}
          rows={4}
          onBlur={onBlur}
          onChange={(event) => update({ instructions: event.target.value }, "member-instructions")}
        />
      </Field>
    </>
  );
}

function MemberReference({
  member,
  agents,
  agentflows,
  onChange,
}: Omit<MemberInspectorProps, "block"> & {
  onChange(relateId: string | null, syncedName?: string): void;
}) {
  const agentflowId = useAgentflowEditorStore((state) => state.agentflowId);
  if (member.kind !== "agent" && member.kind !== "workflowAsAgent") {
    return <p className="text-xs text-muted-foreground">Unsupported member kind: {member.kind}</p>;
  }
  return (
    <Field label={member.kind === "agent" ? "Agent" : "Workflow"}>
      <ReferenceSelect
        kind={member.kind}
        relateId={member.relateId ?? null}
        name={member.name}
        agents={agents}
        agentflows={agentflows}
        currentAgentflowId={agentflowId}
        onChange={onChange}
      />
    </Field>
  );
}

function MemberActions({ block, member }: Pick<MemberInspectorProps, "block" | "member">) {
  const { updateDocument, updateNode, selectMember, selectNode } = useInspectorChanges();
  const makeManager = () =>
    updateNode(block.nodeId, (node) =>
      node.kind === "magenticBlock"
        ? {
            ...node,
            members: node.members.map((item) => ({
              ...item,
              isManager: item.memberId === member.memberId,
            })),
          }
        : node,
    );
  const remove = () => {
    updateDocument((current) => removeMember(current, block.nodeId, member.memberId));
    selectMember(null);
  };
  return (
    <>
      {block.kind === "magenticBlock" ? (
        <Button
          type="button"
          variant={member.isManager ? "secondary" : "outline"}
          onClick={makeManager}
        >
          <Crown className="mr-2 h-4 w-4" /> {member.isManager ? "Manager" : "Make manager"}
        </Button>
      ) : null}
      <Button type="button" variant="outline" onClick={remove}>
        <Trash2 className="mr-2 h-4 w-4" /> Remove from block
      </Button>
      <Button
        type="button"
        variant="ghost"
        className="justify-start"
        onClick={() => selectNode([block.nodeId])}
      >
        Back to {block.name}
      </Button>
    </>
  );
}

function MultiNodeInspector({
  nodeIds,
  onDuplicateNodes,
  onDeleteNodes,
}: Pick<AgentflowInspectorProps, "onDuplicateNodes" | "onDeleteNodes"> & { nodeIds: string[] }) {
  return (
    <InspectorShell title={`${nodeIds.length} nodes selected`} subtitle="Selection">
      <div className="flex gap-2">
        <Button variant="outline" onClick={() => onDuplicateNodes(nodeIds)}>
          Duplicate
        </Button>
        <Button variant="destructive" onClick={() => onDeleteNodes(nodeIds)}>
          Delete
        </Button>
      </div>
    </InspectorShell>
  );
}

type NodeInspectorProps = Pick<
  AgentflowInspectorProps,
  "agents" | "agentflows" | "modelProviders" | "onAddMember"
> & {
  node: AgentflowNode;
  document: AgentflowEditorDocument;
};

function NodeInspector(props: NodeInspectorProps) {
  const { node, document, agents } = props;
  const meta = AGENTFLOW_NODE_META[node.kind];
  return (
    <InspectorShell title={node.name ?? meta.label} subtitle={meta.label} icon={meta.icon}>
      <SelectedNodeFields {...props} />
      <NodeHint kind={node.kind} />
      <NodeOutgoing node={node} document={document} agents={agents} />
      {node.kind !== "input" ? <NodeIncoming nodeId={node.nodeId} document={document} /> : null}
      <NodeRunDetails nodeId={node.nodeId} />
    </InspectorShell>
  );
}

function SelectedNodeFields({
  node,
  agents,
  agentflows,
  modelProviders,
  onAddMember,
}: NodeInspectorProps) {
  const agentflowId = useAgentflowEditorStore((state) => state.agentflowId);
  const { updateDocument, commitHistoryGroup, selectMember, updateNode } = useInspectorChanges();
  const { nodeId } = node;
  return (
    <NodeFields
      node={node}
      agents={agents}
      agentflows={agentflows}
      modelProviders={modelProviders}
      currentAgentflowId={agentflowId}
      commitHistoryGroup={commitHistoryGroup}
      onChange={(transform, group) => updateNode(nodeId, transform, group)}
      onMemberSelect={(memberId) => selectMember({ nodeId, memberId })}
      onAddMember={(reference) => onAddMember(nodeId, reference)}
      onRemoveMember={(memberId) =>
        updateDocument((current) => removeMember(current, nodeId, memberId))
      }
      onRemoveChoice={(choiceId) =>
        updateDocument((current) => removeGateChoice(current, nodeId, choiceId))
      }
    />
  );
}

function NodeHint({ kind }: { kind: AgentflowNode["kind"] }) {
  const note =
    kind === "input"
      ? "Starts the flow with the user's message. It cannot be deleted or used as a target."
      : kind === "clearMessages"
        ? "Drops everything received so far. The next node starts with empty input."
        : null;
  return note ? (
    <p className="rounded-md bg-muted p-2 text-xs text-muted-foreground">{note}</p>
  ) : null;
}

function NodeOutgoing({
  node,
  document,
  agents,
}: Pick<NodeInspectorProps, "node" | "document" | "agents">) {
  const { updateDocument, selectEdge, updateEdge } = useInspectorChanges();
  const edges = document.edges.filter((edge) => edge.sourceNodeId === node.nodeId);
  const sourceAgent =
    node.kind === "agent" ? agents.find((agent) => agent.id === node.relateId) : undefined;
  const onRemoveEdge = (edgeId: string) => updateDocument((current) => deleteEdge(current, edgeId));
  if (node.kind === "humanGate") {
    return (
      <GateOutgoingSection
        node={node}
        edges={edges}
        document={document}
        onSelectEdge={selectEdge}
        onChangeEdge={updateEdge}
        onRemoveEdge={onRemoveEdge}
      />
    );
  }
  return (
    <OutgoingSection
      node={node}
      edges={edges}
      document={document}
      onSelectEdge={selectEdge}
      onRemoveEdge={onRemoveEdge}
      sourceProducesResult={Boolean(sourceAgent?.enableSummary && sourceAgent.responseSchema)}
      responseSchema={sourceAgent?.responseSchema}
      onChangeStrategy={(strategy) =>
        updateDocument((current) => setRoutingStrategy(current, node.nodeId, strategy))
      }
      onMoveCase={(edgeId, targetIndex) =>
        updateDocument((current) => reorderSwitchCase(current, edgeId, targetIndex))
      }
      onSetDefault={(edgeId) => updateDocument((current) => setSwitchDefault(current, edgeId))}
      onClearDefault={(edgeId) => updateDocument((current) => clearSwitchDefault(current, edgeId))}
    />
  );
}

function NodeIncoming({ nodeId, document }: { nodeId: string; document: AgentflowEditorDocument }) {
  const { updateDocument, selectNode } = useInspectorChanges();
  const edges = document.edges.filter((edge) => edge.targetNodeId === nodeId);
  return (
    <IncomingSection
      edges={edges}
      document={document}
      onSelectNode={(id) => selectNode([id])}
      onToggle={(id, value) => updateDocument((current) => setFanInBarrier(current, id, value))}
    />
  );
}

function NodeRunDetails({ nodeId }: { nodeId: string }) {
  const runState = useAgentflowEditorStore((state) => state.runState);
  const latestOutput = runState.latestOutputByNodeId[nodeId];
  const status = getNodeRunStatus(runState, nodeId);
  if (!status && !latestOutput) return null;
  return (
    <section className="space-y-2 border-t pt-3">
      <h3 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
        Last run
      </h3>
      {status ? <p className="text-sm capitalize">{status}</p> : null}
      {latestOutput ? (
        <pre className="max-h-40 overflow-auto whitespace-pre-wrap rounded-md bg-muted p-2 text-xs">
          {latestOutput.text}
        </pre>
      ) : null}
    </section>
  );
}

type ConnectionInspectorProps = Pick<AgentflowInspectorProps, "agents"> & {
  edge: AgentflowEdge;
  document: AgentflowEditorDocument;
};

function ConnectionInspector({ edge, document, agents }: ConnectionInspectorProps) {
  const { updateDocument, selectNode, selectEdge } = useInspectorChanges();
  const source = document.nodes.find((node) => node.nodeId === edge.sourceNodeId);
  const target = document.nodes.find((node) => node.nodeId === edge.targetNodeId);
  const showNode = (nodeId: string) => selectNode([nodeId]);
  const remove = () => {
    updateDocument((current) => deleteEdge(current, edge.edgeId));
    selectEdge(null);
  };
  return (
    <InspectorShell
      title={
        <>
          <NodeLink node={source} onSelect={showNode} />
          {" → "}
          <NodeLink node={target} onSelect={showNode} />
        </>
      }
      subtitle="Connection"
    >
      <div className="space-y-2 rounded-md bg-muted p-2 text-xs text-muted-foreground">
        <p>{getEdgeSummary(edge, source, target)}</p>
        <Button
          type="button"
          variant="link"
          size="sm"
          className="h-auto p-0"
          onClick={() => source && showNode(source.nodeId)}
        >
          Change how {source?.name ?? "this node"} hands off
        </Button>
      </div>
      <ConnectionRouting
        edge={edge}
        source={source}
        sourceAgent={
          source?.kind === "agent"
            ? agents.find((agent) => agent.id === source.relateId)
            : undefined
        }
      />
      <ConnectionLabel
        edge={edge}
        placeholder={describeAgentflowEdge(edge, document.nodes, document.edges)}
      />
      <Button type="button" variant="destructive" onClick={remove}>
        Delete connection
      </Button>
    </InspectorShell>
  );
}

function NodeLink({ node, onSelect }: { node?: AgentflowNode; onSelect(id: string): void }) {
  return node ? (
    <button type="button" className="hover:underline" onClick={() => onSelect(node.nodeId)}>
      {node.name ?? AGENTFLOW_NODE_META[node.kind].label}
    </button>
  ) : (
    "Missing node"
  );
}

function ConnectionRouting({
  edge,
  source,
  sourceAgent,
}: {
  edge: AgentflowEdge;
  source?: AgentflowNode;
  sourceAgent?: AgentDto;
}) {
  const updateDocument = useAgentflowEditorStore((state) => state.updateDocument);
  return (
    <>
      <Field label="Wait">
        <Switch
          checked={edge.isFanInBarrier}
          onCheckedChange={(value) =>
            updateDocument((current) => setFanInBarrier(current, edge.edgeId, value))
          }
          aria-label="Wait"
        />
      </Field>
      {!edge.isFanInBarrier && !edge.isSwitchDefault && source?.kind !== "humanGate" ? (
        <EdgeConditions
          edge={edge}
          sourceProducesResult={Boolean(sourceAgent?.enableSummary && sourceAgent.responseSchema)}
          responseSchema={sourceAgent?.responseSchema}
        />
      ) : null}
      {!edge.isFanInBarrier && edge.isSwitchDefault ? (
        <p className="text-xs text-muted-foreground">Else runs when no other branch matches.</p>
      ) : null}
    </>
  );
}

function ConnectionLabel({ edge, placeholder }: { edge: AgentflowEdge; placeholder: string }) {
  const { updateEdge, commitHistoryGroup } = useInspectorChanges();
  return (
    <Field label="Label on the canvas">
      <Input
        value={edge.label ?? ""}
        onChange={(event) =>
          updateEdge(
            edge.edgeId,
            (item) => ({ ...item, label: event.target.value || null }),
            `edge-label-${edge.edgeId}`,
          )
        }
        onBlur={commitHistoryGroup}
        placeholder={placeholder}
      />
    </Field>
  );
}

function FlowInspector({ document }: { document: AgentflowEditorDocument }) {
  const { updateDocument, commitHistoryGroup } = useInspectorChanges();
  return (
    <InspectorShell title="Agentflow settings" subtitle="">
      <Field label="Description">
        <Textarea
          value={document.description}
          onChange={(event) =>
            updateDocument((current) => ({ ...current, description: event.target.value }), {
              group: "description",
            })
          }
          onBlur={commitHistoryGroup}
          rows={4}
        />
      </Field>
      <p className="text-xs text-muted-foreground">
        {document.nodes.length} nodes · {document.edges.length} connections
      </p>
      <p className="text-sm text-muted-foreground">Select a node or a connection to edit it.</p>
    </InspectorShell>
  );
}

function InspectorShell({
  title,
  subtitle,
  icon: Icon,
  children,
}: React.PropsWithChildren<{
  title: React.ReactNode;
  subtitle: string;
  icon?: React.ComponentType<{ className?: string }>;
}>) {
  return (
    <aside className="flex h-full min-h-0 flex-col border-l bg-background">
      <header className="flex min-h-14 shrink-0 items-center gap-2 border-b px-3 py-2">
        {Icon ? <Icon className="h-4 w-4 shrink-0 text-muted-foreground" /> : null}
        <span className="min-w-0">
          <span className="block truncate text-sm font-semibold">{title}</span>
          {subtitle ? (
            <span className="block truncate text-xs text-muted-foreground">{subtitle}</span>
          ) : null}
        </span>
      </header>
      <div className="min-h-0 flex-1 space-y-4 overflow-y-auto p-3">{children}</div>
    </aside>
  );
}

function Field({ label, children }: React.PropsWithChildren<{ label: string }>) {
  return (
    <label className="grid gap-1.5 text-xs font-medium">
      {label}
      {children}
    </label>
  );
}

type NodeFieldsProps = {
  node: AgentflowNode;
  agents: AgentDto[];
  agentflows: AgentflowDto[];
  modelProviders: ModelProviderDto[];
  currentAgentflowId: string | null;
  commitHistoryGroup(): void;
  onChange(transform: (node: AgentflowNode) => AgentflowNode, group?: string): void;
  onMemberSelect(memberId: string): void;
  onAddMember(reference: NodeReference): void;
  onRemoveMember(memberId: string): void;
  onRemoveChoice(choiceId: string): void;
};

type NodeChange = NodeFieldsProps["onChange"];

function NodeFields(props: NodeFieldsProps) {
  const { node, onChange, commitHistoryGroup } = props;
  if (node.kind === "input") return null;

  return (
    <>
      <Field label="Name">
        <Input
          value={node.name ?? AGENTFLOW_NODE_META[node.kind].label}
          onChange={(event) =>
            onChange(
              (current) => ({ ...current, name: event.target.value }),
              `node-name-${node.nodeId}`,
            )
          }
          onBlur={commitHistoryGroup}
        />
      </Field>
      <NodeTypeFields {...props} />
    </>
  );
}

function NodeTypeFields(props: NodeFieldsProps) {
  const { node, onChange, commitHistoryGroup } = props;
  switch (node.kind) {
    case "agent":
    case "workflowAsAgent":
      return <ReferenceNodeFields {...props} node={node} />;
    case "promptAdapter":
      return <NodeInstructions node={node} onChange={onChange} onBlur={commitHistoryGroup} />;
    case "humanGate":
      return <HumanGateFields {...props} node={node} />;
    case "checkpointMarker":
      return <CheckpointField node={node} onChange={onChange} onBlur={commitHistoryGroup} />;
    case "output":
      return <OutputFields {...props} node={node} />;
    case "concurrentBlock":
    case "handoffBlock":
    case "groupChatBlock":
    case "magenticBlock":
      return <BlockFields {...props} node={node} />;
    default:
      return null;
  }
}

function ReferenceNodeFields({
  node,
  agents,
  agentflows,
  currentAgentflowId,
  onChange,
  commitHistoryGroup,
}: NodeFieldsProps & { node: Extract<AgentflowNode, { kind: "agent" | "workflowAsAgent" }> }) {
  return (
    <>
      <Field label={node.kind === "agent" ? "Agent" : "Workflow"}>
        <ReferenceSelect
          kind={node.kind}
          relateId={node.relateId}
          name={node.name}
          agents={agents}
          agentflows={agentflows}
          currentAgentflowId={currentAgentflowId}
          onChange={(relateId, syncedName) =>
            onChange((current) =>
              current.kind === node.kind
                ? { ...current, relateId, name: syncedName ?? current.name }
                : current,
            )
          }
        />
      </Field>
      <NodeInstructions node={node} onChange={onChange} onBlur={commitHistoryGroup} />
    </>
  );
}

function NodeInstructions({
  node,
  onChange,
  onBlur,
  rows = 5,
  summary = false,
}: {
  node: Extract<AgentflowNode, { instructions: string | null }>;
  onChange: NodeChange;
  onBlur(): void;
  rows?: number;
  summary?: boolean;
}) {
  return (
    <Field label={summary ? "Summary instructions" : "Instructions"}>
      <Textarea
        value={node.instructions ?? ""}
        onChange={(event) => {
          const value = event.target.value;
          onChange(
            (current) =>
              "instructions" in current
                ? { ...current, instructions: summary ? value || null : value }
                : current,
            `node-instructions-${node.nodeId}`,
          );
        }}
        onBlur={onBlur}
        rows={rows}
      />
    </Field>
  );
}

type HumanGateNode = Extract<AgentflowNode, { kind: "humanGate" }>;
type HumanGateFieldsProps = NodeFieldsProps & { node: HumanGateNode };

function HumanGateFields(props: HumanGateFieldsProps) {
  const { node, onChange, commitHistoryGroup } = props;
  const patch = (fields: Partial<HumanGateNode>, group?: string) =>
    onChange(
      (current) => (current.kind === "humanGate" ? { ...current, ...fields } : current),
      group,
    );
  return (
    <>
      <Field label="Prompt">
        <Textarea
          value={node.humanPrompt ?? ""}
          onChange={(event) =>
            patch({ humanPrompt: event.target.value || null }, `node-human-prompt-${node.nodeId}`)
          }
          onBlur={commitHistoryGroup}
          rows={4}
        />
      </Field>
      <GateChoices {...props} />
      <Field label="Allow a text reply">
        <Switch
          checked={node.allowResponseText}
          aria-label="Allow a text reply"
          onCheckedChange={(allowResponseText) => patch({ allowResponseText })}
        />
      </Field>
    </>
  );
}

type GateChoice = HumanGateNode["choices"][number];

function GateChoices({ node, onChange, commitHistoryGroup, onRemoveChoice }: HumanGateFieldsProps) {
  const update = (transform: (choices: GateChoice[]) => GateChoice[], group?: string) =>
    onChange(
      (current) =>
        current.kind === "humanGate"
          ? { ...current, choices: transform(current.choices) }
          : current,
      group,
    );
  const move = (id: string, target: number) =>
    update((choices) => {
      const from = choices.findIndex((choice) => choice.choiceId === id);
      return from === -1 || from === target ? choices : moveItem(choices, from, target);
    });
  const rename = (id: string, label: string) =>
    update(
      (choices) =>
        choices.map((choice) => (choice.choiceId === id ? { ...choice, label } : choice)),
      `choice-${id}`,
    );
  const add = () =>
    update((choices) => [...choices, { choiceId: createId("choice"), label: "New choice" }]);
  return (
    <section className="space-y-2">
      <h3 className="text-xs font-semibold">Choices</h3>
      {node.choices.map((choice, index) => (
        <GateChoiceRow
          key={choice.choiceId}
          choice={choice}
          index={index}
          count={node.choices.length}
          rename={rename}
          move={move}
          onBlur={commitHistoryGroup}
          remove={onRemoveChoice}
        />
      ))}
      <Button type="button" variant="outline" size="sm" onClick={add}>
        <span className="mr-1">+</span> Add choice
      </Button>
    </section>
  );
}

function GateChoiceRow({
  choice,
  index,
  count,
  rename,
  move,
  remove,
  onBlur,
}: {
  choice: GateChoice;
  index: number;
  count: number;
  rename(id: string, label: string): void;
  move(id: string, index: number): void;
  remove(id: string): void;
  onBlur(): void;
}) {
  const id = choice.choiceId;
  return (
    <div
      className="flex items-center gap-1"
      {...reorderDropHandlers(CHOICE_DRAG_TYPE, (source) => move(source, index))}
    >
      <ReorderHandle itemType={CHOICE_DRAG_TYPE} itemId={id} label="Drag choice to reorder" />
      <Input
        value={choice.label}
        aria-label="Choice label"
        onBlur={onBlur}
        onChange={(event) => rename(id, event.target.value)}
      />
      <ReorderButtons
        label="choice"
        index={index}
        count={count}
        onMove={(target) => move(id, target)}
      />
      <Button
        type="button"
        variant="ghost"
        size="icon-sm"
        aria-label="Remove choice"
        disabled={count === 1}
        onClick={() => remove(id)}
      >
        <Trash2 className="h-3.5 w-3.5" />
      </Button>
    </div>
  );
}

function ReorderButtons({
  label,
  index,
  count,
  onMove,
}: {
  label: string;
  index: number;
  count: number;
  onMove(index: number): void;
}) {
  return (
    <>
      <Button
        type="button"
        size="icon-sm"
        variant="ghost"
        aria-label={`Move ${label} up`}
        disabled={index === 0}
        onClick={() => onMove(index - 1)}
      >
        <ArrowUp className="h-3.5 w-3.5" />
      </Button>
      <Button
        type="button"
        size="icon-sm"
        variant="ghost"
        aria-label={`Move ${label} down`}
        disabled={index === count - 1}
        onClick={() => onMove(index + 1)}
      >
        <ArrowDown className="h-3.5 w-3.5" />
      </Button>
    </>
  );
}

function CheckpointField({
  node,
  onChange,
  onBlur,
}: {
  node: Extract<AgentflowNode, { kind: "checkpointMarker" }>;
  onChange: NodeChange;
  onBlur(): void;
}) {
  return (
    <Field label="Checkpoint name">
      <Input
        value={node.checkpointName ?? ""}
        onChange={(event) =>
          onChange(
            (current) =>
              current.kind === "checkpointMarker"
                ? { ...current, checkpointName: event.target.value || null }
                : current,
            `checkpoint-${node.nodeId}`,
          )
        }
        onBlur={onBlur}
      />
    </Field>
  );
}

function OutputFields({
  node,
  modelProviders,
  onChange,
  commitHistoryGroup,
}: NodeFieldsProps & { node: Extract<AgentflowNode, { kind: "output" }> }) {
  const updateModel = (summaryModelProviderId: string | null) =>
    onChange((current) =>
      current.kind === "output" ? { ...current, summaryModelProviderId } : current,
    );
  return (
    <>
      <Field label="Write a summary">
        <Switch
          checked={node.enableSummary}
          onCheckedChange={(checked) =>
            onChange((current) =>
              current.kind === "output" ? { ...current, enableSummary: checked } : current,
            )
          }
          aria-label="Write a summary"
        />
      </Field>
      {node.enableSummary ? (
        <>
          <SummaryModelField
            value={node.summaryModelProviderId}
            models={modelProviders}
            onChange={updateModel}
          />
          <NodeInstructions
            node={node}
            onChange={onChange}
            onBlur={commitHistoryGroup}
            rows={4}
            summary
          />
        </>
      ) : null}
    </>
  );
}

function SummaryModelField({
  value,
  models,
  onChange,
}: {
  value: string | null;
  models: ModelProviderDto[];
  onChange(value: string | null): void;
}) {
  return (
    <Field label="Summary model">
      <select
        className="h-9 rounded-md border border-input bg-background px-2"
        value={value ?? ""}
        onChange={(event) => onChange(event.target.value || null)}
      >
        <option value="">Select a model</option>
        {models.map((provider) => (
          <option key={provider.id} value={provider.id}>
            {provider.modelName}
          </option>
        ))}
      </select>
    </Field>
  );
}

type BlockFieldsProps = NodeFieldsProps & {
  node: Extract<AgentflowNode, { members: AgentflowBlockMember[] }>;
};
type HandoffNode = Extract<AgentflowNode, { kind: "handoffBlock" }>;

function BlockFields(props: BlockFieldsProps) {
  const { node, onChange, commitHistoryGroup } = props;
  const hasRounds = node.kind === "groupChatBlock" || node.kind === "magenticBlock";
  return (
    <>
      <NodeInstructions node={node} onChange={onChange} onBlur={commitHistoryGroup} rows={4} />
      {node.kind === "handoffBlock" ? <HandoffFields {...props} node={node} /> : null}
      {hasRounds ? (
        <OptionalNumberField
          label="Max rounds"
          value={node.maxRounds}
          onBlur={commitHistoryGroup}
          onChange={(maxRounds) =>
            onChange(
              (current) =>
                current.kind === "groupChatBlock" || current.kind === "magenticBlock"
                  ? { ...current, maxRounds }
                  : current,
              `rounds-${node.nodeId}`,
            )
          }
        />
      ) : null}
      {node.kind === "magenticBlock" ? <MagenticFields {...props} node={node} /> : null}
      <BlockMembers {...props} />
    </>
  );
}

function HandoffFields({
  node,
  onChange,
  commitHistoryGroup,
}: NodeFieldsProps & { node: HandoffNode }) {
  const patch = (fields: Partial<HandoffNode>, group?: string) =>
    onChange(
      (current) => (current.kind === "handoffBlock" ? { ...current, ...fields } : current),
      group,
    );
  return (
    <>
      <HandoffPrompt
        label="Handoff instructions"
        value={node.handoffInstructions}
        onBlur={commitHistoryGroup}
        onChange={(handoffInstructions) =>
          patch({ handoffInstructions }, `handoff-instructions-${node.nodeId}`)
        }
      />
      <Field label="Enable return to previous">
        <Switch
          checked={node.enableReturnToPrevious ?? false}
          onCheckedChange={(enableReturnToPrevious) => patch({ enableReturnToPrevious })}
        />
      </Field>
      <Field label="Autonomous">
        <Switch
          checked={node.autonomous ?? false}
          onCheckedChange={(autonomous) => patch({ autonomous })}
        />
      </Field>
      {node.autonomous ? (
        <OptionalNumberField
          label="Autonomous turn limit"
          value={node.autonomousTurnLimit}
          onChange={(autonomousTurnLimit) => patch({ autonomousTurnLimit })}
        />
      ) : null}
      <HandoffPrompt
        label="Continuation prompt"
        value={node.continuationPrompt}
        onBlur={commitHistoryGroup}
        onChange={(continuationPrompt) =>
          patch({ continuationPrompt }, `continuation-${node.nodeId}`)
        }
      />
    </>
  );
}

function HandoffPrompt({
  label,
  value,
  onChange,
  onBlur,
}: {
  label: string;
  value?: string | null;
  onChange(value: string | null): void;
  onBlur(): void;
}) {
  return (
    <Field label={label}>
      <Textarea
        value={value ?? ""}
        onChange={(event) => onChange(event.target.value || null)}
        onBlur={onBlur}
        rows={3}
      />
    </Field>
  );
}

type MagenticNode = Extract<AgentflowNode, { kind: "magenticBlock" }>;

function MagenticFields({
  node,
  onChange,
  commitHistoryGroup,
}: NodeFieldsProps & { node: MagenticNode }) {
  const patch = (fields: Partial<MagenticNode>, group?: string) =>
    onChange(
      (current) => (current.kind === "magenticBlock" ? { ...current, ...fields } : current),
      group,
    );
  return (
    <>
      <OptionalNumberField
        label="Max stalls"
        value={node.maxStalls}
        onBlur={commitHistoryGroup}
        onChange={(maxStalls) => patch({ maxStalls }, `stalls-${node.nodeId}`)}
      />
      <OptionalNumberField
        label="Max resets"
        value={node.maxResets}
        onBlur={commitHistoryGroup}
        onChange={(maxResets) => patch({ maxResets }, `resets-${node.nodeId}`)}
      />
      <Field label="Require plan signoff">
        <Switch
          checked={node.requirePlanSignoff ?? false}
          onCheckedChange={(requirePlanSignoff) => patch({ requirePlanSignoff })}
        />
      </Field>
    </>
  );
}

function OptionalNumberField({
  label,
  value,
  onChange,
  onBlur,
}: {
  label: string;
  value?: number | null;
  onChange(value: number | null): void;
  onBlur?(): void;
}) {
  return (
    <Field label={label}>
      <Input
        type="number"
        min={1}
        value={value ?? ""}
        onBlur={onBlur}
        onChange={(event) => onChange(readOptionalNumber(event.target.value))}
      />
    </Field>
  );
}

function BlockMembers({
  node,
  agents,
  agentflows,
  currentAgentflowId,
  onMemberSelect,
  onAddMember,
  onRemoveMember,
}: BlockFieldsProps) {
  return (
    <section className="space-y-1 border-t pt-3">
      <h3 className="text-xs font-semibold">Members</h3>
      {node.members.map((member) => (
        <BlockMemberRow
          key={member.memberId}
          member={member}
          select={() => onMemberSelect(member.memberId)}
          remove={() => onRemoveMember(member.memberId)}
        />
      ))}
      <AgentflowNodeMenu
        agents={agents}
        agentflows={agentflows}
        currentAgentflowId={currentAgentflowId}
        referencesOnly
        trigger={
          <Button type="button" variant="outline" size="sm">
            <Plus className="h-4 w-4" /> Add member
          </Button>
        }
        onSelect={(_kind, reference) => {
          if (reference) onAddMember(reference);
        }}
      />
    </section>
  );
}

function BlockMemberRow({
  member,
  select,
  remove,
}: {
  member: AgentflowBlockMember;
  select(): void;
  remove(): void;
}) {
  const Icon = member.kind === "agent" ? Bot : Workflow;
  return (
    <div className="flex items-center gap-1 rounded-md px-1 py-1 hover:bg-muted">
      <button
        type="button"
        className="flex min-w-0 flex-1 items-center gap-2 px-1 py-0.5 text-left text-xs"
        onClick={select}
      >
        <Icon className="h-3.5 w-3.5" />
        <span className="flex-1 truncate">{member.name}</span>
        {member.isManager ? <Crown className="h-3.5 w-3.5" /> : null}
      </button>
      <Button
        type="button"
        variant="ghost"
        size="icon-sm"
        aria-label={`Remove member ${member.name}`}
        onClick={remove}
      >
        <Trash2 className="h-3.5 w-3.5" />
      </Button>
    </div>
  );
}

function ReferenceSelect({
  kind,
  relateId,
  name,
  agents,
  agentflows,
  currentAgentflowId,
  onChange,
}: {
  kind: NodeReference["kind"];
  relateId: string | null;
  name: string | null | undefined;
  agents: AgentDto[];
  agentflows: AgentflowDto[];
  currentAgentflowId: string | null;
  onChange(relateId: string | null, syncedName?: string): void;
}) {
  const references = kind === "agent" ? agents : agentflows;
  const values = getReferenceOptions(kind, agents, agentflows, currentAgentflowId);
  return (
    <SearchableSelect
      id={`agentflow-reference-${kind}`}
      ariaLabel={kind === "agent" ? "Agent" : "Workflow"}
      value={relateId ?? ""}
      options={values}
      onValueChange={(value) => {
        if (!value) return onChange(null);
        const selected = values.find((option) => option.value === value);
        if (!selected) return;
        const previousName = references.find((reference) => reference.id === relateId)?.name;
        const shouldSyncName = !name?.trim() || name === previousName;
        onChange(value, shouldSyncName ? selected.title : undefined);
      }}
      placeholder={`Select ${kind === "agent" ? "an Agent" : "a Workflow"}...`}
      searchPlaceholder={`Search ${kind === "agent" ? "Agents" : "Workflows"}...`}
      clearable
    />
  );
}

function getReferenceOptions(
  kind: NodeReference["kind"],
  agents: AgentDto[],
  agentflows: AgentflowDto[],
  currentAgentflowId: string | null,
): SearchableSelectOption[] {
  const available =
    kind === "agent"
      ? agents.filter((agent) => agent.enable)
      : agentflows.filter((agentflow) => agentflow.id !== currentAgentflowId);
  return available.map(({ id, name, description }) => ({
    value: id,
    title: name,
    subtitle: description ?? "",
  }));
}

type OutgoingSectionProps = {
  node: AgentflowNode;
  edges: AgentflowEdge[];
  document: AgentflowEditorDocument;
  sourceProducesResult: boolean;
  responseSchema?: string | null;
  onChangeStrategy(strategy: AgentflowRoutingStrategy): void;
  onSelectEdge(edgeId: string): void;
  onRemoveEdge(edgeId: string): void;
  onMoveCase(edgeId: string, targetIndex: number): void;
  onSetDefault(edgeId: string): void;
  onClearDefault(edgeId: string): void;
};

function OutgoingSection(props: OutgoingSectionProps) {
  const { node, edges, document, onChangeStrategy, onSelectEdge, onRemoveEdge } = props;
  const isBranch = node.routingStrategy === "switch";
  const orderedEdges = isBranch
    ? getSwitchCases(edges, node.nodeId)
    : getRegularOutgoingEdges(edges, node.nodeId);
  const defaults = edges.filter((edge) => edge.isSwitchDefault && !edge.isFanInBarrier);
  return (
    <section className="space-y-3 border-t pt-3">
      <OutgoingHeading />
      <RoutingTabs strategy={node.routingStrategy} onChange={onChangeStrategy} />
      {edges.length === 0 ? <NoOutgoingConnections /> : null}
      {orderedEdges.map((edge, index) => (
        <RoutingEdge
          key={edge.edgeId}
          edge={edge}
          index={index}
          cases={orderedEdges}
          showElseAction={isBranch && defaults.length === 0}
          {...props}
        />
      ))}
      <WaitingOutgoingRows
        edges={edges}
        document={document}
        onSelectEdge={onSelectEdge}
        onRemoveEdge={onRemoveEdge}
      />
      {defaults.map((edge) => (
        <RoutingElse key={edge.edgeId} edge={edge} {...props} />
      ))}
    </section>
  );
}

function OutgoingHeading() {
  return (
    <h3 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
      Outgoing
    </h3>
  );
}

function RoutingTabs({
  strategy,
  onChange,
}: {
  strategy: AgentflowRoutingStrategy;
  onChange(strategy: AgentflowRoutingStrategy): void;
}) {
  return (
    <>
      <div className="grid grid-cols-3 gap-1">
        {ROUTING_STRATEGIES.map((option) => (
          <Button
            key={option}
            type="button"
            variant={strategy === option ? "default" : "outline"}
            size="sm"
            aria-pressed={strategy === option}
            onClick={() => onChange(option)}
          >
            {ROUTING_OPTIONS[option].label}
          </Button>
        ))}
      </div>
      <p className="text-xs text-muted-foreground">
        {ROUTING_OPTIONS[strategy === "choice" ? "direct" : strategy].description}
      </p>
    </>
  );
}

type RoutingEdgeProps = OutgoingSectionProps & {
  edge: AgentflowEdge;
  index: number;
  cases: AgentflowEdge[];
  showElseAction: boolean;
};

function RoutingEdge(props: RoutingEdgeProps) {
  const { edge, node, index, cases, showElseAction, document, onSelectEdge, onRemoveEdge } = props;
  const isBranch = node.routingStrategy === "switch";
  const moveHere = (id: string) => {
    if (id !== edge.edgeId && cases.some((item) => item.edgeId === id)) props.onMoveCase(id, index);
  };
  return (
    <div
      className="space-y-2 rounded-md border p-2"
      {...(isBranch ? reorderDropHandlers(BRANCH_DRAG_TYPE, moveHere) : {})}
    >
      <OutgoingEdgeLine
        edge={edge}
        target={edgeTargetName(document, edge)}
        onSelect={onSelectEdge}
        onRemove={onRemoveEdge}
        prefix={isBranch ? <BranchOrder {...props} /> : null}
      >
        {showElseAction ? (
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={() => props.onSetDefault(edge.edgeId)}
          >
            Use as Else
          </Button>
        ) : null}
      </OutgoingEdgeLine>
      <EdgeConditions
        edge={edge}
        sourceProducesResult={props.sourceProducesResult}
        responseSchema={props.responseSchema}
      />
    </div>
  );
}

function BranchOrder({
  edge,
  index,
  cases,
  onMoveCase,
}: Pick<RoutingEdgeProps, "edge" | "index" | "cases" | "onMoveCase">) {
  return (
    <>
      <ReorderHandle
        itemType={BRANCH_DRAG_TYPE}
        itemId={edge.edgeId}
        label="Drag branch to reorder"
      />
      <span className="shrink-0">{index === 0 ? "If" : "Else if"}</span>
      <ReorderButtons
        label="branch"
        index={index}
        count={cases.length}
        onMove={(target) => onMoveCase(edge.edgeId, target)}
      />
    </>
  );
}

function RoutingElse({
  edge,
  document,
  onSelectEdge,
  onRemoveEdge,
  onClearDefault,
}: OutgoingSectionProps & { edge: AgentflowEdge }) {
  return (
    <div className="space-y-1 rounded-md border p-2 text-xs">
      <OutgoingEdgeLine
        edge={edge}
        target={edgeTargetName(document, edge)}
        onSelect={onSelectEdge}
        onRemove={onRemoveEdge}
        prefix={<span>Else</span>}
      />
      <p className="text-muted-foreground">Runs when no branch above matches.</p>
      <Button type="button" variant="ghost" size="sm" onClick={() => onClearDefault(edge.edgeId)}>
        Give it a condition
      </Button>
    </div>
  );
}

function EdgeConditions({
  edge,
  sourceProducesResult,
  responseSchema,
}: {
  edge: AgentflowEdge;
  sourceProducesResult: boolean;
  responseSchema?: string | null;
}) {
  const updateDocument = useAgentflowEditorStore((state) => state.updateDocument);
  const commitHistoryGroup = useAgentflowEditorStore((state) => state.commitHistoryGroup);
  return (
    <AgentflowConditionEditor
      rules={edge.conditions}
      sourceProducesResult={sourceProducesResult}
      responseSchema={responseSchema}
      onChange={(conditions, textField) =>
        updateDocument(
          (current) => ({
            ...current,
            edges: current.edges.map((item) =>
              item.edgeId === edge.edgeId ? { ...item, conditions } : item,
            ),
          }),
          textField ? { group: `conditions-${edge.edgeId}-${textField}` } : "atomic",
        )
      }
      onCommitText={commitHistoryGroup}
    />
  );
}

type GateOutgoingProps = {
  node: HumanGateNode;
  edges: AgentflowEdge[];
  document: AgentflowEditorDocument;
  onSelectEdge(edgeId: string): void;
  onChangeEdge(edgeId: string, transform: (edge: AgentflowEdge) => AgentflowEdge): void;
  onRemoveEdge(edgeId: string): void;
};

function GateOutgoingSection(props: GateOutgoingProps) {
  const { node, edges } = props;
  const routedEdges = edges.filter((edge) => !edge.isFanInBarrier);
  return (
    <section className="space-y-3 border-t pt-3">
      <OutgoingHeading />
      {edges.length === 0 ? <NoOutgoingConnections /> : null}
      {node.choices.map((choice) => (
        <GateOutgoingPath
          key={choice.choiceId}
          {...props}
          choice={choice}
          edges={routedEdges.filter((edge) => edge.choiceId === choice.choiceId)}
        />
      ))}
      <WaitingOutgoingRows {...props} />
    </section>
  );
}

function GateOutgoingPath({
  choice,
  edges,
  node,
  document,
  onSelectEdge,
  onRemoveEdge,
  onChangeEdge,
}: GateOutgoingProps & { choice: GateChoice }) {
  return (
    <div className="space-y-2 rounded-md border p-2">
      <h4 className="text-xs font-medium">{choice.label}</h4>
      {edges.map((edge) => (
        <OutgoingEdgeLine
          key={edge.edgeId}
          edge={edge}
          target={edgeTargetName(document, edge)}
          onSelect={onSelectEdge}
          onRemove={onRemoveEdge}
        >
          <select
            aria-label="Choice path"
            className="h-8 max-w-28 rounded-md border bg-background px-1 text-xs"
            value={edge.choiceId ?? ""}
            onChange={(event) =>
              onChangeEdge(edge.edgeId, (current) => ({
                ...current,
                choiceId: event.target.value || null,
              }))
            }
          >
            {node.choices.map((item) => (
              <option key={item.choiceId} value={item.choiceId}>
                {item.label}
              </option>
            ))}
          </select>
        </OutgoingEdgeLine>
      ))}
      {edges.length === 0 ? (
        <p className="text-xs text-muted-foreground">This path ends here.</p>
      ) : null}
    </div>
  );
}

function NoOutgoingConnections() {
  return (
    <p className="text-xs text-muted-foreground">
      No connections yet. Drag from the dot on the right edge of the node.
    </p>
  );
}

function WaitingOutgoingRows({
  edges,
  document,
  onSelectEdge,
  onRemoveEdge,
}: {
  edges: AgentflowEdge[];
  document: AgentflowEditorDocument;
  onSelectEdge(edgeId: string): void;
  onRemoveEdge(edgeId: string): void;
}) {
  return edges
    .filter((edge) => edge.isFanInBarrier)
    .map((edge) => {
      const target = edgeTargetName(document, edge);
      return (
        <div key={edge.edgeId} className="space-y-1 rounded-md border p-2">
          <OutgoingEdgeLine
            edge={edge}
            target={target}
            onSelect={onSelectEdge}
            onRemove={onRemoveEdge}
            prefix={<span>Wait</span>}
          />
          <p className="text-xs text-muted-foreground">{target} waits for this node.</p>
        </div>
      );
    });
}

function OutgoingEdgeLine({
  edge,
  target,
  prefix,
  children,
  onSelect,
  onRemove,
}: React.PropsWithChildren<{
  edge: AgentflowEdge;
  target: string;
  prefix?: React.ReactNode;
  onSelect(edgeId: string): void;
  onRemove(edgeId: string): void;
}>) {
  return (
    <div className="flex items-center gap-1 text-xs">
      {prefix}
      <button
        type="button"
        className="min-w-0 flex-1 truncate text-left font-medium"
        onClick={() => onSelect(edge.edgeId)}
      >
        {target}
      </button>
      {children}
      <Button
        type="button"
        variant="ghost"
        size="icon-sm"
        aria-label="Remove connection"
        onClick={() => onRemove(edge.edgeId)}
      >
        <Trash2 className="h-3.5 w-3.5" />
      </Button>
    </div>
  );
}

function edgeTargetName(document: AgentflowEditorDocument, edge: AgentflowEdge): string {
  return document.nodes.find((node) => node.nodeId === edge.targetNodeId)?.name ?? "Missing node";
}

function IncomingSection({
  edges,
  document,
  onSelectNode,
  onToggle,
}: {
  edges: AgentflowEdge[];
  document: AgentflowEditorDocument;
  onSelectNode(nodeId: string): void;
  onToggle(edgeId: string, waiting: boolean): void;
}) {
  return (
    <section className="space-y-2 border-t pt-3">
      <h3 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
        Incoming
      </h3>
      {edges.map((edge) => {
        const sourceName =
          document.nodes.find((item) => item.nodeId === edge.sourceNodeId)?.name ?? "Missing node";
        return (
          <div key={edge.edgeId} className="flex items-center gap-2 rounded-md border p-2">
            <button
              type="button"
              className="min-w-0 flex-1 truncate text-left text-xs"
              onClick={() => onSelectNode(edge.sourceNodeId)}
            >
              {sourceName}
            </button>
            <Field label="Wait">
              <Switch
                checked={edge.isFanInBarrier}
                onCheckedChange={(checked) => onToggle(edge.edgeId, checked)}
                aria-label={`Wait for ${sourceName}`}
              />
            </Field>
          </div>
        );
      })}
      <p className="text-xs text-muted-foreground">
        With Wait on, this node starts only after every waited-for node has finished.
      </p>
    </section>
  );
}

function getEdgeSummary(
  edge: AgentflowEdge,
  source?: AgentflowNode,
  target?: AgentflowNode,
): string {
  if (edge.isFanInBarrier)
    return `${target?.name ?? "This node"} waits for ${source?.name ?? "the source"} to finish.`;
  if (source?.kind === "humanGate") {
    const choice = source.choices.find((item) => item.choiceId === edge.choiceId);
    return `${source.name ?? "Human Gate"} continues here when the answer is ${choice?.label ?? "this choice"}.`;
  }
  const routing = source?.routingStrategy ?? "direct";
  const strategy = ROUTING_OPTIONS[routing === "choice" ? "direct" : routing].label;
  return `${source?.name ?? "This node"} is set to ${strategy}.`;
}

function updateMember(
  updateDocument: (
    update: (document: AgentflowEditorDocument) => AgentflowEditorDocument,
    historyMode?: AgentflowEditorHistoryMode,
  ) => void,
  nodeId: string,
  memberId: string,
  patch: Partial<AgentflowBlockMember>,
  group?: string,
) {
  updateDocument(
    (document) => ({
      ...document,
      nodes: document.nodes.map((node) =>
        node.nodeId === nodeId && "members" in node
          ? {
              ...node,
              members: node.members.map((member) =>
                member.memberId === memberId ? { ...member, ...patch } : member,
              ),
            }
          : node,
      ),
    }),
    group ? { group } : "atomic",
  );
}

function moveItem<T>(items: T[], from: number, to: number): T[] {
  const next = [...items];
  const [item] = next.splice(from, 1);
  next.splice(to, 0, item);
  return next;
}

function ReorderHandle({
  itemType,
  itemId,
  label,
}: {
  itemType: string;
  itemId: string;
  label: string;
}) {
  return (
    <span
      draggable
      title={label}
      className="shrink-0 cursor-grab select-none active:cursor-grabbing"
      onDragStart={(event) => {
        event.dataTransfer.setData(itemType, itemId);
        event.dataTransfer.effectAllowed = "move";
      }}
    >
      <GripVertical className="h-4 w-4" aria-hidden />
    </span>
  );
}

function reorderDropHandlers(itemType: string, onMove: (itemId: string) => void) {
  return {
    onDragOver(event: React.DragEvent) {
      if (!event.dataTransfer.types.includes(itemType)) return;
      event.preventDefault();
      event.dataTransfer.dropEffect = "move";
    },
    onDrop(event: React.DragEvent) {
      const itemId = event.dataTransfer.getData(itemType);
      if (!itemId) return;
      event.preventDefault();
      onMove(itemId);
    },
  };
}

function readOptionalNumber(value: string): number | null {
  return value.trim() ? Math.max(1, Math.trunc(Number(value))) : null;
}
