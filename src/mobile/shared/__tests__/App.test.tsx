import React from "react";
import renderer, { act } from "react-test-renderer";
import { encodeConfigBase64Url } from "../src/rn/config/agw-config";
import { readLocalConfig, writeLocalConfig } from "../src/rn/config/config-store";
import App from "../src/rn/App";

jest.mock("react-native-safe-area-context", () => {
  const React = require("react");

  return {
    SafeAreaProvider: ({ children }: { children: React.ReactNode }) =>
      React.createElement(React.Fragment, null, children),
    useSafeAreaInsets: () => ({ top: 0, right: 0, bottom: 0, left: 0 }),
  };
});

jest.mock("../src/rn/config/config-store", () => ({
  readLocalConfig: jest.fn(),
  writeLocalConfig: jest.fn(),
}));

(
  globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }
).IS_REACT_ACT_ENVIRONMENT = true;

const testConfig = {
  version: 1 as const,
  serverDomain: "http://localhost:5015",
  apiKey: "test-api-key",
};
const readLocalConfigMock = readLocalConfig as jest.MockedFunction<
  typeof readLocalConfig
>;
const writeLocalConfigMock = writeLocalConfig as jest.MockedFunction<
  typeof writeLocalConfig
>;
const fetchMock = jest.fn();

