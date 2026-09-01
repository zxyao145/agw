import { fireEvent, render } from "@testing-library/react-native";
import { router } from "expo-router";
import React from "react";

import { HistoryScreen } from "@/features/history/history-screen";
import { useWorkspace } from "@/features/workspace/workspace-provider";

jest.mock("expo-router", () => ({
  router: {
    back: jest.fn(),
    canGoBack: jest.fn(),
    push: jest.fn(),
    replace: jest.fn(),
  },
}));
jest.mock("lucide-react-native", () => {
  const Icon = () => null;
  return {
    ChevronDown: Icon,
    Pencil: Icon,
    RefreshCw: Icon,
    Settings: Icon,
    Trash2: Icon,
    X: Icon,
  };
});
jest.mock("react-native-safe-area-context", () => ({
  useSafeAreaInsets: () => ({ top: 0, right: 0, bottom: 0, left: 0 }),
}));
jest.mock("@/features/workspace/workspace-provider", () => ({
  useWorkspace: jest.fn(),
}));

const conversations = [
  {
    projectId: "project-1",
    conversationId: "11111111-1111-1111-1111-000000000001",
    contextId: "context-1",
    title: "Active conversation",
    executionCount: 1,
    messageCount: 2,
    createTime: "2026-08-21T00:00:00Z",
  },
  {
    projectId: "project-1",
    conversationId: "11111111-1111-1111-1111-000000000002",
    contextId: "context-2",
    title: "Inactive conversation",
    executionCount: 1,
    messageCount: 3,
    createTime: "2026-08-21T01:00:00Z",
  },
];

beforeEach(() => {
  jest.clearAllMocks();
  jest.mocked(useWorkspace).mockReturnValue({
    projects: [{ id: "project-1", name: "Agw" }],
    conversations,
    selectedProjectId: "project-1",
    selectedProject: { id: "project-1", name: "Agw" },
    selectedConversationId: "11111111-1111-1111-1111-000000000001",
    selectedContextId: "context-1",
    isExecuting: false,
    selectConversation: jest.fn(),
    selectProject: jest.fn(),
    refreshConversations: jest.fn(),
    renameConversation: jest.fn(),
    deleteConversation: jest.fn(),
  } as never);
});

describe("HistoryScreen conversation actions", () => {
  test("returns to Chat when history has no previous route", async () => {
    jest.mocked(router.canGoBack).mockReturnValue(false);
    const view = await render(<HistoryScreen />);

    await fireEvent.press(view.getByLabelText("Close history"));

    expect(router.back).not.toHaveBeenCalled();
    expect(router.replace).toHaveBeenCalledWith("/chat");
  });

  test("returns to the previous route when history was pushed", async () => {
    jest.mocked(router.canGoBack).mockReturnValue(true);
    const view = await render(<HistoryScreen />);

    await fireEvent.press(view.getByLabelText("Close history"));

    expect(router.back).toHaveBeenCalledTimes(1);
    expect(router.replace).not.toHaveBeenCalled();
  });

  test("shows rename and delete actions for inactive conversations", async () => {
    jest.mocked(useWorkspace).mockReturnValue({
      projects: [{ id: "project-1", name: "Agw" }],
      conversations,
      selectedProjectId: "project-1",
      selectedProject: { id: "project-1", name: "Agw" },
      selectedConversationId: "11111111-1111-1111-1111-000000000001",
      selectedContextId: "context-1",
      isExecuting: false,
      selectConversation: jest.fn(),
      selectProject: jest.fn(),
      refreshConversations: jest.fn(),
      renameConversation: jest.fn(),
      deleteConversation: jest.fn(),
    } as never);

    const view = await render(<HistoryScreen />);

    expect(view.getByLabelText("Rename Inactive conversation")).toBeTruthy();
    expect(view.getByLabelText("Delete Inactive conversation")).toBeTruthy();
  });

  test("opens the rename dialog without selecting the conversation", async () => {
    const selectConversation = jest.fn();
    jest.mocked(useWorkspace).mockReturnValue({
      projects: [{ id: "project-1", name: "Agw" }],
      conversations,
      selectedProjectId: "project-1",
      selectedProject: { id: "project-1", name: "Agw" },
      selectedConversationId: null,
      selectedContextId: null,
      isExecuting: false,
      selectConversation,
      selectProject: jest.fn(),
      refreshConversations: jest.fn(),
      renameConversation: jest.fn(),
      deleteConversation: jest.fn(),
    } as never);

    const view = await render(<HistoryScreen />);
    await fireEvent.press(view.getByLabelText("Rename Inactive conversation"), {
      stopPropagation: jest.fn(),
    });

    expect(view.getByText("Rename conversation")).toBeTruthy();
    expect(selectConversation).not.toHaveBeenCalled();
  });
});
