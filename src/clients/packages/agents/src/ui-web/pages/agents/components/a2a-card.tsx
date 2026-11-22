import * as React from "react";
import { apiPost, getApiErrorMessage, type components } from "@agw/api";
import { toA2AEndpointRequest, type A2AEndpointDraft } from "./a2a-endpoint";
import type { A2AEndpointDto } from "./types";

type CardPreview = components["schemas"]["A2ACardPreviewResponse"];
export type CardAction = "fetch" | "refresh";

interface CardRequest {
  value: A2AEndpointDraft;
  agentId?: string;
  onPreview?: (name: string, description: string) => void;
  onRefresh?: (endpoint: A2AEndpointDto) => void;
}

export function useA2ACard({ value, agentId, onPreview, onRefresh }: CardRequest) {
  const [preview, setPreview] = React.useState<CardPreview | null>(null);
  const [pending, setPending] = React.useState<CardAction | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const request = React.useRef<AbortController | null>(null);
  React.useEffect(() => () => request.current?.abort(), []);

  const loadCard = async (action: CardAction) => {
    const controller = new AbortController();
    request.current = controller;
    setPending(action);
    setError(null);
    try {
      if (action === "refresh" && agentId) {
        const endpoint = await refreshCard(agentId, controller.signal);
        setPreview(null);
        onRefresh?.(endpoint);
      } else {
        const result = await fetchCardPreview(value, agentId, controller.signal);
        setPreview(result);
        const card = asRecord(result.card);
        onPreview?.(getText(card, "name"), getText(card, "description"));
      }
    } catch (failure) {
      if (!controller.signal.aborted) setError(getApiErrorMessage(failure));
    } finally {
      if (!controller.signal.aborted) setPending(null);
      if (request.current === controller) request.current = null;
    }
  };

  return { preview, pending, error, loadCard, setPreview, setError };
}

async function refreshCard(agentId: string, signal: AbortSignal) {
  const agent = await apiPost("/api/agents/a2a-card/refresh", { body: { agentId }, signal });
  if (!agent?.a2aEndpoint) throw new Error("The refreshed endpoint is missing from the response.");
  return agent.a2aEndpoint;
}

async function fetchCardPreview(
  draft: A2AEndpointDraft,
  agentId: string | undefined,
  signal: AbortSignal,
) {
  const result = await apiPost("/api/agents/a2a-card/preview", {
    body: { ...toA2AEndpointRequest(draft), agentId },
    signal,
  });
  if (!result) throw new Error("The Agent Card preview response is empty.");
  return result;
}

interface CardSummaryProps {
  card: CardPreview;
  fetchedAt?: string;
}

export function CardSummary({ card, fetchedAt }: CardSummaryProps) {
  const info = asRecord(card.card);
  const description = getText(info, "description");
  const skills = Array.isArray(info.skills) ? info.skills.map(asRecord) : [];
  return (
    <div className="grid gap-3 rounded-lg border bg-muted/20 p-4 text-sm">
      <div className="grid gap-1">
        <h3 className="font-medium">{getText(info, "name") || "Agent Card"}</h3>
        {description && <p className="whitespace-pre-wrap break-words">{description}</p>}
      </div>
      <CardDetails card={card} info={info} fetchedAt={fetchedAt} />
      {skills.length > 0 && <CardSkills skills={skills} />}
    </div>
  );
}

function CardDetails({
  card,
  info,
  fetchedAt,
}: CardSummaryProps & { info: Record<string, unknown> }) {
  const provider = asRecord(info.provider);
  const providerInfo = [getText(provider, "organization"), getText(provider, "url")]
    .filter(Boolean)
    .join(" · ");
  return (
    <dl className="grid grid-cols-[auto_minmax(0,1fr)] gap-x-3 gap-y-1">
      <dt className="text-muted-foreground">Version</dt>
      <dd>{getText(info, "version") || "—"}</dd>
      <dt className="text-muted-foreground">Provider</dt>
      <dd className="break-words">{providerInfo || "—"}</dd>
      <dt className="text-muted-foreground">Streaming</dt>
      <dd>{card.streaming ? "Supported" : "Unavailable"}</dd>
      <dt className="text-muted-foreground">Binding</dt>
      <dd>{card.protocolBinding}</dd>
      <dt className="text-muted-foreground">Interface</dt>
      <dd className="break-all font-mono text-xs">{card.interfaceUrl}</dd>
      {fetchedAt && (
        <>
          <dt className="text-muted-foreground">Last refreshed</dt>
          <dd>
            <time dateTime={fetchedAt}>{new Date(fetchedAt).toLocaleString()}</time>
          </dd>
        </>
      )}
    </dl>
  );
}

function CardSkills({ skills }: { skills: Record<string, unknown>[] }) {
  return (
    <div className="grid gap-1">
      <h4 className="font-medium">Skills</h4>
      <ul className="grid gap-1">
        {skills.map((skill, index) => {
          const description = getText(skill, "description");
          return (
            <li key={getText(skill, "id") || index} className="break-words">
              <span className="font-medium">{getText(skill, "name")}</span>
              {description && <p className="text-muted-foreground">{description}</p>}
            </li>
          );
        })}
      </ul>
    </div>
  );
}

function asRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === "object" ? (value as Record<string, unknown>) : {};
}

function getText(value: Record<string, unknown>, name: string): string {
  const field = value[name];
  return typeof field === "string" ? field : "";
}
