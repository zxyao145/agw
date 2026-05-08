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

describe("App", () => {
  beforeEach(() => {
    readLocalConfigMock.mockResolvedValue(testConfig);
    writeLocalConfigMock.mockResolvedValue(undefined);
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

    const output = collectText(tree?.toJSON());

    expect(output).toContain("Chat");
    expect(output).toContain("Files");
    expect(output).toContain("TODAY, OCT 24");
    expect(output).toContain("Just finished reviewing them.");
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

    expect(collectText(tree?.toJSON())).toContain("Sarah is typing");

    const filesTab = tree!.root.findByProps({ testID: "agw-tab-files" });

    await act(async () => {
      filesTab.props.onPress();
    });

    const output = collectText(tree?.toJSON());

    expect(output).toContain("Project Alpha");
    expect(output).toContain("Brand_Assets_Hero.png");
    expect(output).toContain("Q4_Marketing_Strategy.pdf");
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

    expect(collectText(tree?.toJSON())).toContain("UI Refresh Strategy");
    expect(collectText(tree?.toJSON())).toContain("Settings");

    const closeDrawer = tree!.root.findByProps({ testID: "agw-close-drawer" });

    await act(async () => {
      closeDrawer.props.onPress();
    });

    expect(collectText(tree?.toJSON())).not.toContain("UI Refresh Strategy");
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
    const importButton = tree!.root.findByProps({
      testID: "agw-config-import-save",
    });
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
});

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
