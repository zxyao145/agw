import { render } from "@testing-library/react-native";
import React from "react";
import { StyleSheet } from "react-native";

import { FilesScreen } from "@/features/files/files-screen";
import { useWorkspace } from "@/features/workspace/workspace-provider";

jest.mock("expo-router", () => ({
  router: {
    push: jest.fn(),
  },
}));
jest.mock("lucide-react-native", () => {
  const Icon = () => null;
  return {
    ChevronDown: Icon,
    ChevronRight: Icon,
    File: Icon,
    FileCode2: Icon,
    Folder: Icon,
    FolderOpen: Icon,
    RefreshCw: Icon,
  };
});
jest.mock("@/features/workspace/workspace-provider", () => ({
  useWorkspace: jest.fn(),
}));

test("centers the native Changed switch within the files toolbar", async () => {
  jest.mocked(useWorkspace).mockReturnValue({
    filesService: null,
    selectedProjectId: null,
    selectedProject: { id: "project-1", name: "Agw" },
  } as never);

  const view = await render(<FilesScreen />);
  const changedSwitch = view.getByRole("switch");

  expect(StyleSheet.flatten(changedSwitch.props.style)).toMatchObject({ alignSelf: "center" });
});
