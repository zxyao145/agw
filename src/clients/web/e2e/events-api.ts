import type { Page } from "@playwright/test";
import { fulfillApiData, fulfillShellRead } from "./web-session-api";

export const executionSourceId = "11111111-1111-4111-8111-111111111111";
export const timerSourceId = "22222222-2222-4222-8222-222222222222";
export const agentId = "33333333-3333-4333-8333-333333333333";
export const projectId = "44444444-4444-4444-8444-444444444444";

type Source = {
  id: string;
  name: string;
  kind: number;
  managedBy: string;
  managedKey?: string;
  ceSource: string;
  config: unknown;
  eventTypes: string[];
  enabled: boolean;
  hasSecret: boolean;
  version: number;
};

type Subscription = {
  id: string;
  name: string;
  enabled: boolean;
  sourceIds: string[];
  eventTypes: string[];
  filter: unknown;
  targetKind: number;
  agentId: string | null;
  projectId: string | null;
  permissionMode: number | null;
  instructions: string | null;
  managedBy: string;
  version: number;
};

const initialSources: Source[] = [
  {
    id: executionSourceId,
    name: "agw.execution",
    kind: 4,
    managedBy: "agw",
    ceSource: "/agw/executions",
    config: {},
    eventTypes: ["agw.execution.completed"],
    enabled: true,
    hasSecret: false,
    version: 1,
  },
  {
    id: timerSourceId,
    name: "agw.timer.job-1-20261114",
    kind: 0,
    managedBy: "job",
    managedKey: "job-1",
    ceSource: "/agw/jobs/job-1",
    config: {},
    eventTypes: ["job.timer.fired"],
    enabled: true,
    hasSecret: false,
    version: 1,
  },
];

type EventApiState = {
  sources: Source[];
  subscriptions: Subscription[];
  writes: { path: string; body: Record<string, unknown> }[];
};

export async function stubEventsApi(page: Page) {
  const state: EventApiState = {
    sources: initialSources.map((source) => ({ ...source })),
    subscriptions: [],
    writes: [],
  };
  await page.route("**/api/**", (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    if (request.method() === "POST") {
      const body = request.postDataJSON() as Record<string, unknown>;
      return fulfillApiData(route, writeEventFixture(state, path, body));
    }
    if (request.method() !== "GET")
      throw new Error(`Unexpected method: ${request.method()} ${path}`);
    return fulfillShellRead(route, path) ?? fulfillApiData(route, readEventFixture(state, url));
  });
  return { writes: state.writes };
}

function writeEventFixture(state: EventApiState, path: string, body: Record<string, unknown>) {
  state.writes.push({ path, body });
  if (path === "/api/events/sources/create") return createSource(state.sources, body);
  if (path === "/api/events/subscriptions/create") {
    const subscription = {
      ...(body as Omit<Subscription, "id" | "managedBy" | "version">),
      id: "66666666-6666-4666-8666-666666666666",
      managedBy: "user",
      version: 1,
    };
    state.subscriptions.push(subscription);
    return subscription;
  }
  throw new Error(`Unexpected mutation: POST ${path}`);
}

function createSource(sources: Source[], body: Record<string, unknown>) {
  const id = `55555555-5555-4555-8555-${String(sources.length + 1).padStart(12, "0")}`;
  const config = body.config as Record<string, unknown>;
  sources.push({
    id,
    name: String(body.name),
    kind: Number(body.kind),
    managedBy: "user",
    ceSource: `/agw/sources/${id}`,
    config,
    eventTypes:
      typeof config.fixedType === "string" ? [config.fixedType] : (config.eventTypes as string[]),
    enabled: true,
    hasSecret: true,
    version: 1,
  });
  return { id, version: 1, secret: body.kind === 1 ? "one-time-webhook-secret" : null };
}

function readEventFixture({ sources, subscriptions }: EventApiState, url: URL): unknown {
  const paged = <T>(items: T[]) => ({ items, total: items.length, pageIndex: 1, pageSize: 20 });
  const cursorPath = url.searchParams.get("cursorPath");
  const responses: Record<string, unknown> = {
    "/api/auth/oidc/providers": [],
    "/api/events/list": paged([]),
    "/api/events/sources/list": paged(sources),
    "/api/events/sources/options": sources.map(({ id, name, kind, managedBy, eventTypes }) => ({
      id,
      name,
      kind,
      managedBy,
      eventTypes,
    })),
    "/api/events/sources/providers": [],
    "/api/events/sources/http-poll-options": {
      maxPages: 4,
      cursorPathValid: cursorPath === null ? null : cursorPath === "$.next",
    },
    "/api/events/subscriptions/list": paged(subscriptions),
    "/api/events/deliveries/list": paged([]),
    "/api/events/schedules/list": paged([]),
    "/api/events/unpublished/list": paged([]),
    "/api/agents": [{ id: agentId, name: "Reviewer", displayName: "Reviewer" }],
    "/api/agentflows": [],
    "/api/projects": [{ id: projectId, name: "Review project" }],
    "/api/integrations/connections": [],
  };
  if (!Object.hasOwn(responses, url.pathname)) throw new Error(`Unexpected query: ${url.pathname}`);
  return responses[url.pathname];
}