describe("App", () => {
  beforeEach(() => {
    readLocalConfigMock.mockResolvedValue(testConfig);
    writeLocalConfigMock.mockResolvedValue(undefined);
    fetchMock.mockImplementation(createAgwFetchMock());
    globalThis.fetch = fetchMock as unknown as typeof fetch;
  });

  afterEach(() => {
    jest.clearAllMocks();
  });

  it("renders the Agw chat page from native props", async () => {
    let tree: renderer.ReactTestRenderer | undefined;

    await act(async () => {
      tree = renderer.create(
        <App routeName="home" title="Home" source="SwiftUI" />
      );
    });
    await settleAsync();

    const output = collectText(tree?.toJSON());

    expect(output).toContain("Chat");
    expect(output).toContain("Files");
    expect(output).toContain("Backend response from task history.");
    expect(output).not.toContain("TODAY, OCT 24");
    expect(output).not.toContain("Just finished reviewing them.");
  });

  it("renders a fallback screen for an unknown route", async () => {
    let tree: renderer.ReactTestRenderer | undefined;

    await act(async () => {
      tree = renderer.create(<App routeName="missing" title="Missing" />);
    });

    expect(collectText(tree?.toJSON())).toContain("Unknown route: missing");
  });

  it("switches between chat and files tab states", async () => {
    let tree: renderer.ReactTestRenderer | undefined;

    await act(async () => {
      tree = renderer.create(
        <App routeName="home" title="Home" source="Android" />
      );
    });

    await settleAsync();

    expect(collectText(tree?.toJSON())).toContain(
      "Backend response from task history."
    );
    expect(collectText(tree?.toJSON())).not.toContain("Sarah is typing");

    const filesTab = tree!.root.findByProps({ testID: "agw-tab-files" });

    await act(async () => {
      filesTab.props.onPress();
    });
    await settleAsync();

    const output = collectText(tree?.toJSON());

    expect(output).toContain("src");
    expect(output).toContain("mobile-api.md");
    expect(output).not.toContain("Brand_Assets_Hero.png");
    expect(fetchMock).toHaveBeenCalledWith(
      "http://localhost:5015/api/files/list?path=D%3A%5Cwork%5Cmobile",
      expect.objectContaining({
        headers: { "X-API-Key": "test-api-key" },
        method: "GET",
      })
    );
  });

  it("opens and closes the history drawer on the same page", async () => {
    let tree: renderer.ReactTestRenderer | undefined;

    await act(async () => {
      tree = renderer.create(
        <App routeName="home" title="Home" source="SwiftUI" />
      );
    });

    const openDrawer = tree!.root.findByProps({ testID: "agw-open-drawer" });

    await act(async () => {
      openDrawer.props.onPress();
    });

    await settleAsync();

    expect(collectText(tree?.toJSON())).toContain("Mobile Workspace");
    expect(collectText(tree?.toJSON())).toContain("Mobile Agent");
    expect(collectText(tree?.toJSON())).toContain("Mobile API Chat");
    expect(collectText(tree?.toJSON())).toContain("Settings");

    const closeDrawer = tree!.root.findByProps({ testID: "agw-close-drawer" });

    await act(async () => {
      closeDrawer.props.onPress();
    });

    expect(collectText(tree?.toJSON())).not.toContain("Mobile API Chat");
  });

  it("renders drawer project and agent selectors from backend lists", async () => {
    let tree: renderer.ReactTestRenderer | undefined;

    await act(async () => {
      tree = renderer.create(<App routeName="home" title="Home" />);
    });
    await settleAsync();

    await act(async () => {
      tree!.root.findByProps({ testID: "agw-open-drawer" }).props.onPress();
    });

    await act(async () => {
      tree!.root.findByProps({ testID: "agw-project-selector" }).props.onPress();
    });

    expect(collectText(tree?.toJSON())).toContain("Mobile Workspace");
    expect(collectText(tree?.toJSON())).toContain("Backend Project Two");

    await act(async () => {
      tree!.root.findByProps({ testID: "agw-agent-selector" }).props.onPress();
    });

    expect(collectText(tree?.toJSON())).toContain("Mobile Agent");
    expect(collectText(tree?.toJSON())).toContain("Mobile Flow");
    expect(collectText(tree?.toJSON())).toContain("Backend Agent Two");

    await act(async () => {
      tree!.root.findByProps({ testID: "agw-project-selector" }).props.onPress();
    });

    await act(async () => {
      tree!.root
        .findByProps({ testID: "agw-project-option-project-2" })
        .props.onPress();
    });
    await settleAsync();

    expect(fetchMock).toHaveBeenCalledWith(
      "http://localhost:5015/api/projects/project-2/tasks",
      expect.objectContaining({
        headers: { "X-API-Key": "test-api-key" },
        method: "GET",
      })
    );
    expect(collectText(tree?.toJSON())).toContain("Project Two API Chat");
  });

  it("imports a Base64URL config when no local config exists", async () => {
    readLocalConfigMock.mockResolvedValueOnce(null);
    let tree: renderer.ReactTestRenderer | undefined;

    await act(async () => {
      tree = renderer.create(<App routeName="home" title="Home" />);
    });

    expect(collectText(tree?.toJSON())).toContain("Server Configuration");

    const input = tree!.root.findByProps({
      testID: "agw-config-import-input",
    });
    const importButtons = tree!.root.findAllByProps({
      testID: "agw-config-import-save",
    }).filter((node) => typeof node.props.onPress === "function");
    const importButton = importButtons[importButtons.length - 1];
    const encodedConfig = encodeConfigBase64Url({
      version: 1,
      serverDomain: "https://api.example.com/",
      apiKey: "imported-key",
    });

    await act(async () => {
      input.props.onChangeText(encodedConfig);
    });

    await act(async () => {
      importButton.props.onPress();
    });

    expect(writeLocalConfigMock).toHaveBeenCalledWith({
      version: 1,
      serverDomain: "https://api.example.com",
      apiKey: "imported-key",
    });
    await settleAsync();
    expect(collectText(tree?.toJSON())).toContain("Chat");
  });

  it("opens settings from the drawer and saves config changes", async () => {
    let tree: renderer.ReactTestRenderer | undefined;

    await act(async () => {
      tree = renderer.create(<App routeName="home" title="Home" />);
    });

    await act(async () => {
      tree!.root.findByProps({ testID: "agw-open-drawer" }).props.onPress();
    });

    await act(async () => {
      tree!.root.findByProps({ testID: "agw-open-settings" }).props.onPress();
    });

    expect(collectText(tree?.toJSON())).toContain("Local Configuration");

    await act(async () => {
      tree!.root
        .findByProps({ testID: "agw-settings-domain-input" })
        .props.onChangeText("https://mobile.example.com/");
      tree!.root
        .findByProps({ testID: "agw-settings-api-key-input" })
        .props.onChangeText("updated-key");
    });

    await act(async () => {
      tree!.root.findByProps({ testID: "agw-settings-save" }).props.onPress();
    });

    expect(writeLocalConfigMock).toHaveBeenLastCalledWith({
      version: 1,
      serverDomain: "https://mobile.example.com",
      apiKey: "updated-key",
    });
    expect(collectText(tree?.toJSON())).not.toContain("Local Configuration");
  });

  it("sends composer input through the execution API", async () => {
    let tree: renderer.ReactTestRenderer | undefined;

    await act(async () => {
      tree = renderer.create(<App routeName="home" title="Home" />);
    });
    await settleAsync();

    await act(async () => {
      tree!.root
        .findByProps({ testID: "agw-message-input" })
        .props.onChangeText("Run the mobile task");
    });

    await act(async () => {
      tree!.root.findByProps({ testID: "agw-send-message" }).props.onPress();
    });
    await settleAsync();

    const executionCall = fetchMock.mock.calls.find(([url]) =>
      String(url).includes("/api/executions/agent-2/execute")
    );

    expect(executionCall).toBeDefined();
    expect(executionCall?.[1]).toMatchObject({
      headers: {
        "Content-Type": "application/json",
        "X-API-Key": "test-api-key",
      },
      method: "POST",
    });
    expect(JSON.parse(String(executionCall?.[1].body))).toEqual({
      agentType: 0,
      input: "Run the mobile task",
      projectId: "project-1",
      taskId: "task-1",
    });
    expect(collectText(tree?.toJSON())).toContain(
      "Execution response from API."
    );
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

function createAgwFetchMock() {
  return async (input: RequestInfo | URL) => {
    const url = String(input);
    const pathname = new URL(url).pathname;

    if (pathname === "/api/projects") {
      return jsonResponse([
        {
          id: "project-1",
          name: "Mobile Workspace",
          workspace: "D:\\work\\mobile",
          enable: true,
          extraSetting: "{}",
        },
        {
          id: "project-2",
          name: "Backend Project Two",
          workspace: "D:\\work\\project-two",
          enable: true,
          extraSetting: "{}",
        },
      ]);
    }

    if (pathname === "/api/agents") {
      return jsonResponse([
        {
          id: "agent-1",
          displayName: "Mobile Agent",
          name: "mobile-agent",
        },
        {
          id: "agent-2",
          displayName: "Backend Agent Two",
          name: "backend-agent-two",
        },
      ]);
    }

    if (pathname === "/api/agentflows") {
      return jsonResponse([
        {
          id: "agentflow-1",
          name: "Mobile Flow",
          enable: true,
        },
      ]);
    }

    if (pathname === "/api/projects/project-1/tasks") {
      return jsonResponse([
        {
          id: "task-1",
          projectId: "project-1",
          contextId: "context-1",
          status: 2,
          title: "Mobile API Chat",
          createTime: "2026-05-22T10:00:00Z",
          updateTime: "2026-05-22T10:05:00Z",
        },
      ]);
    }

    if (pathname === "/api/projects/project-2/tasks") {
      return jsonResponse([
        {
          id: "task-2",
          projectId: "project-2",
          contextId: "context-2",
          status: 2,
          title: "Project Two API Chat",
          createTime: "2026-05-22T11:00:00Z",
          updateTime: "2026-05-22T11:05:00Z",
        },
      ]);
    }

    if (pathname === "/api/projects/project-2/tasks/task-2") {
      return jsonResponse({
        id: "task-2",
        projectId: "project-2",
        contextId: "context-2",
        status: 2,
        title: "Project Two API Chat",
        input: "Load project two.",
        createTime: "2026-05-22T11:00:00Z",
        updateTime: "2026-05-22T11:05:00Z",
        messageCount: 1,
        messages: [
          {
            messageId: "message-project-2",
            author: "Backend Agent Two",
            role: "assistant",
            contents: [
              {
                type: "TextContent",
                content: "Project two response from task history.",
              },
            ],
          },
        ],
      });
    }

    if (pathname === "/api/projects/project-1/tasks/task-1") {
      return jsonResponse({
        id: "task-1",
        projectId: "project-1",
        contextId: "context-1",
        status: 2,
        title: "Mobile API Chat",
        input: "Please summarize the API changes.",
        createTime: "2026-05-22T10:00:00Z",
        updateTime: "2026-05-22T10:05:00Z",
        messageCount: 2,
        messages: [
          {
            messageId: "message-user-1",
            author: "$agw",
            role: "user",
            contents: [
              {
                type: "TextContent",
                content: "Please summarize the API changes.",
              },
            ],
          },
          {
            messageId: "message-assistant-1",
            author: "Mobile Agent",
            role: "assistant",
            contents: [
              {
                type: "TextContent",
                content: "Backend response from task history.",
              },
            ],
          },
        ],
      });
    }

    if (pathname === "/api/files/list") {
      return jsonResponse({
        items: [
          {
            name: "src",
            path: "D:\\work\\mobile\\src",
            type: "directory",
            modifiedTime: "2026-05-22T08:00:00Z",
          },
          {
            name: "mobile-api.md",
            path: "D:\\work\\mobile\\mobile-api.md",
            type: "file",
            size: 1024,
            modifiedTime: "2026-05-22T09:00:00Z",
          },
        ],
      });
    }

    if (pathname === "/api/executions/agent-2/execute") {
      return jsonResponse({
        taskId: "task-1",
        messages: [
          {
            messageId: "message-user-2",
            author: "$agw",
            role: "user",
            contents: [
              {
                type: "TextContent",
                content: "Run the mobile task",
              },
            ],
          },
          {
            messageId: "message-assistant-2",
            author: "Mobile Agent",
            role: "assistant",
            contents: [
              {
                type: "TextContent",
                content: "Execution response from API.",
              },
            ],
          },
        ],
      });
    }

    return jsonResponse({ message: `Unhandled URL: ${url}` }, 404);
  };
}

function jsonResponse(body: unknown, status = 200): Response {
  const envelope =
    status >= 200 && status < 300
      ? { code: 2000000, title: "OK", data: body }
      : body;

  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: status === 200 ? "OK" : "Not Found",
    headers: {
      get: (name: string) =>
        name.toLowerCase() === "content-type" ? "application/json" : null,
    },
    json: async () => envelope,
    text: async () => JSON.stringify(envelope),
  } as unknown as Response;
}
