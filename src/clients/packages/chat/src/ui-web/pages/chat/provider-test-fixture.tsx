import type { TestContext } from "node:test";
import * as React from "react";
import { configureApiRuntime, getApiRuntime } from "@agw/api";
import { startApiServer, type ApiRoutes } from "@agw/test-harness";
import { AppRouterContext } from "next/dist/shared/lib/app-router-context.shared-runtime";
import { SearchParamsContext } from "next/dist/shared/lib/hooks-client-context.shared-runtime";
import Router from "next/router";
import { adaptForAppRouterInstance } from "next/dist/shared/lib/router/adapters";
import { QueryClient, QueryClientProvider } from "@agw/components/query";
import { ChatProjectProvider } from "./chat-project-provider";

export async function providerFixture(t: TestContext, routes: ApiRoutes = {}) {
  const server = await startApiServer({
    "GET /api/projects": [
      {
        id: "a",
        name: "A",
        workspace: "/a",
        additionalDirectories: [{ id: "d2", path: "/extra" }],
      },
      { id: "b", name: "B", workspace: "/b" },
    ],
    "GET /api/agents": [{ id: "agent-1", name: "hello", displayName: "Hello", enable: true }],
    "GET /api/agentflows": [],
    "GET /api/projects/conversation-activity": { items: [] },
    ...routes,
  });
  const previous = getApiRuntime();
  configureApiRuntime({ baseUrl: server.baseUrl, token: null });
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  const router = adaptForAppRouterInstance(Router);
  t.after(() => {
    client.clear();
    configureApiRuntime(previous);
  });
  function wrap(children: React.ReactNode, projectId = "a", activeConversationId?: string | null) {
    return (
      <QueryClientProvider client={client}>
        <AppRouterContext value={router}>
          <SearchParamsContext value={new URLSearchParams({ projectId })}>
            <ChatProjectProvider routeBasePath="/chat" activeConversationId={activeConversationId}>
              {children}
            </ChatProjectProvider>
          </SearchParamsContext>
        </AppRouterContext>
      </QueryClientProvider>
    );
  }
  return { server, client, wrap };
}
