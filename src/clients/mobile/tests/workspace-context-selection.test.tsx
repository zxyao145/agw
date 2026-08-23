import type { AgwApiClient, AiMessage } from "@agw/api";
import {
  NativeWorkspaceProvider,
  type NativeVerifiedServer,
  useNativeWorkspace,
} from "@agw/chat-native/workspace";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react-native";
import React from "react";

const context = {
  projectId: "project-1",
  conversationId: "11111111-1111-1111-1111-000000000001",
  contextId: "context-1",
  title: "Conversation",
  executionCount: 1,
  messageCount: 1,
  createTime: "2026-08-21T00:00:00Z",
};
const historyMessage: AiMessage = {
  messageId: "message-1",
  role: "user",
  contents: [{ type: "TextContent", content: "Keep this history" }],
};

test("selecting the active conversation again preserves its loaded history", async () => {
  const apiGet = jest.fn(async (path: string) => {
    switch (path) {
      case "/api/projects":
        return [{ id: "project-1", name: "Agw" }];
      case "/api/agents":
        return [{ id: "agent-1", name: "ClaudeCode", displayName: "Claude Code" }];
      case "/api/agentflows":
        return [];
      case "/api/agents/suggestions":
        return { mode: "unsupported", suggestions: [] };
      case "/api/projects/{projectId}/conversations":
        return [context];
      case "/api/projects/{projectId}/conversations/{conversationId}":
        return { ...context, usage: null, resumeState: null };
      case "/api/projects/{projectId}/conversations/{conversationId}/messages":
        return { items: [historyMessage], nextCursor: null, hasMore: false };
      default:
        throw new Error(`Unexpected GET ${path}`);
    }
  });
  const verifiedServer: NativeVerifiedServer = {
    profile: { id: "profile-1", serverUrl: "https://agw.example" },
    client: { apiGet } as unknown as AgwApiClient,
    token: "test-token",
  };
  const queryClient = new QueryClient({
    defaultOptions: { queries: { gcTime: Infinity, retry: false } },
  });
  const wrapper = ({ children }: React.PropsWithChildren) => (
    <QueryClientProvider client={queryClient}>
      <NativeWorkspaceProvider verifiedServer={verifiedServer}>{children}</NativeWorkspaceProvider>
    </QueryClientProvider>
  );
  const { result } = await renderHook(() => useNativeWorkspace(), { wrapper });

  await waitFor(() => expect(result.current.conversations).toHaveLength(1));
  await act(() => result.current.selectConversation(context.conversationId));
  await waitFor(() => expect(result.current.messages).toHaveLength(1));
  expect(result.current.selectedConversationId).toBe(context.conversationId);
  expect(result.current.selectedContextId).toBe(context.contextId);

  await act(() => result.current.selectConversation(context.conversationId));

  expect(result.current.messages).toHaveLength(1);
  expect(result.current.messages[0]).toMatchObject(historyMessage);
});
