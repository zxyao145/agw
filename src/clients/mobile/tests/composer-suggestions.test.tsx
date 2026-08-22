import { act, fireEvent, render, waitFor } from "@testing-library/react-native";
import React from "react";

import { Composer } from "@/features/chat/composer";
import { useComposer } from "@/features/chat/composer-provider";
import { useWorkspace } from "@/features/workspace/workspace-provider";

jest.mock("expo-image", () => ({ Image: () => null }));
jest.mock("lucide-react-native", () => {
  const Icon = () => null;
  return {
    ArrowUp: Icon,
    ChevronDown: Icon,
    ImagePlus: Icon,
    Lightbulb: Icon,
    Plus: Icon,
    ShieldAlert: Icon,
    Sparkles: Icon,
    Square: Icon,
    Wrench: Icon,
    X: Icon,
  };
});
jest.mock("@/components/icon-button", () => ({ IconButton: () => null }));
jest.mock("@/features/chat/composer-provider", () => ({ useComposer: jest.fn() }));
jest.mock("@/features/workspace/workspace-provider", () => ({ useWorkspace: jest.fn() }));

const setTextCalls: string[] = [];
let initialText = "";
let workspaceState: Record<string, unknown>;

function createWorkspace(overrides: Record<string, unknown> = {}) {
  const selectedTarget = { id: "agent-1", label: "Agent", type: "agent" as const };
  const agentSuggestions = [
    { text: "/deploy", description: "Skill · Deploy", kind: "skill" as const },
    { text: "/debug", description: "Tool · Debug", kind: "tool" as const },
  ];
  return {
    selectedProjectId: "project-1",
    targets: [selectedTarget],
    selectedTarget,
    selectedTargetValue: "agent:agent-1",
    selectedContextId: "context-1",
    commandSource: { mode: "system" as const, suggestions: agentSuggestions },
    agentSuggestions,
    supportsAgentMode: false,
    isSuggestionsLoading: false,
    suggestionsError: null,
    isExecuting: false,
    permissionMode: "fullAccess",
    agentMode: "execute",
    filesService: { searchFiles: jest.fn(async () => ({ results: [] })) },
    pickImages: jest.fn(),
    stopExecution: jest.fn(),
    selectTarget: jest.fn(),
    setPermissionMode: jest.fn(),
    setAgentMode: jest.fn(),
    ...overrides,
  };
}

beforeEach(() => {
  jest.clearAllMocks();
  setTextCalls.length = 0;
  initialText = "";
  workspaceState = createWorkspace();
  jest.mocked(useWorkspace).mockImplementation(() => workspaceState as never);
  jest.mocked(useComposer).mockImplementation(() => {
    const [text, setTextState] = React.useState(initialText);
    const setText = (value: string) => {
      setTextCalls.push(value);
      setTextState(value);
    };
    return {
      text,
      attachments: [],
      error: null,
      quickTextOpen: false,
      setText,
      pickImages: async () => undefined,
      removeAttachment: jest.fn(),
      submit: async () => setText(""),
      openQuickText: jest.fn(),
      closeQuickText: jest.fn(),
      selectQuickText: setText,
    } as never;
  });
});

test("shows slash suggestions and replaces the trigger at the active caret", async () => {
  initialText = "Please /dep later";
  const view = await render(<Composer safeBottom={0} />);
  const input = view.getByLabelText("Message");

  await fireEvent(input, "selectionChange", {
    nativeEvent: { selection: { start: 11, end: 11 } },
  });

  const suggestion = await view.findByLabelText("Use suggestion /deploy");
  await fireEvent.press(suggestion);

  expect(setTextCalls.at(-1)).toBe("Please /deploy later");
  expect(view.getByLabelText("Message").props.value).toBe("Please /deploy later");
  expect(view.queryByLabelText("Suggestions")).toBeNull();
});

test("releases the one-shot suggestion selection when the user moves to the end", async () => {
  initialText = "Please /dep later";
  const view = await render(<Composer safeBottom={0} />);
  let input = view.getByLabelText("Message");

  await fireEvent(input, "selectionChange", {
    nativeEvent: { selection: { start: 11, end: 11 } },
  });
  await fireEvent.press(await view.findByLabelText("Use suggestion /deploy"));

  input = view.getByLabelText("Message");
  expect(input.props.selection).toEqual({ start: 15, end: 15 });
  await fireEvent(input, "selectionChange", {
    nativeEvent: { selection: { start: 20, end: 20 } },
  });

  expect(view.getByLabelText("Message").props.selection).toBeUndefined();
});

