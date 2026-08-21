import { fireEvent, render } from "@testing-library/react-native";
import { router } from "expo-router";
import React from "react";
import { Alert } from "react-native";

import { useComposer } from "@/features/chat/composer-provider";
import { WorkspaceHeader } from "@/features/workspace/workspace-header";
import { useWorkspace } from "@/features/workspace/workspace-provider";
import { WorkspaceScreen } from "@/features/workspace/workspace-screen";

let mockChatMounts = 0;
let mockFilesMounts = 0;
let mockShellMounts = 0;
const mockChatScrollToTop = jest.fn();
const mockFilesScrollToTop = jest.fn();

jest.mock("expo-router", () => ({
  router: {
    push: jest.fn(),
    replace: jest.fn(),
  },
}));
jest.mock("lucide-react-native", () => {
  const Icon = () => null;
  return {
    Menu: Icon,
    MessageSquarePlus: Icon,
    MoreHorizontal: Icon,
  };
});
jest.mock("@/features/chat/composer-provider", () => ({
  useComposer: jest.fn(),
}));
jest.mock("@/features/workspace/workspace-provider", () => ({
  useWorkspace: jest.fn(),
}));
jest.mock("@/features/chat/chat-screen", () => {
  const mockReact = jest.requireActual<typeof import("react")>("react");
  const { Pressable, Text, View } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    ChatScreen: mockReact.forwardRef(function MockChatScreen(_, ref) {
      const [count, setCount] = mockReact.useState(0);
      mockReact.useEffect(() => {
        mockChatMounts += 1;
      }, []);
      mockReact.useImperativeHandle(ref, () => ({ scrollToTop: mockChatScrollToTop }), []);
      return mockReact.createElement(
        View,
        null,
        mockReact.createElement(Text, null, `Chat state ${count}`),
        mockReact.createElement(Pressable, {
          accessibilityLabel: "Increment chat",
          onPress: () => setCount(count + 1),
        }),
      );
    }),
  };
});
jest.mock("@/features/files/files-screen", () => {
  const mockReact = jest.requireActual<typeof import("react")>("react");
  const { Pressable, Text, View } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    FilesScreen: mockReact.forwardRef(function MockFilesScreen(_, ref) {
      const [count, setCount] = mockReact.useState(0);
      mockReact.useEffect(() => {
        mockFilesMounts += 1;
      }, []);
      mockReact.useImperativeHandle(ref, () => ({ scrollToTop: mockFilesScrollToTop }), []);
      return mockReact.createElement(
        View,
        null,
        mockReact.createElement(Text, null, `Files state ${count}`),
        mockReact.createElement(Pressable, {
          accessibilityLabel: "Increment files",
          onPress: () => setCount(count + 1),
        }),
      );
    }),
  };
});
jest.mock("@/features/workspace/workspace-shell", () => {
  const mockReact = jest.requireActual<typeof import("react")>("react");
  const { Pressable, Text, View } =
    jest.requireActual<typeof import("react-native")>("react-native");
  return {
    WorkspaceShell: ({
      active,
      children,
      onScrollToTop,
      onTabChange,
    }: {
      active: "chat" | "files";
      children: React.ReactNode;
      onScrollToTop(): void;
      onTabChange(tab: "chat" | "files"): void;
    }) => {
      mockReact.useEffect(() => {
        mockShellMounts += 1;
      }, []);
      return mockReact.createElement(
        View,
        null,
        mockReact.createElement(Text, null, `Active ${active}`),
        mockReact.createElement(Pressable, {
          accessibilityLabel: "Show chat",
          onPress: () => onTabChange("chat"),
        }),
        mockReact.createElement(Pressable, {
          accessibilityLabel: "Show files",
          onPress: () => onTabChange("files"),
        }),
        mockReact.createElement(Pressable, {
          accessibilityLabel: "Scroll to top",
          onPress: onScrollToTop,
        }),
        children,
      );
    },
  };
});

beforeEach(() => {
  jest.clearAllMocks();
  mockChatMounts = 0;
  mockFilesMounts = 0;
  mockShellMounts = 0;
  jest.mocked(useComposer).mockReturnValue({ openQuickText: jest.fn() } as never);
  jest.mocked(useWorkspace).mockReturnValue({
    newChat: jest.fn(),
    selectedContextId: null,
    isExecuting: false,
  } as never);
});

test("switches workspace content without remounting visited panes or the shell", async () => {
  const view = await render(<WorkspaceScreen initialTab="chat" />);

  expect(view.getByText("Active chat")).toBeTruthy();
  expect(mockChatMounts).toBe(1);
  expect(mockFilesMounts).toBe(0);
  expect(mockShellMounts).toBe(1);

  await fireEvent.press(view.getByLabelText("Show files"));
  await fireEvent.press(view.getByLabelText("Increment files"));
  expect(view.getByText("Files state 1")).toBeTruthy();
  expect(view.queryByRole("button", { name: "Increment chat" })).toBeNull();

  await fireEvent.press(view.getByLabelText("Show chat"));
  expect(view.queryByRole("button", { name: "Increment files" })).toBeNull();
  await fireEvent.press(view.getByLabelText("Show files"));

  expect(view.getByText("Files state 1")).toBeTruthy();
  expect(mockChatMounts).toBe(1);
  expect(mockFilesMounts).toBe(1);
  expect(mockShellMounts).toBe(1);

  await fireEvent.press(view.getByLabelText("Scroll to top"));
  expect(mockFilesScrollToTop).toHaveBeenCalledTimes(1);
  expect(mockChatScrollToTop).not.toHaveBeenCalled();
});

test("uses the route-provided initial tab without eagerly mounting the other pane", async () => {
  const view = await render(<WorkspaceScreen initialTab="files" />);

  expect(view.getByText("Active files")).toBeTruthy();
  expect(mockFilesMounts).toBe(1);
  expect(mockChatMounts).toBe(0);
});

test("workspace header switches tabs locally and new chat selects Chat", async () => {
  const newChat = jest.fn();
  const onTabChange = jest.fn();
  jest.mocked(useWorkspace).mockReturnValue({
    newChat,
    selectedContextId: null,
    isExecuting: false,
  } as never);

  const view = await render(
    <WorkspaceHeader
      active="files"
      safeTop={0}
      onScrollToTop={jest.fn()}
      onTabChange={onTabChange}
    />,
  );

  await fireEvent.press(view.getByRole("tab", { name: "Chat" }));
  await fireEvent.press(view.getByRole("tab", { name: "Files" }));
  await fireEvent.press(view.getByLabelText("New chat"));

  expect(onTabChange.mock.calls).toEqual([["chat"], ["files"], ["chat"]]);
  expect(newChat).toHaveBeenCalledTimes(1);
  expect(router.replace).not.toHaveBeenCalled();
});

test("new chat keeps the active tab when execution blocks the action", async () => {
  const onTabChange = jest.fn();
  jest.mocked(useWorkspace).mockReturnValue({
    newChat: () => {
      throw new Error("Stop the current execution first.");
    },
    selectedContextId: "context-1",
    isExecuting: true,
  } as never);
  const alert = jest.spyOn(Alert, "alert").mockImplementation(() => undefined);

  const view = await render(
    <WorkspaceHeader active="files" safeTop={0} onTabChange={onTabChange} />,
  );
  await fireEvent.press(view.getByLabelText("New chat"));

  expect(onTabChange).not.toHaveBeenCalled();
  expect(alert).toHaveBeenCalledWith("Execution in progress", "Stop the current execution first.");
  alert.mockRestore();
});
