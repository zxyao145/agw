import React from "react";
import renderer, { act } from "react-test-renderer";
import { HistoryDrawer } from "../src/rn/pages/home/components/history-drawer";

jest.mock(
  "lucide-react-native",
  () => {
    const React = require("react");
    const { Text } = require("react-native");

    return {
      Bolt: () => React.createElement(Text, null, "lucide-bolt"),
    };
  },
  { virtual: true }
);

(
  globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }
).IS_REACT_ACT_ENVIRONMENT = true;

describe("HistoryDrawer", () => {
  it("uses the built-in project and Hello agent as unresolved selector defaults", () => {
    let tree: renderer.ReactTestRenderer | undefined;

    act(() => {
      tree = renderer.create(
        <HistoryDrawer
          onClose={jest.fn()}
          onOpenSettings={jest.fn()}
          onProjectSelect={jest.fn()}
          onTargetSelect={jest.fn()}
          contexts={[]}
          onContextSelect={jest.fn()}
          projects={[]}
          safeBottom={0}
          safeTop={0}
          selectedProjectId={null}
          selectedTargetValue={null}
          targets={[]}
        />
      );
    });

    const output = collectText(tree?.toJSON());

    expect(output).toContain("default-built-in");
    expect(output).toContain("Hello");
    expect(output).not.toContain("No project");
    expect(output).not.toContain("No agent");
  });

  it("uses a lucide Bolt icon for the settings entry", () => {
    let tree: renderer.ReactTestRenderer | undefined;

    act(() => {
      tree = renderer.create(
        <HistoryDrawer
          onClose={jest.fn()}
          onOpenSettings={jest.fn()}
          onProjectSelect={jest.fn()}
          onTargetSelect={jest.fn()}
          contexts={[]}
          onContextSelect={jest.fn()}
          projects={[]}
          safeBottom={0}
          safeTop={0}
          selectedProjectId={null}
          selectedTargetValue={null}
          targets={[]}
        />
      );
    });

    expect(collectText(tree?.toJSON())).toContain("lucide-bolt");
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
