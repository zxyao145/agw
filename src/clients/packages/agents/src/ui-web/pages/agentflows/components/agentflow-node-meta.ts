import { AGENTFLOW_NODE_LABELS } from "@agw/chat-core";
import {
  ArrowLeftRight,
  Bot,
  Eraser,
  Flag,
  LogIn,
  LogOut,
  MessagesSquare,
  Network,
  PenLine,
  Split,
  UserCheck,
  Workflow,
  type LucideIcon,
} from "lucide-react";

import type {
  AgentDto,
  AgentflowDto,
  AgentflowNode,
  AgentflowNodeKind,
  ModelProviderDto,
} from "../../../../types/agentflow";

export type AgentflowNodeMeta = {
  kind: AgentflowNodeKind;
  label: string;
  description: string;
  icon: LucideIcon;
  group: "steps" | "blocks" | null;
};

export const AGENTFLOW_NODE_META: Record<AgentflowNodeKind, AgentflowNodeMeta> = {
  input: {
    kind: "input",
    label: AGENTFLOW_NODE_LABELS.input,
    description: "Starts the flow with the user's message.",
    icon: LogIn,
    group: null,
  },
  agent: {
    kind: "agent",
    label: AGENTFLOW_NODE_LABELS.agent,
    description: "Runs an Agent.",
    icon: Bot,
    group: null,
  },
  workflowAsAgent: {
    kind: "workflowAsAgent",
    label: AGENTFLOW_NODE_LABELS.workflowAsAgent,
    description: "Runs a published Agentflow.",
    icon: Workflow,
    group: null,
  },
  promptAdapter: {
    kind: "promptAdapter",
    label: AGENTFLOW_NODE_LABELS.promptAdapter,
    description: "Rewrites the input with instructions.",
    icon: PenLine,
    group: "steps",
  },
  clearMessages: {
    kind: "clearMessages",
    label: AGENTFLOW_NODE_LABELS.clearMessages,
    description: "Continues with empty input.",
    icon: Eraser,
    group: "steps",
  },
  humanGate: {
    kind: "humanGate",
    label: AGENTFLOW_NODE_LABELS.humanGate,
    description: "Waits for a person to choose a path.",
    icon: UserCheck,
    group: "steps",
  },
  checkpointMarker: {
    kind: "checkpointMarker",
    label: AGENTFLOW_NODE_LABELS.checkpointMarker,
    description: "Marks a point the run can resume from.",
    icon: Flag,
    group: "steps",
  },
  output: {
    kind: "output",
    label: AGENTFLOW_NODE_LABELS.output,
    description: "Returns the result of the flow.",
    icon: LogOut,
    group: "steps",
  },
  concurrentBlock: {
    kind: "concurrentBlock",
    label: AGENTFLOW_NODE_LABELS.concurrentBlock,
    description: "Runs every member at the same time.",
    icon: Split,
    group: "blocks",
  },
  handoffBlock: {
    kind: "handoffBlock",
    label: AGENTFLOW_NODE_LABELS.handoffBlock,
    description: "Members hand the task to each other.",
    icon: ArrowLeftRight,
    group: "blocks",
  },
  groupChatBlock: {
    kind: "groupChatBlock",
    label: AGENTFLOW_NODE_LABELS.groupChatBlock,
    description: "Members take turns in one conversation.",
    icon: MessagesSquare,
    group: "blocks",
  },
  magenticBlock: {
    kind: "magenticBlock",
    label: AGENTFLOW_NODE_LABELS.magenticBlock,
    description: "A manager plans and assigns work to members.",
    icon: Network,
    group: "blocks",
  },
};

export type AgentflowNodeLookups = {
  agents: AgentDto[];
  agentflows: AgentflowDto[];
  modelProviders: ModelProviderDto[];
};

export function describeNode(node: AgentflowNode, lookups: AgentflowNodeLookups): string {
  switch (node.kind) {
    case "input":
      return "User message starts the flow";
    case "agent": {
      const agent = lookups.agents.find((item) => item.id === node.relateId);
      if (!agent) return "No agent selected";
      if (agent.type === 1) return "External agent";
      return (
        lookups.modelProviders.find((provider) => provider.id === agent.modelProviderId)
          ?.modelName ?? "No model selected"
      );
    }
    case "workflowAsAgent": {
      const agentflow = lookups.agentflows.find((item) => item.id === node.relateId);
      return agentflow?.description || "Nested Agentflow";
    }
    case "promptAdapter":
      return node.instructions || "No instructions yet";
    case "clearMessages":
      return "Continues with empty input";
    case "humanGate":
      return node.choices.map((choice) => choice.label).join(" / ");
    case "checkpointMarker":
      return node.checkpointName || node.name || "Checkpoint";
    case "output": {
      if (!node.enableSummary) return "Returns the incoming messages";
      const provider = lookups.modelProviders.find(
        (item) => item.id === node.summaryModelProviderId,
      );
      return provider ? `Summary by ${provider.modelName}` : "Summary enabled";
    }
    case "concurrentBlock":
    case "handoffBlock":
      return `${node.members.length} members`;
    case "groupChatBlock":
    case "magenticBlock":
      return `${node.members.length} members${node.maxRounds === null ? "" : ` · max ${node.maxRounds} rounds`}`;
  }
}
