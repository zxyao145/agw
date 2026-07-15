import React from "react";
import renderer, { act } from "react-test-renderer";
import { encodeConfigBase64Url } from "../src/rn/config/agw-config";
import { readLocalConfig, writeLocalConfig } from "../src/rn/config/config-store";
import App from "../src/rn/App";
import { Composer } from "../src/rn/pages/home/components/composer";
import { styles } from "../src/rn/pages/home/components/styles";

class MockWebSocket {
  static readonly CONNECTING = 0;
  static readonly OPEN = 1;
  static readonly CLOSING = 2;
  static readonly CLOSED = 3;
  static instances: MockWebSocket[] = [];

  public static reset(): void {
    this.instances = [];
  }

  public url: string;
  public readyState = MockWebSocket.CONNECTING;
  public sentData: string[] = [];
  public onclose: ((event: { code: number; reason: string }) => void) | null = null;
  public onerror: ((event: unknown) => void) | null = null;
  public onmessage: ((event: { data: string }) => void) | null = null;
  public onopen: ((event: { target: MockWebSocket }) => void) | null = null;

  public constructor(url: string) {
    this.url = url;
    MockWebSocket.instances.push(this);
    setTimeout(() => {
      this.readyState = MockWebSocket.OPEN;
      this.onopen?.({ target: this });
    }, 0);
  }

  public send(data: string): void {
    this.sentData.push(data);
  }

  public emitMessage(data: string): void {
    if (this.onmessage) {
      this.onmessage({ data });
    }
  }

  public close(code = 1000, reason = ""): void {
    this.readyState = MockWebSocket.CLOSING;
    this.onclose?.({ code, reason });
    this.readyState = MockWebSocket.CLOSED;
  }
}

function getLatestWebSocket(): MockWebSocket | undefined {
  return MockWebSocket.instances[MockWebSocket.instances.length - 1];
}

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

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const testConfig = {
  version: 2 as const,
  apiMajorVersion: 1 as const,
  serverUrl: "http://localhost:5015",
  token: "test-api-key",
};
const readLocalConfigMock = readLocalConfig as jest.MockedFunction<typeof readLocalConfig>;
const writeLocalConfigMock = writeLocalConfig as jest.MockedFunction<typeof writeLocalConfig>;
const fetchMock = jest.fn();

