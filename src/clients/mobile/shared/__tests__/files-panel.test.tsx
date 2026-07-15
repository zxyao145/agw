import React from "react";
import { Alert } from "react-native";
import renderer, { act } from "react-test-renderer";
import { FilesPanel } from "../src/rn/pages/home/components/files-panel";
import type { AgwApiClient } from "../src/rn/api/agw-api-client";

(
  globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }
).IS_REACT_ACT_ENVIRONMENT = true;

describe("FilesPanel", () => {
  const projectId = "project-1";

  beforeEach(() => {
    jest.spyOn(Alert, "alert").mockImplementation(() => undefined);
  });

  afterEach(() => {
    jest.restoreAllMocks();
  });

  it("loads recursive diff files, builds a tree, and renders selected diff content", async () => {
    const client = createFilesClient({
      lists: {
        "|true|true": {
          items: [
            {
              gitStatus: "modified",
              name: "index.ts",
              path: "src/index.ts",
              type: "file",
            },
            {
              gitStatus: "added",
              name: "readme.md",
              path: "docs/readme.md",
              type: "file",
            },
          ],
        },
      },
      diffs: {
        "src/index.ts": {
          diff: [
            "diff --git a/src/index.ts b/src/index.ts",
            "--- a/src/index.ts",
            "+++ b/src/index.ts",
            "@@ -1 +1 @@",
            "-old value",
            "+new value",
          ].join("\n"),
          unchanged: false,
        },
      },
    });

    let tree: renderer.ReactTestRenderer | undefined;

    await act(async () => {
      tree = renderer.create(
        <FilesPanel apiClient={client} projectId={projectId} />
      );
    });
    await settleAsync();

    const output = collectText(tree?.toJSON());

    expect(output).toContain("File Explorer");
    expect(output).toContain("src");
    expect(output).toContain("index.ts");
    expect(output).toContain("M");
    expect(client.getJson).toHaveBeenCalledWith("/api/files/list", {
      query: {
        diff: true,
        path: "",
        projectId,
        recursive: true,
      },
    });

    await act(async () => {
      tree!.root
        .findByProps({ testID: "agw-file-node-src/index.ts" })
        .props.onPress();
    });
    await settleAsync();

    const selectedOutput = collectText(tree?.toJSON());

    expect(client.getJson).toHaveBeenCalledWith("/api/files/diff", {
      query: { path: "src/index.ts", projectId },
    });
    expect(
      (client.getJson as jest.Mock).mock.calls.filter(
        ([path]) => path === "/api/files/diff"
      )
    ).toHaveLength(1);
    expect(selectedOutput).toContain("Original");
    expect(selectedOutput).toContain("Modified");
    expect(selectedOutput).toContain("old value");
    expect(selectedOutput).toContain("new value");
  });

  it("switches to full file mode, reads file text, and stores line comments", async () => {
    const client = createFilesClient({
      lists: {
        "|true|true": {
          items: [
            {
              gitStatus: "modified",
              name: "index.ts",
              path: "src/index.ts",
              type: "file",
            },
          ],
        },
        "|false|true": {
          items: [
            {
              name: "index.ts",
              path: "src/index.ts",
              type: "file",
            },
          ],
        },
      },
      reads: {
        "src/index.ts": "line one\nline two",
      },
    });

    let tree: renderer.ReactTestRenderer | undefined;

    await act(async () => {
      tree = renderer.create(
        <FilesPanel apiClient={client} projectId={projectId} />
      );
    });
    await settleAsync();

    await act(async () => {
      tree!.root.findByProps({ testID: "agw-files-diff-switch" }).props.onValueChange(false);
    });
    await settleAsync();

    await act(async () => {
      tree!.root
        .findByProps({ testID: "agw-file-node-src/index.ts" })
        .props.onPress();
    });
    await settleAsync();

    expect(client.getText).toHaveBeenCalledWith("/api/files/read", {
      query: { path: "src/index.ts", projectId },
    });
    expect(collectText(tree?.toJSON())).toContain("line two");

    await act(async () => {
      tree!.root
        .findByProps({ testID: "agw-file-line-current-2" })
        .props.onPress();
    });
    await act(async () => {
      tree!.root
        .findByProps({ testID: "agw-comment-input-current-2" })
        .props.onChangeText("Check this line");
    });
    await act(async () => {
      tree!.root
        .findByProps({ testID: "agw-comment-save-current-2" })
        .props.onPress();
    });

    expect(collectText(tree?.toJSON())).toContain("Check this line");
  });

  it("uses long press actions for delete and reset, then refreshes the tree", async () => {
    const client = createFilesClient({
      deleteResult: { message: "Deleted file", success: true },
      lists: {
        "|true|true": {
          items: [
            {
              gitStatus: "modified",
              name: "index.ts",
              path: "src/index.ts",
              type: "file",
            },
          ],
        },
      },
      resetResult: { message: "Reset file", success: true },
    });

    let tree: renderer.ReactTestRenderer | undefined;

    await act(async () => {
      tree = renderer.create(
        <FilesPanel apiClient={client} projectId={projectId} />
      );
    });
    await settleAsync();

    await act(async () => {
      tree!.root
        .findByProps({ testID: "agw-file-node-src/index.ts" })
        .props.onLongPress();
    });

    expect(collectText(tree?.toJSON())).toContain("Delete");
    expect(collectText(tree?.toJSON())).toContain("Reset to HEAD");

    await act(async () => {
      tree!.root.findByProps({ testID: "agw-file-action-reset" }).props.onPress();
    });
    await settleAsync();

    expect(client.postJson).toHaveBeenCalledWith("/api/files/reset", undefined, {
      query: { path: "src/index.ts", projectId },
    });

    await act(async () => {
      tree!.root
        .findByProps({ testID: "agw-file-node-src/index.ts" })
        .props.onLongPress();
    });
    await act(async () => {
      tree!.root.findByProps({ testID: "agw-file-action-delete" }).props.onPress();
    });
    await settleAsync();

    expect(client.deleteJson).toHaveBeenCalledWith("/api/files/delete", {
      query: { path: "src/index.ts", projectId },
    });
    expect(client.getJson).toHaveBeenCalledTimes(3);
  });

  it("uses accordion headers to collapse explorer and preview panes", async () => {
    const client = createFilesClient({
      lists: {
        "|true|true": {
          items: [
            {
              gitStatus: "modified",
              name: "index.ts",
              path: "src/index.ts",
              type: "file",
            },
          ],
        },
      },
      diffs: {
        "src/index.ts": {
          diff: [
            "diff --git a/src/index.ts b/src/index.ts",
            "--- a/src/index.ts",
            "+++ b/src/index.ts",
            "@@ -1 +1 @@",
            "-old value",
            "+new value",
          ].join("\n"),
          unchanged: false,
        },
      },
    });

    let tree: renderer.ReactTestRenderer | undefined;

    await act(async () => {
      tree = renderer.create(
        <FilesPanel apiClient={client} projectId={projectId} />
      );
    });
    await settleAsync();

    expect(collectText(tree?.toJSON())).not.toContain("Hide Preview");
    expect(collectText(tree?.toJSON())).not.toContain("Show Preview");
    expect(collectText(tree?.toJSON())).not.toContain("Hide Explorer");
    expect(collectText(tree?.toJSON())).not.toContain("Show Explorer");

    await act(async () => {
      tree!.root.findByProps({ testID: "agw-files-preview-accordion" }).props.onPress();
    });

    let output = collectText(tree?.toJSON());

    expect(output).toContain("File Explorer");
    expect(output).toContain("index.ts");
    expect(output).toContain("File Preview");
    expect(output).not.toContain("Select a file to view its contents");
    expect(output).not.toContain("Show Preview");

    await act(async () => {
      tree!.root.findByProps({ testID: "agw-files-preview-accordion" }).props.onPress();
    });
    await act(async () => {
      tree!.root
        .findByProps({ testID: "agw-file-node-src/index.ts" })
        .props.onPress();
    });
    await settleAsync();

    await act(async () => {
      tree!.root.findByProps({ testID: "agw-files-explorer-accordion" }).props.onPress();
    });

    output = collectText(tree?.toJSON());

    expect(output).toContain("File Explorer");
    expect(output).toContain("Original");
    expect(output).toContain("Modified");
    expect(output).not.toContain("Refresh");
    expect(output).not.toContain("Show Explorer");

    await act(async () => {
      tree!.root.findByProps({ testID: "agw-files-explorer-accordion" }).props.onPress();
    });

    output = collectText(tree?.toJSON());

    expect(output).toContain("File Explorer");
    expect(output).toContain("Original");
  });
});

