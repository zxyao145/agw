import React from "react";
import renderer, { act } from "react-test-renderer";
import App from "../src/rn/App";

jest.mock("react-native-safe-area-context", () => {
  const React = require("react");

  return {
    SafeAreaProvider: ({ children }: { children: React.ReactNode }) =>
      React.createElement(React.Fragment, null, children),
    useSafeAreaInsets: () => ({ top: 0, right: 0, bottom: 0, left: 0 }),
  };
});

(
  globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }
).IS_REACT_ACT_ENVIRONMENT = true;

describe("App", () => {
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
