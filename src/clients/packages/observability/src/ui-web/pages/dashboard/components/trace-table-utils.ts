import type { paths } from "@agw/api";

export type TraceFilters = {
  projectId: string;
  contextId: string;
  agentflowId: string;
  fromUtc: string;
  toUtc: string;
};

export const EMPTY_TRACE_FILTERS: TraceFilters = {
  projectId: "",
  contextId: "",
  agentflowId: "",
  fromUtc: "",
  toUtc: "",
};

type TraceQuery = NonNullable<paths["/api/traces"]["get"]["parameters"]["query"]>;

export function buildTraceQuery(
  filters: TraceFilters,
  pageIndex: number,
  pageSize: number,
): TraceQuery {
  const projectId = filters.projectId.trim();
  const contextId = filters.contextId.trim();
  const agentflowId = filters.agentflowId.trim();

  return {
    ...(projectId ? { projectId } : {}),
    ...(contextId ? { contextId } : {}),
    ...(agentflowId ? { agentflowId } : {}),
    ...(filters.fromUtc ? { fromUtc: new Date(filters.fromUtc).toISOString() } : {}),
    ...(filters.toUtc ? { toUtc: new Date(filters.toUtc).toISOString() } : {}),
    pageIndex,
    pageSize,
  };
}

export function getPaginationMeta(total: number, pageIndex: number, pageSize: number) {
  const totalPages = Math.max(1, Math.ceil(total / pageSize));

  return {
    start: total === 0 ? 0 : (pageIndex - 1) * pageSize + 1,
    end: total === 0 ? 0 : Math.min(pageIndex * pageSize, total),
    totalPages,
    canGoPrevious: pageIndex > 1,
    canGoNext: pageIndex < totalPages,
  };
}

const TRACE_STATUS_LABELS = ["Succeeded", "Failed", "Cancelled", "Rejected"] as const;

export function getTraceStatusLabel(status: number): string {
  return TRACE_STATUS_LABELS[status] ?? `Unknown (${status})`;
}

const NODE_KIND_LABELS = [
  "Agent",
  "Workflow as Agent",
  "Prompt Adapter",
  "Human Gate",
  "Checkpoint Marker",
  "Concurrent Block",
  "Handoff Block",
  "Group Chat Block",
  "Magentic Block",
  "Output",
  "Input",
] as const;

export function getNodeKindLabel(kind: number): string {
  return NODE_KIND_LABELS[kind] ?? `Unknown (${kind})`;
}

export function extractTraceInputText(input: string): string {
  try {
    const messages: unknown = JSON.parse(input);
    if (!Array.isArray(messages)) return "—";

    const texts: string[] = [];
    for (const message of messages) {
      if (typeof message !== "object" || message === null) continue;

      const contents = (message as { contents?: unknown }).contents;
      if (!Array.isArray(contents)) continue;

      for (const content of contents) {
        if (typeof content !== "object" || content === null) continue;

        const text = (content as { text?: unknown }).text;
        if (typeof text === "string" && text.trim().length > 0) {
          texts.push(text);
        }
      }
    }

    return texts.length > 0 ? texts.join("\n") : "—";
  } catch {
    return "—";
  }
}

export function formatTraceStartTime(value: string): string {
  const trimmedValue = value.trim();
  const hasTimeZone = /(?:z|[+-]\d{2}:?\d{2})$/i.test(trimmedValue);
  const date = new Date(hasTimeZone ? trimmedValue : `${trimmedValue}Z`);
  if (Number.isNaN(date.getTime())) return "—";

  const pad = (component: number) => String(component).padStart(2, "0");

  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
}
