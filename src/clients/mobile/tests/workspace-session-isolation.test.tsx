jest.mock("uuid", () => ({ v7: () => "01900000-0000-7000-8000-000000000001" }));
import type { AgwApiClient } from "@agw/api";
import {
  NativeWorkspaceProvider,
  useNativeWorkspace,
  type NativeVerifiedServer,
} from "@agw/chat-native/workspace";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react-native";
import React from "react";

const mockDispose = jest.fn(async () => undefined);
jest.mock("@agw/chat-native/execution", () => ({
  MobileExecutionSession: class {
    async configure() {}
    async execute() {}
    dispose = mockDispose;
    async setMode() {}
    async setPermissionMode() {}
  },
}));

function client(name: string): AgwApiClient {
  return {
    apiGet: jest.fn(async (path: string) => {
      if (path === "/api/projects") return [{ id: "project-1", name }];
      if (path === "/api/agents") return [{ id: "agent-1", name: "Agent", displayName: "Agent" }];
      if (path === "/api/agentflows") return [];
      if (path === "/api/agents/permission-capabilities")
        return { supportedPermissionModes: ["fullAccess"] };
      if (path === "/api/agents/suggestions") return { mode: "unsupported", suggestions: [] };
      if (path.endsWith("/conversations"))
        return { items: [], total: 0, pageIndex: 1, pageSize: 20 };
      throw new Error(`Unexpected GET ${path}`);
    }),
  } as unknown as AgwApiClient;
}

test.each(["token", "serverUrl"] as const)(
  "changing the same profile %s retires cached data and its execution session",
  async (changed) => {
    mockDispose.mockClear();
    let server: NativeVerifiedServer = {
      profile: { id: "same-profile", serverUrl: "https://first.example" },
      client: client("First identity"),
      token: "agw_first",
    };
    const queryClient = new QueryClient({
      defaultOptions: { queries: { staleTime: Infinity, gcTime: Infinity, retry: false } },
    });
    const wrapper = ({ children }: React.PropsWithChildren) => (
      <QueryClientProvider client={queryClient}>
        <NativeWorkspaceProvider verifiedServer={server}>{children}</NativeWorkspaceProvider>
      </QueryClientProvider>
    );
    const { result, rerender, unmount } = await renderHook(() => useNativeWorkspace(), { wrapper });
    try {
      await waitFor(() => expect(result.current.selectedProject?.name).toBe("First identity"));
      await waitFor(() => expect(result.current.permissionUnavailable).toBeNull());
      await act(async () => result.current.sendMessage("first message", []));
      server = {
        profile: {
          ...server.profile,
          serverUrl: changed === "serverUrl" ? "https://second.example" : server.profile.serverUrl,
        },
        token: changed === "token" ? "agw_second" : server.token,
        client: client("Second identity"),
      };
      await rerender(undefined);
      await waitFor(() => expect(result.current.selectedProject?.name).toBe("Second identity"));
      expect(mockDispose).toHaveBeenCalled();
      expect(result.current.messages).toEqual([]);
      expect(result.current.isExecuting).toBe(false);
      expect(
        JSON.stringify(
          queryClient
            .getQueryCache()
            .getAll()
            .map((query) => query.state.data),
        ),
      ).not.toContain("First identity");
    } finally {
      await unmount();
      queryClient.clear();
    }
  },
);