async function settleAsync(): Promise<void> {
  for (let index = 0; index < 8; index += 1) {
    await act(async () => {
      await Promise.resolve();
    });
  }
}

function collectText(
  node:
    | renderer.ReactTestRendererJSON
    | renderer.ReactTestRendererJSON[]
    | null
    | undefined
): string {
  if (!node) {
    return "";
  }

  if (Array.isArray(node)) {
    return node.map(collectText).join("");
  }

  return (node.children ?? [])
    .map((child) => (typeof child === "string" ? child : collectText(child)))
    .join("");
}

type FileClientData = {
  deleteResult?: { message: string; success: boolean };
  diffs?: Record<string, unknown>;
  lists: Record<string, unknown>;
  reads?: Record<string, string>;
  resetResult?: { message: string; success: boolean };
};

function createFilesClient(data: FileClientData): AgwApiClient {
  return {
    deleteJson: jest.fn(async (_path: string, options?: { query?: Record<string, unknown> }) => {
      if (options?.query?.path) {
        return data.deleteResult ?? { message: "Deleted", success: true };
      }

      throw new Error("Missing delete path");
    }),
    getJson: jest.fn(async (path: string, options?: { query?: Record<string, unknown> }) => {
      if (path === "/api/files/list") {
        const key = `${options?.query?.path}|${options?.query?.diff}|${options?.query?.recursive}`;
        return data.lists[key] ?? { items: [] };
      }

      if (path === "/api/files/diff") {
        const key = String(options?.query?.path ?? "");
        return data.diffs?.[key] ?? { diff: "", unchanged: true };
      }

      throw new Error(`Unhandled getJson path: ${path}`);
    }),
    getText: jest.fn(async (_path: string, options?: { query?: Record<string, unknown> }) => {
      const key = String(options?.query?.path ?? "");
      return data.reads?.[key] ?? "";
    }),
    postJson: jest.fn(async (_path: string, _body?: unknown, options?: { query?: Record<string, unknown> }) => {
      if (options?.query?.path) {
        return data.resetResult ?? { message: "Reset", success: true };
      }

      throw new Error("Missing reset path");
    }),
    putJson: jest.fn(),
  } as unknown as AgwApiClient;
}
