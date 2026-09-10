import type { AgwApiClient, AiMessage } from "@agw/api";
import {
  NativeWorkspaceProvider,
  type NativeVerifiedServer,
  useNativeWorkspace,
} from "@agw/chat-native/workspace";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react-native";
import React from "react";
import {
  NativeChatProvider,
  useNativeComposer,
  type NativeChatBindings,
} from "@agw/chat-native/provider";

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

test("programmatic composer submit preserves its draft until conversation restoration", async () => {
  const sendMessage = jest.fn(async () => undefined);
  let bindings = {
    selectedConversationId: "conversation-1",
    selectedContextId: null,
    isExecuting: false,
    sendMessage,
  } as unknown as NativeChatBindings;
  const wrapper = ({ children }: React.PropsWithChildren) => (
    <NativeChatProvider bindings={bindings}>{children}</NativeChatProvider>
  );
  const { result, rerender } = await renderHook(() => useNativeComposer(), { wrapper });
  await act(() => result.current.setText("Keep this draft"));
  await act(() => result.current.submit());
  expect(sendMessage).not.toHaveBeenCalled();
  expect(result.current.text).toBe("Keep this draft");

  bindings = { ...bindings, selectedContextId: "context-1" };
  await rerender(undefined);
  await act(() => result.current.submit());
  expect(sendMessage).toHaveBeenCalledWith("Keep this draft", []);
  expect(result.current.text).toBe("");
});

jest.mock("uuid", () => ({ v7: () => "01900000-0000-7000-8000-000000000001" }));
const mockExecutions: { conversationId: string; contextId: string }[] = [];
jest.mock("@agw/chat-native/execution", () => ({
  MobileExecutionSession: class {
    contextId = "";
    async configure(setting: { contextId: string }) {
      this.contextId = setting.contextId;
    }
    async execute(request: { conversationId: string }) {
      mockExecutions.push({ ...request, contextId: this.contextId });
    }
    async dispose() {}
    async setMode() {}
    async setPermissionMode() {}
  },
}));

test.each([false, true])(
  "unrestored mobile conversations cannot send until history succeeds (failed=%s)",
  async (failHistory) => {
    mockExecutions.length = 0;
    let historyFails = failHistory;
    let finishHistory!: () => void;
    const historyReady = new Promise<void>((resolve) => {
      finishHistory = resolve;
    });
    const apiGet = jest.fn(async (path: string) => {
      switch (path) {
        case "/api/projects":
          return [{ id: "project-1", name: "Agw" }];
        case "/api/agents":
          return [{ id: "agent-1", name: "ClaudeCode", displayName: "Claude Code" }];
        case "/api/agentflows":
          return [];
        case "/api/agents/permission-capabilities":
          return { supportedPermissionModes: ["fullAccess", "alwaysAsk", "allowSameArguments"] };
        case "/api/agents/suggestions":
          return { mode: "unsupported", suggestions: [] };
        case "/api/projects/{projectId}/conversations":
          return { items: [context], total: 1, pageIndex: 1, pageSize: 20 };
        case "/api/projects/{projectId}/conversations/{conversationId}":
          await historyReady;
          if (historyFails) throw new Error("History unavailable");
          return { ...context, usage: null, resumeState: null };
        case "/api/projects/{projectId}/conversations/{conversationId}/messages":
          await historyReady;
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
        <NativeWorkspaceProvider verifiedServer={verifiedServer}>
          {children}
        </NativeWorkspaceProvider>
      </QueryClientProvider>
    );
    const { result, unmount } = await renderHook(() => useNativeWorkspace(), { wrapper });

    try {
      await waitFor(() => expect(result.current.conversations).toHaveLength(1));
      await waitFor(() => expect(result.current.selectedTarget).not.toBeNull());
      await act(() => result.current.selectConversation(context.conversationId));
      await waitFor(() => expect(result.current.isChatLoading).toBe(true));
      if (failHistory) {
        await act(() => finishHistory());
        await waitFor(() => expect(result.current.error).toContain("History unavailable"));
      }
      expect(result.current.selectedContextId).toBeNull();
      await act(() => result.current.sendMessage("sent too early", []));
      expect(mockExecutions).toHaveLength(0);
      expect(result.current.selectedContextId).toBeNull();
      // Permission/mode controls must not manufacture a context that bypasses the send guard.
      await act(() => result.current.setPermissionMode("alwaysAsk"));
      await act(() => result.current.setAgentMode("plan"));
      await act(() => result.current.sendMessage("still too early", []));
      expect(mockExecutions).toHaveLength(0);
      expect(result.current.selectedContextId).toBeNull();
      historyFails = false;
      await act(async () => {
        finishHistory();
        if (failHistory)
          await queryClient.invalidateQueries({
            queryKey: ["mobile", "profile-1", "conversation"],
          });
      });
      await waitFor(() => expect(result.current.selectedContextId).toBe(context.contextId));
      await act(() => result.current.sendMessage("restored", []));
      expect(mockExecutions).toHaveLength(1);
      expect(mockExecutions[0].conversationId).toBe(context.conversationId);
      expect(mockExecutions[0].contextId).toBe(context.contextId);
    } finally {
      await unmount();
      queryClient.clear();
      finishHistory();
    }
  },
);