test("searches project files for an at trigger and applies the mapped result", async () => {
  const searchFiles = jest.fn(async () => ({
    results: [
      {
        relativePath: "src/app.ts",
        fullPath: "/workspace/src/app.ts",
        type: "file" as const,
      },
    ],
  }));
  workspaceState = createWorkspace({ filesService: { searchFiles } });
  const view = await render(<Composer safeBottom={0} />);
  const input = view.getByLabelText("Message");

  await fireEvent.changeText(input, "@src");
  await fireEvent(input, "selectionChange", {
    nativeEvent: { selection: { start: 4, end: 4 } },
  });

  const suggestion = await view.findByLabelText("Use suggestion @src/app.ts");
  expect(searchFiles).toHaveBeenCalledWith("project-1", "", "src", true);
  await fireEvent.press(suggestion);

  expect(setTextCalls.at(-1)).toBe("@src/app.ts ");
  expect(view.getByLabelText("Message").props.value).toBe("@src/app.ts ");
});

test("ignores a stale asynchronous file result", async () => {
  let resolveOld!: (value: { results: Array<Record<string, string>> }) => void;
  let resolveNew!: (value: { results: Array<Record<string, string>> }) => void;
  const oldResult = new Promise<{ results: Array<Record<string, string>> }>((resolve) => {
    resolveOld = resolve;
  });
  const newResult = new Promise<{ results: Array<Record<string, string>> }>((resolve) => {
    resolveNew = resolve;
  });
  const searchFiles = jest.fn((_: string, __: string, keyword: string) =>
    keyword === "old" ? oldResult : newResult,
  );
  workspaceState = createWorkspace({ filesService: { searchFiles } });
  const view = await render(<Composer safeBottom={0} />);
  const input = view.getByLabelText("Message");

  await fireEvent.changeText(input, "@old");
  await fireEvent(input, "selectionChange", {
    nativeEvent: { selection: { start: 4, end: 4 } },
  });
  await waitFor(() => expect(searchFiles).toHaveBeenCalledWith("project-1", "", "old", true));

  await fireEvent.changeText(input, "@new");
  await waitFor(() => expect(searchFiles).toHaveBeenCalledWith("project-1", "", "new", true));

  await act(async () => {
    resolveNew({
      results: [{ relativePath: "new.ts", fullPath: "/workspace/new.ts", type: "file" }],
    });
  });
  expect(await view.findByLabelText("Use suggestion @new.ts")).toBeTruthy();

  await act(async () => {
    resolveOld({
      results: [{ relativePath: "old.ts", fullPath: "/workspace/old.ts", type: "file" }],
    });
  });
  expect(view.queryByLabelText("Use suggestion @old.ts")).toBeNull();
  expect(view.getByLabelText("Use suggestion @new.ts")).toBeTruthy();
});

test("clears suggestions when execution starts", async () => {
  const view = await render(<Composer safeBottom={0} />);
  const input = view.getByLabelText("Message");
  await fireEvent.changeText(input, "/dep");
  await fireEvent(input, "selectionChange", {
    nativeEvent: { selection: { start: 4, end: 4 } },
  });
  expect(await view.findByLabelText("Use suggestion /deploy")).toBeTruthy();

  workspaceState = { ...workspaceState, isExecuting: true };
  await view.rerender(<Composer safeBottom={0} />);

  await waitFor(() => expect(view.queryByLabelText("Suggestions")).toBeNull());
});

test("clears suggestions after submit and when the context changes", async () => {
  const view = await render(<Composer safeBottom={0} />);
  const input = view.getByLabelText("Message");
  await fireEvent.changeText(input, "/dep");
  await fireEvent(input, "selectionChange", {
    nativeEvent: { selection: { start: 4, end: 4 } },
  });
  expect(await view.findByLabelText("Use suggestion /deploy")).toBeTruthy();

  await fireEvent.press(view.getByLabelText("Send message"));
  await waitFor(() => expect(view.queryByLabelText("Suggestions")).toBeNull());

  await fireEvent.changeText(input, "/dep");
  expect(await view.findByLabelText("Use suggestion /deploy")).toBeTruthy();
  workspaceState = { ...workspaceState, selectedContextId: "context-2" };
  await view.rerender(<Composer safeBottom={0} />);

  await waitFor(() => expect(view.queryByLabelText("Suggestions")).toBeNull());
});
