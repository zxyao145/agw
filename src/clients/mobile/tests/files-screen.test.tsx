import { fireEvent, render, waitFor } from "@testing-library/react-native";
import React from "react";
import { router } from "expo-router";
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

test("directory dropdown reloads files and preserves the selected root in preview links", async () => {
  const listFiles = jest
    .fn()
    .mockResolvedValue({ items: [{ name: "README.md", path: "README.md", type: "file" }] });
  const filesService = { listFiles };
  let project = {
    id: "project-1",
    name: "Agw",
    workspace: "/data/primary",
    additionalDirectories: [{ id: "extra", path: "/data/extra" }],
  };
  jest.mocked(useWorkspace).mockImplementation(() => {
    const [directoryId, selectDirectory] = React.useState<string | null>(null);
    return {
      filesService,
      selectedProjectId: project.id,
      selectedProject: project,
      selectedDirectoryId: directoryId,
      selectDirectory,
    } as never;
  });
  const view = await render(<FilesScreen />);
  await waitFor(() => expect(listFiles).toHaveBeenCalledWith("project-1", "", true, true, null));
  await fireEvent.press(view.getByLabelText("Select project directory"));
  await fireEvent.press(view.getByText("extra"));
  await waitFor(() =>
    expect(listFiles).toHaveBeenLastCalledWith("project-1", "", true, true, "extra"),
  );
  await fireEvent.press(view.getByText("README.md"));
  expect(router.push).toHaveBeenCalledWith(
    expect.objectContaining({
      params: expect.objectContaining({
        projectId: "project-1",
        directoryId: "extra",
        path: "README.md",
      }),
    }),
  );
  project = { ...project, additionalDirectories: [] };
  await view.rerender(<FilesScreen />);
  await waitFor(() =>
    expect(listFiles).toHaveBeenLastCalledWith("project-1", "", true, true, null),
  );
  expect(view.queryByLabelText("Select project directory")).toBeNull();
});
