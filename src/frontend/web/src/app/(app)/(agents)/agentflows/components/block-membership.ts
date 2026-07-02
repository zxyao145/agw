import type { Edge, Node } from "reactflow";

type MembershipNodeData = {
  kind: number;
  title?: string;
  configJson?: string | null;
};

export type BlockMemberView = {
  nodeId: string;
  title: string;
  kind: number | null;
  ownerBlockIds: string[];
  isHidden: boolean;
  isExternallyLinked: boolean;
  isShared: boolean;
  isMissing: boolean;
  isManager: boolean;
};

export type BlockMembership = {
  membersByBlockId: Map<string, BlockMemberView[]>;
  hiddenParticipantIds: Set<string>;
  externallyLinkedParticipantIds: Set<string>;
  sharedParticipantIds: Set<string>;
  participantOwnersByNodeId: Map<string, string[]>;
};

const NodeKind = {
  Agent: 0,
  WorkflowAsAgent: 1,
  ConcurrentBlock: 5,
  HandoffBlock: 6,
  GroupChatBlock: 7,
  MagenticBlock: 8,
} as const;

export function createBlockMembership<TNodeData extends MembershipNodeData>(
  nodes: Node<TNodeData>[],
  edges: Edge[],
): BlockMembership {
  const nodeById = new Map(nodes.map((node) => [node.id, node]));
  const edgeNodeIds = new Set<string>();
  edges.forEach((edge) => {
    edgeNodeIds.add(edge.source);
    edgeNodeIds.add(edge.target);
  });

  const participantOwnersByNodeId = new Map<string, string[]>();
  const blockParticipantIds = new Map<string, string[]>();
  const blockManagerIds = new Map<string, string>();

  nodes.forEach((node) => {
    if (!isBlockNodeKind(node.data.kind)) return;

    const config = readConfigJson(node.data.configJson || "") ?? {};
    const participantNodeIds = uniqueStrings(readStringArray(config.participantNodeIds));
    blockParticipantIds.set(node.id, participantNodeIds);

    const managerNodeId = readString(config.managerNodeId);
    if (managerNodeId) {
      blockManagerIds.set(node.id, managerNodeId);
    }

    participantNodeIds.forEach((participantNodeId) => {
      const owners = participantOwnersByNodeId.get(participantNodeId) ?? [];
      owners.push(node.id);
      participantOwnersByNodeId.set(participantNodeId, owners);
    });
  });

  const externallyLinkedParticipantIds = new Set<string>();
  const sharedParticipantIds = new Set<string>();
  const hiddenParticipantIds = new Set<string>();

  participantOwnersByNodeId.forEach((ownerBlockIds, participantNodeId) => {
    const participantNode = nodeById.get(participantNodeId);
    const isParticipantNode =
      participantNode !== undefined && isAgentParticipantKind(participantNode.data.kind);

    if (edgeNodeIds.has(participantNodeId)) {
      externallyLinkedParticipantIds.add(participantNodeId);
    }

    if (ownerBlockIds.length > 1) {
      sharedParticipantIds.add(participantNodeId);
    }

    if (isParticipantNode && ownerBlockIds.length === 1 && !edgeNodeIds.has(participantNodeId)) {
      hiddenParticipantIds.add(participantNodeId);
    }
  });

  const membersByBlockId = new Map<string, BlockMemberView[]>();
  blockParticipantIds.forEach((participantNodeIds, blockId) => {
    const managerNodeId = blockManagerIds.get(blockId);
    membersByBlockId.set(
      blockId,
      participantNodeIds.map((participantNodeId) => {
        const participantNode = nodeById.get(participantNodeId);
        const ownerBlockIds = participantOwnersByNodeId.get(participantNodeId) ?? [];
        return {
          nodeId: participantNodeId,
          title: participantNode?.data.title || participantNodeId,
          kind: participantNode?.data.kind ?? null,
          ownerBlockIds,
          isHidden: hiddenParticipantIds.has(participantNodeId),
          isExternallyLinked: externallyLinkedParticipantIds.has(participantNodeId),
          isShared: sharedParticipantIds.has(participantNodeId),
          isMissing:
            participantNode === undefined || !isAgentParticipantKind(participantNode.data.kind),
          isManager: managerNodeId === participantNodeId,
        };
      }),
    );
  });

  return {
    membersByBlockId,
    hiddenParticipantIds,
    externallyLinkedParticipantIds,
    sharedParticipantIds,
    participantOwnersByNodeId,
  };
}

