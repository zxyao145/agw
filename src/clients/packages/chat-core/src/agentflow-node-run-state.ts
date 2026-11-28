import type { AiMessage } from "@agw/api";
import {
  getTurnFinishedStatus,
  getTurnIdentity,
  getTurnTarget,
  isSupersededTurnMessage,
  type AgentflowNodeStatus,
  type AgentflowNodeStatusEvent,
  type ExecutionStatusEvent,
} from "@agw/execution-core";

export type AgentflowNodeRunState = {
  turnId: string | null;
  agentflowId: string | null;
  agentflowRevision: number | null;
  turnActive: boolean;
  statusByNodeId: Readonly<Record<string, "running" | "completed" | "failed">>;
  pendingInteractionIdsByNodeId: Readonly<Record<string, readonly string[]>>;
  activeMemberIdByNodeId: Readonly<Record<string, string>>;
};

export const EMPTY_AGENTFLOW_NODE_RUN_STATE: AgentflowNodeRunState = {
  turnId: null,
  agentflowId: null,
  agentflowRevision: null,
  turnActive: false,
  statusByNodeId: {},
  pendingInteractionIdsByNodeId: {},
  activeMemberIdByNodeId: {},
};

function getPendingInteractions(state: AgentflowNodeRunState, nodeId: string): readonly string[] {
  return Object.hasOwn(state.pendingInteractionIdsByNodeId, nodeId)
    ? state.pendingInteractionIdsByNodeId[nodeId]
    : [];
}

function applyNodeStatus(
  state: AgentflowNodeRunState,
  { nodeId, status, interactionId }: AgentflowNodeStatusEvent,
): AgentflowNodeRunState {
  const pending = getPendingInteractions(state, nodeId);
  if (status === "waiting") {
    if (!interactionId || pending.includes(interactionId)) return state;
    return {
      ...state,
      pendingInteractionIdsByNodeId: {
        ...state.pendingInteractionIdsByNodeId,
        [nodeId]: [...pending, interactionId],
      },
    };
  }

  let pendingInteractionIdsByNodeId = state.pendingInteractionIdsByNodeId;
  if (interactionId && pending.includes(interactionId)) {
    const nextPending = { ...pendingInteractionIdsByNodeId };
    const remaining = pending.filter((id) => id !== interactionId);
    if (remaining.length > 0) nextPending[nodeId] = remaining;
    else delete nextPending[nodeId];
    pendingInteractionIdsByNodeId = nextPending;
  }

  const statusByNodeId =
    state.statusByNodeId[nodeId] === status
      ? state.statusByNodeId
      : { ...state.statusByNodeId, [nodeId]: status };
  if (
    statusByNodeId === state.statusByNodeId &&
    pendingInteractionIdsByNodeId === state.pendingInteractionIdsByNodeId
  ) {
    return state;
  }
  return { ...state, statusByNodeId, pendingInteractionIdsByNodeId };
}

function recordsEqual<T>(
  left: Readonly<Record<string, T>>,
  right: Readonly<Record<string, T>>,
  equal: (left: T, right: T) => boolean,
): boolean {
  if (left === right) return true;
  const keys = Object.keys(left);
  return (
    keys.length === Object.keys(right).length &&
    keys.every((key) => Object.hasOwn(right, key) && equal(left[key], right[key]))
  );
}

/** 状态相同时返回原对象。Returns the original object when the state is unchanged. */
export function applyAgentflowNodeStatus(
  state: AgentflowNodeRunState,
  status: ExecutionStatusEvent,
): AgentflowNodeRunState {
  if (status.type === "agentflow-node-status") return applyNodeStatus(state, status);
  if (status.type !== "agentflow-node-snapshot") return state;

  const current =
    status.turnId === state.turnId
      ? state
      : {
          ...EMPTY_AGENTFLOW_NODE_RUN_STATE,
          turnId: status.turnId,
          agentflowId: status.agentflowId,
          agentflowRevision: status.agentflowRevision,
        };
  const next = status.statuses.reduce(
    applyNodeStatus,
    current.turnActive ? current : { ...current, turnActive: true },
  );
  if (
    current === state &&
    state.turnActive &&
    recordsEqual(state.statusByNodeId, next.statusByNodeId, (left, right) => left === right) &&
    recordsEqual(
      state.pendingInteractionIdsByNodeId,
      next.pendingInteractionIdsByNodeId,
      (left, right) => left.length === right.length && left.every((id) => right.includes(id)),
    )
  )
    return state;
  return next;
}

export function applyAgentflowNodeMessage(
  state: AgentflowNodeRunState,
  message: AiMessage,
): AgentflowNodeRunState {
  if (isSupersededTurnMessage(message)) return state;

  const target = getTurnTarget(message);
  if (target) {
    if (target.agentType === "agent") {
      return { ...EMPTY_AGENTFLOW_NODE_RUN_STATE, turnId: target.turnId };
    }
    return {
      ...EMPTY_AGENTFLOW_NODE_RUN_STATE,
      turnId: target.turnId,
      agentflowId: target.agentId,
      agentflowRevision: target.agentflowRevision,
      turnActive: true,
    };
  }

  if (getTurnFinishedStatus(message)) {
    const turnId = getTurnIdentity(message)?.turnId ?? message.additionalProperties?.turnId;
    if (typeof turnId === "string" && turnId.length > 0 && turnId !== state.turnId) return state;
    return finishAgentflowNodeRun(state);
  }

  const { nodeId, nodePath } = message.additionalProperties ?? {};
  if (typeof nodeId !== "string" || typeof nodePath !== "string") return state;
  const pathNodeId = nodePath.split("/").at(-1);
  const memberPrefix = `${nodeId}.`;
  if (!pathNodeId?.startsWith(memberPrefix)) return state;
  const memberId = pathNodeId.slice(memberPrefix.length).replace(/\.manager$/, "");
  if (!memberId || state.activeMemberIdByNodeId[nodeId] === memberId) return state;
  return {
    ...state,
    activeMemberIdByNodeId: { ...state.activeMemberIdByNodeId, [nodeId]: memberId },
  };
}

/** 结束节点运行状态。Finishes the node run state. */
export function finishAgentflowNodeRun(state: AgentflowNodeRunState): AgentflowNodeRunState {
  if (!state.turnActive) return state;
  return {
    ...state,
    turnActive: false,
    statusByNodeId: Object.fromEntries(
      Object.entries(state.statusByNodeId).filter(([, status]) => status !== "running"),
    ),
    pendingInteractionIdsByNodeId: {},
    activeMemberIdByNodeId: {},
  };
}

export function getAgentflowNodeStatus(
  state: AgentflowNodeRunState,
  nodeId: string,
): AgentflowNodeStatus | null {
  if (getPendingInteractions(state, nodeId).length > 0) return "waiting";
  return Object.hasOwn(state.statusByNodeId, nodeId) ? state.statusByNodeId[nodeId] : null;
}
