import type { AgwApiClient, components } from "@agw/api";
import * as browserClient from "@agw/api";

type ProviderSessionApiClient = Pick<AgwApiClient, "apiGet" | "apiPost">;

export type ProviderSessionRecord = components["schemas"]["ProviderSessionResponse"];

/**
 * 同一 Agent 与 External Agent 名称下的 provider session：最多一条生效记录，其余为已归档的历史。
 * Provider sessions of one Agent and External Agent name: at most one active record, the rest are archived history.
 */
export type ProviderSessionGroup = {
  key: string;
  agentId: string;
  externalAgentName: string;
  active: ProviderSessionRecord | null;
  history: ProviderSessionRecord[];
};

export type ProviderSessionTarget = {
  projectId: string;
  conversationId: string;
};

/**
 * 读取对话中已经保存的全部 provider session 记录。
 * Reads every provider session record saved for the conversation.
 */
export async function getProviderSessions(
  target: ProviderSessionTarget,
  signal?: AbortSignal,
  client: ProviderSessionApiClient = browserClient,
): Promise<ProviderSessionRecord[]> {
  const records = await client.apiGet("/api/projects/conversations/provider-sessions", {
    params: { query: { projectId: target.projectId, conversationId: target.conversationId } },
    signal,
  });
  if (!records) throw new Error("The provider session response is missing.");
  return records;
}

/**
 * 归档指定记录；该组下一次执行创建新的 provider session。
 * Archives the specified record; the group's next run starts a new provider session.
 */
export async function archiveProviderSession(
  target: ProviderSessionTarget & { bindingId: string },
  client: ProviderSessionApiClient = browserClient,
): Promise<void> {
  await client.apiPost("/api/projects/conversations/provider-sessions/archive", {
    body: {
      projectId: target.projectId,
      conversationId: target.conversationId,
      bindingId: target.bindingId,
    },
  });
}

function compareNewestFirst(left: ProviderSessionRecord, right: ProviderSessionRecord): number {
  const byTime = Date.parse(right.createTime) - Date.parse(left.createTime);
  if (byTime !== 0) return byTime;
  return right.id < left.id ? -1 : right.id > left.id ? 1 : 0;
}

/**
 * 按 (AgentId, ExternalAgentName) 分组；组内与历史按创建时间和记录 ID 倒序，最近有记录的组排在前面。
 * Groups by (AgentId, ExternalAgentName); records and history are newest first by creation time and record ID, and the group with the latest record comes first.
 */
export function groupProviderSessions(
  records: readonly ProviderSessionRecord[],
): ProviderSessionGroup[] {
  const groups = new Map<string, ProviderSessionRecord[]>();
  for (const record of records) {
    const key = JSON.stringify([record.agentId, record.externalAgentName]);
    const group = groups.get(key);
    if (group) group.push(record);
    else groups.set(key, [record]);
  }

  return [...groups.entries()]
    .map(([key, group]) => [key, [...group].sort(compareNewestFirst)] as const)
    .sort(([, left], [, right]) => compareNewestFirst(left[0], right[0]))
    .map(([key, ordered]) => {
      const active = ordered.find((record) => record.isActive) ?? null;
      return {
        key,
        agentId: ordered[0].agentId,
        externalAgentName: ordered[0].externalAgentName,
        active,
        history: ordered.filter((record) => record !== active),
      };
    });
}