export function getVisibleNodes<TNodeData>(
  nodes: Node<TNodeData>[],
  membership: BlockMembership,
): Node<TNodeData>[] {
  return nodes.filter((node) => !membership.hiddenParticipantIds.has(node.id));
}

export function getVisibleEdges<TEdgeData>(
  edges: Edge<TEdgeData>[],
  membership: BlockMembership,
): Edge<TEdgeData>[] {
  return edges.filter(
    (edge) =>
      !membership.hiddenParticipantIds.has(edge.source) &&
      !membership.hiddenParticipantIds.has(edge.target),
  );
}

export function updateBlockParticipantIds(currentJson: string, participantNodeIds: string[]) {
  const config = readConfigJson(currentJson) ?? {};
  const nextIds = uniqueStrings(participantNodeIds);
  const update: Record<string, unknown> = { participantNodeIds: nextIds };
  const managerNodeId = readString(config.managerNodeId);

  if (managerNodeId && !nextIds.includes(managerNodeId)) {
    update.managerNodeId = undefined;
  }

  return updateConfigJson(currentJson, update);
}

export function addBlockParticipantId(currentJson: string, participantNodeId: string) {
  const config = readConfigJson(currentJson) ?? {};
  return updateBlockParticipantIds(currentJson, [
    ...readStringArray(config.participantNodeIds),
    participantNodeId,
  ]);
}

export function removeBlockParticipantId(currentJson: string, participantNodeId: string) {
  const config = readConfigJson(currentJson) ?? {};
  return updateBlockParticipantIds(
    currentJson,
    readStringArray(config.participantNodeIds).filter((id) => id !== participantNodeId),
  );
}

export function readBlockParticipantIds(currentJson: string) {
  const config = readConfigJson(currentJson) ?? {};
  return uniqueStrings(readStringArray(config.participantNodeIds));
}

export function canDeleteBlockMember(membership: BlockMembership, participantNodeId: string) {
  return (
    !membership.externallyLinkedParticipantIds.has(participantNodeId) &&
    (membership.participantOwnersByNodeId.get(participantNodeId)?.length ?? 0) <= 1
  );
}

export function isAgentParticipantKind(kind: number) {
  return kind === NodeKind.Agent || kind === NodeKind.WorkflowAsAgent;
}

export function isBlockNodeKind(kind: number) {
  return (
    kind === NodeKind.ConcurrentBlock ||
    kind === NodeKind.HandoffBlock ||
    kind === NodeKind.GroupChatBlock ||
    kind === NodeKind.MagenticBlock
  );
}

function readConfigJson(value: string): Record<string, unknown> | null {
  if (!value.trim()) return {};

  try {
    const parsed = JSON.parse(value) as unknown;
    return isPlainObject(parsed) ? parsed : null;
  } catch {
    return null;
  }
}

function updateConfigJson(currentJson: string, update: Record<string, unknown>) {
  const config = readConfigJson(currentJson) ?? {};
  for (const [key, value] of Object.entries(update)) {
    if (shouldRemoveConfigValue(value)) {
      delete config[key];
    } else {
      config[key] = value;
    }
  }

  return Object.keys(config).length > 0 ? JSON.stringify(config, null, 2) : "";
}

function shouldRemoveConfigValue(value: unknown) {
  if (value === undefined || value === null) return true;
  if (typeof value === "string" && value.trim() === "") return true;
  if (Array.isArray(value) && value.length === 0) return true;
  return false;
}

function readString(value: unknown) {
  return typeof value === "string" ? value : "";
}

function readStringArray(value: unknown) {
  return Array.isArray(value)
    ? value.filter((item): item is string => typeof item === "string")
    : [];
}

function uniqueStrings(values: string[]) {
  return Array.from(new Set(values.filter((value) => value.trim().length > 0)));
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