describe("App", () => {
  beforeEach(() => {
    readLocalConfigMock.mockResolvedValue(testConfig);
    writeLocalConfigMock.mockResolvedValue(undefined);
    fetchMock.mockImplementation(createAgwFetchMock());
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    globalThis.WebSocket = MockWebSocket as unknown as typeof WebSocket;
    MockWebSocket.reset();
  });

  afterEach(() => {
    jest.clearAllMocks();
  });

  it("renders the Agw chat page from native props", async () => {
    let tree: renderer.ReactTestRenderer | undefined;

    await act(async () => {
      tree = renderer.create(<App routeName="home" title="Home" source="SwiftUI" />);
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
      tree = renderer.create(<App routeName="home" title="Home" source="Android" />);
    });

    await settleAsync();

    expect(collectText(tree?.toJSON())).toContain("Backend response from task history.");
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
      "http://localhost:5015/api/files/list?diff=true&path=&projectId=project-1&recursive=true",
      expect.objectContaining({
        headers: { Authorization: "Bearer test-api-key" },
        method: "GET",
      }),
    );
  });

  it("opens and closes the history drawer on the same page", async () => {
    let tree: renderer.ReactTestRenderer | undefined;

    await act(async () => {
      tree = renderer.create(<App routeName="home" title="Home" source="SwiftUI" />);
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
      tree!.root.findByProps({ testID: "agw-project-option-project-2" }).props.onPress();
    });
    await settleAsync();

    expect(fetchMock).toHaveBeenCalledWith(
      "http://localhost:5015/api/projects/project-2/contexts",
      expect.objectContaining({
        headers: { Authorization: "Bearer test-api-key" },
        method: "GET",
      }),
    );
    expect(collectText(tree?.toJSON())).toContain("Project Two API Chat");
  });

  it("starts a websocket execution stream for default selections", async () => {
    fetchMock.mockImplementation(createAgwFetchMock({ includeDefaultSelections: true }));
    let tree: renderer.ReactTestRenderer | undefined;

    await act(async () => {
      tree = renderer.create(<App routeName="home" title="Home" />);
    });
    await settleAsync();

    await act(async () => {
      tree!.root.findByProps({ testID: "agw-open-drawer" }).props.onPress();
    });

    expect(collectText(tree?.toJSON())).toContain("Default Built In Chat");
    expect(collectText(tree?.toJSON())).toContain("Default Built In");
    expect(collectText(tree?.toJSON())).toContain("Hello");

    await act(async () => {
      tree!.root
        .findByProps({ testID: "agw-message-input" })
        .props.onChangeText("Use the defaults");
    });

    await act(async () => {
      tree!.root.findByProps({ testID: "agw-send-message" }).props.onPress();
    });
    await settleAsync();

    const ws = getLatestWebSocket();

    expect(ws).toBeDefined();

    const sentPayloads = ws!.sentData.map((payload) => JSON.parse(payload));
    expect(sentPayloads[0]).toMatchObject({
      type: "SettingCommand",
      projectId: "default-built-in",
      taskId: "task-default",
    });
    expect(sentPayloads[1]).toMatchObject({
      type: "ExecCommand",
      agentType: 0,
    });

    await act(async () => {
      ws!.emitMessage(
        JSON.stringify({
          messageId: "hello-assistant",
          author: "Hello",
          role: "assistant",
          contents: [
            {
              type: "TextContent",
              content: "Hello from stream.",
            },
          ],
        }),
      );
    });
    await act(async () => {
      ws!.emitMessage(
        JSON.stringify({
          messageId: "hello-system",
          author: "$agw-server",
          role: "system",
          contents: [
            {
              type: "TextContent",
              content: "Execution done.",
              additionalProperties: {
                type: "turn-finished",
              },
            },
          ],
        }),
      );
    });

    await settleAsync();
    expect(collectText(tree?.toJSON())).toContain("Hello from stream.");
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
    const importButtons = tree!.root
      .findAllByProps({
        testID: "agw-config-import-save",
      })
      .filter((node) => typeof node.props.onPress === "function");
    const importButton = importButtons[importButtons.length - 1];
    const encodedConfig = encodeConfigBase64Url({
      version: 2,
      apiMajorVersion: 1 as const,
      serverUrl: "https://api.example.com/",
      token: "imported-key",
    });

    await act(async () => {
      input.props.onChangeText(encodedConfig);
    });

    await act(async () => {
      importButton.props.onPress();
    });

    expect(writeLocalConfigMock).toHaveBeenCalledWith({
      version: 2,
      apiMajorVersion: 1 as const,
      serverUrl: "https://api.example.com",
      token: "imported-key",
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
    expect(tree!.root.findAllByProps({ testID: "agw-settings-page" }).length).toBeGreaterThan(0);
    expect(tree!.root.findAllByProps({ testID: "agw-settings-sheet" })).toHaveLength(0);
    expect(collectText(tree?.toJSON())).not.toContain("Mobile API Chat");
    expect(getSettingsActionBottomPadding(tree!)).toBeGreaterThanOrEqual(24);

    await act(async () => {
      tree!.root
        .findByProps({ testID: "agw-settings-domain-input" })
        .props.onChangeText("https://mobile.example.com/");
      tree!.root
        .findByProps({ testID: "agw-settings-token-input" })
        .props.onChangeText("updated-key");
    });

    await act(async () => {
      tree!.root.findByProps({ testID: "agw-settings-save" }).props.onPress();
    });

    expect(writeLocalConfigMock).toHaveBeenLastCalledWith({
      version: 2,
      apiMajorVersion: 1 as const,
      serverUrl: "https://mobile.example.com",
      token: "updated-key",
    });
    expect(collectText(tree?.toJSON())).not.toContain("Local Configuration");
    expect(collectText(tree?.toJSON())).toContain("Mobile API Chat");
  });

  it("returns to the history drawer from the settings secondary page", async () => {
    let tree: renderer.ReactTestRenderer | undefined;

    await act(async () => {
      tree = renderer.create(<App routeName="home" title="Home" />);
    });
    await settleAsync();

    await act(async () => {
      tree!.root.findByProps({ testID: "agw-open-drawer" }).props.onPress();
    });

    await act(async () => {
      tree!.root.findByProps({ testID: "agw-open-settings" }).props.onPress();
    });

    expect(collectText(tree?.toJSON())).toContain("Local Configuration");
    expect(collectText(tree?.toJSON())).not.toContain("Mobile API Chat");

    await act(async () => {
      tree!.root.findByProps({ testID: "agw-settings-back" }).props.onPress();
    });

    const output = collectText(tree?.toJSON());

    expect(output).not.toContain("Local Configuration");
    expect(output).toContain("Mobile API Chat");
    expect(output).toContain("Settings");
  });

  it("sends composer input through websocket stream", async () => {
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
    const ws = getLatestWebSocket();

    expect(ws).toBeDefined();
    expect(ws!.sentData[0]).toBeDefined();
    expect(ws!.sentData[1]).toBeDefined();

    const settingCommand = JSON.parse(ws!.sentData[0]);
    const execCommand = JSON.parse(ws!.sentData[1]);

    expect(settingCommand).toMatchObject({
      type: "SettingCommand",
      projectId: "project-1",
      taskId: "task-1",
      settingContent: "{}",
    });
    expect(execCommand).toMatchObject({
      type: "ExecCommand",
      agentType: 0,
      input: {
        messageId: expect.any(String),
        author: "$agw",
        contents: [{ type: "TextContent", content: "Run the mobile task" }],
      },
    });

    await act(async () => {
      ws!.emitMessage(
        JSON.stringify({
          messageId: "assistant-stream",
          author: "Mobile Agent",
          role: "assistant",
          contents: [
            {
              type: "TextContent",
              content: "Execution response from stream.",
            },
          ],
        }),
      );
    });
    await act(async () => {
      ws!.emitMessage(
        JSON.stringify({
          messageId: "system-end",
          author: "$agw-server",
          role: "system",
          contents: [
            {
              type: "TextContent",
              content: "",
              additionalProperties: {
                type: "turn-finished",
              },
            },
          ],
        }),
      );
    });

    await settleAsync();

    expect(collectText(tree?.toJSON())).toContain("Execution response from stream.");
  });

  it("matches the web composer top-right actions", async () => {
    const onClear = jest.fn();
    const onMessageChange = jest.fn();
    const onScrollToTop = jest.fn();
    let tree: renderer.ReactTestRenderer | undefined;

    await act(async () => {
      tree = renderer.create(
        <Composer
          message=""
          onClear={onClear}
          onMessageChange={onMessageChange}
          onScrollToTop={onScrollToTop}
          onSend={jest.fn()}
          safeBottom={0}
        />,
      );
    });

    await act(async () => {
      tree!.root.findByProps({ testID: "agw-quick-text-open" }).props.onPress();
    });
    await act(async () => {
      tree!.root.findByProps({ testID: "agw-quick-text-option-analyze" }).props.onPress();
    });

    expect(onMessageChange).toHaveBeenCalledWith(
      "Please analyze the code in this file and provide insights about ",
    );

    await act(async () => {
      tree!.root.findByProps({ testID: "agw-clear-session" }).props.onPress();
    });
    await act(async () => {
      tree!.root.findByProps({ testID: "agw-scroll-to-top" }).props.onPress();
    });

    expect(onClear).toHaveBeenCalledTimes(1);
    expect(onScrollToTop).toHaveBeenCalledTimes(1);

    await act(async () => {
      tree!.update(
        <Composer
          isSending
          message=""
          onClear={onClear}
          onMessageChange={onMessageChange}
          onScrollToTop={onScrollToTop}
          onSend={jest.fn()}
          safeBottom={0}
        />,
      );
    });

    expect(tree!.root.findByProps({ testID: "agw-clear-session" }).props.disabled).toBe(true);
  });

  it("clears current context records from the composer toolbar", async () => {
    let tree: renderer.ReactTestRenderer | undefined;

    await act(async () => {
      tree = renderer.create(<App routeName="home" title="Home" />);
    });
    await settleAsync();

    expect(collectText(tree?.toJSON())).toContain("Backend response from task history.");

    await act(async () => {
      tree!.root.findByProps({ testID: "agw-clear-session" }).props.onPress();
    });
    await settleAsync();

    expect(fetchMock).toHaveBeenCalledWith(
      "http://localhost:5015/api/projects/project-1/contexts/context-1/clear-records",
      expect.objectContaining({
        headers: { Authorization: "Bearer test-api-key" },
        method: "DELETE",
      }),
    );
    expect(collectText(tree?.toJSON())).not.toContain("Backend response from task history.");
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
  node: renderer.ReactTestRendererJSON | renderer.ReactTestRendererJSON[] | null | undefined,
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

function getSettingsActionBottomPadding(tree: renderer.ReactTestRenderer): number {
  const actionRows = tree.root.findAll(
    (node) =>
      Array.isArray(node.props.style) &&
      node.props.style.includes(styles.configActionRow) &&
      node.props.style.includes(styles.settingsActionRow),
  );
  const styleEntries = actionRows[0]?.props.style ?? [];
  const inlineStyle = styleEntries.find(
    (entry: unknown): entry is { paddingBottom: number } =>
      typeof entry === "object" && entry !== null && "paddingBottom" in entry,
  );

  return inlineStyle?.paddingBottom ?? 0;
}

function createAgwFetchMock({
  includeDefaultSelections = false,
}: {
  includeDefaultSelections?: boolean;
} = {}) {
  return async (input: RequestInfo | URL) => {
    const url = String(input);
    const pathname = new URL(url).pathname;

    if (pathname === "/api/server-info") {
      return jsonResponse({ serverVersion: "0.1.0-test", apiMajorVersion: 1, initialized: true });
    }

    if (pathname === "/api/projects") {
      const projectResponses = [
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
      ];

      if (includeDefaultSelections) {
        projectResponses.splice(1, 0, {
          id: "default-built-in",
          name: "Default Built In",
          workspace: "D:\\work\\default-built-in",
          enable: true,
          extraSetting: "{}",
        });
      }

      return jsonResponse(projectResponses);
    }

    if (pathname === "/api/agents") {
      const agentResponses = [
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
      ];

      if (includeDefaultSelections) {
        agentResponses.splice(1, 0, {
          id: "agent-hello",
          displayName: "Hello",
          name: "hello",
        });
      }

      return jsonResponse(agentResponses);
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

    if (pathname === "/api/projects/project-1/contexts") {
      return jsonResponse([
        {
          projectId: "project-1",
          contextId: "context-1",
          title: "Mobile API Chat",
          latestTaskId: "task-1",
          latestStatus: 2,
          taskCount: 1,
          messageCount: 2,
          createTime: "2026-05-22T10:00:00Z",
          updateTime: "2026-05-22T10:05:00Z",
        },
      ]);
    }

    if (pathname === "/api/projects/project-2/contexts") {
      return jsonResponse([
        {
          projectId: "project-2",
          contextId: "context-2",
          title: "Project Two API Chat",
          latestTaskId: "task-2",
          latestStatus: 2,
          taskCount: 1,
          messageCount: 1,
          createTime: "2026-05-22T11:00:00Z",
          updateTime: "2026-05-22T11:05:00Z",
        },
      ]);
    }

    if (pathname === "/api/projects/default-built-in/contexts") {
      return jsonResponse([
        {
          projectId: "default-built-in",
          contextId: "context-default",
          title: "Default Built In Chat",
          latestTaskId: "task-default",
          latestStatus: 2,
          taskCount: 1,
          messageCount: 1,
          createTime: "2026-05-22T12:00:00Z",
          updateTime: "2026-05-22T12:05:00Z",
        },
      ]);
    }

    if (pathname === "/api/projects/project-2/contexts/context-2") {
      return jsonResponse({
        projectId: "project-2",
        contextId: "context-2",
        title: "Project Two API Chat",
        latestTaskId: "task-2",
        latestStatus: 2,
        taskCount: 1,
        createTime: "2026-05-22T11:00:00Z",
        updateTime: "2026-05-22T11:05:00Z",
        messageCount: 1,
        tasks: [],
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

    if (pathname === "/api/projects/project-1/contexts/context-1") {
      return jsonResponse({
        projectId: "project-1",
        contextId: "context-1",
        title: "Mobile API Chat",
        latestTaskId: "task-1",
        latestStatus: 2,
        taskCount: 1,
        createTime: "2026-05-22T10:00:00Z",
        updateTime: "2026-05-22T10:05:00Z",
        messageCount: 2,
        tasks: [],
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

    if (pathname === "/api/projects/project-1/contexts/context-1/clear-records") {
      return jsonResponse(undefined);
    }

    if (pathname === "/api/projects/default-built-in/contexts/context-default") {
      return jsonResponse({
        projectId: "default-built-in",
        contextId: "context-default",
        title: "Default Built In Chat",
        latestTaskId: "task-default",
        latestStatus: 2,
        taskCount: 1,
        createTime: "2026-05-22T12:00:00Z",
        updateTime: "2026-05-22T12:05:00Z",
        messageCount: 1,
        tasks: [],
        messages: [
          {
            messageId: "message-default",
            author: "Hello",
            role: "assistant",
            contents: [
              {
                type: "TextContent",
                content: "Default built-in response from task history.",
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
            path: "src",
            type: "directory",
            modifiedTime: "2026-05-22T08:00:00Z",
          },
          {
            name: "mobile-api.md",
            path: "mobile-api.md",
            type: "file",
            size: 1024,
            modifiedTime: "2026-05-22T09:00:00Z",
          },
        ],
      });
    }

    return jsonResponse({ message: `Unhandled URL: ${url}` }, 404);
  };
}

function jsonResponse(body: unknown, status = 200): Response {
  const envelope =
    status >= 200 && status < 300 ? { code: 2000000, title: "OK", data: body } : body;

  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: status === 200 ? "OK" : "Not Found",
    headers: {
      get: (name: string) => (name.toLowerCase() === "content-type" ? "application/json" : null),
    },
    json: async () => envelope,
    text: async () => JSON.stringify(envelope),
  } as unknown as Response;
}
