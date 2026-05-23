import React from "react";
import { StyleSheet, Text, View } from "react-native";
import renderer, { act } from "react-test-renderer";
import type { AgwMessage } from "../src/rn/api/agw-api-types";
import { ChatPanel } from "../src/rn/pages/home/components/chat-panel";
import { styles } from "../src/rn/pages/home/components/styles";

jest.mock(
  "react-native-markdown-display",
  () => {
    const React = require("react");
    const { Text } = require("react-native");

    return {
      __esModule: true,
      default: ({ children }: { children: React.ReactNode }) =>
        React.createElement(Text, { testID: "agw-markdown" }, children),
    };
  },
  { virtual: true }
);

(
  globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }
).IS_REACT_ACT_ENVIRONMENT = true;

describe("ChatPanel", () => {
  it("renders text contents through the markdown component", () => {
    const tree = renderChat([
      createMessage({
        contents: [
          {
            type: "TextContent",
            content: "Hello **mobile**",
          },
        ],
      }),
    ]);

    expect(markdownTexts(tree)).toContain("Hello **mobile**");
  });

  it("uses readable spacing for message markdown text", () => {
    expect(StyleSheet.flatten(styles.messageContentContainer)).toMatchObject({
      gap: 8,
    });
    expect(StyleSheet.flatten(styles.markdownBody)).toMatchObject({
      lineHeight: 22,
    });
    expect(StyleSheet.flatten(styles.selfBubbleText)).toMatchObject({
      lineHeight: 22,
    });
    expect(StyleSheet.flatten(styles.receiverBubbleText)).toMatchObject({
      lineHeight: 22,
    });
    expect(StyleSheet.flatten(styles.markdownParagraph)).toMatchObject({
      marginBottom: 8,
      marginTop: 0,
    });
    expect(StyleSheet.flatten(styles.markdownList)).toMatchObject({
      marginBottom: 8,
      marginTop: 2,
    });
    expect(StyleSheet.flatten(styles.markdownListItem)).toMatchObject({
      marginBottom: 4,
    });
    expect(StyleSheet.flatten(styles.markdownCodeInline)).toMatchObject({
      backgroundColor: "#fdf6e3",
    });
    expect(StyleSheet.flatten(styles.selfMarkdownCodeInline)).toMatchObject({
      backgroundColor: "#fdf6e3",
    });
    expect(StyleSheet.flatten(styles.markdownCodeBlock)).toMatchObject({
      backgroundColor: "#fdf6e4",
    });
    expect(StyleSheet.flatten(styles.selfMarkdownCodeBlock)).toMatchObject({
      backgroundColor: "#fdf6e4",
    });
  });

  it("skips system messages and messages without an author", () => {
    const tree = renderChat([
      createMessage({
        author: "$agw-server",
        messageId: "system-message",
        role: "system",
        contents: [{ type: "TextContent", content: "Hidden system text" }],
      }),
      createMessage({
        author: null,
        messageId: "missing-author-message",
        contents: [{ type: "TextContent", content: "Hidden author text" }],
      }),
      createMessage({
        messageId: "visible-message",
        contents: [{ type: "TextContent", content: "Visible response" }],
      }),
    ]);

    const output = collectText(tree.toJSON());

    expect(output).toContain("Visible response");
    expect(output).not.toContain("Hidden system text");
    expect(output).not.toContain("Hidden author text");
  });

  it("groups matching function calls and function results by call id", () => {
    const tree = renderChat([
      createMessage({
        messageId: "call-message",
        contents: [
          {
            type: "FunctionCallContent",
            content: "{\"query\":\"repo\"}",
            additionalProperties: {
              callId: "call-1",
              toolName: "Search",
            },
          },
        ],
      }),
      createMessage({
        messageId: "result-message",
        role: "tool",
        author: "Search",
        contents: [
          {
            type: "FunctionResultContent",
            content: "{\"total\":1}",
            additionalProperties: {
              callId: "call-1",
            },
          },
        ],
      }),
    ]);

    const toolGroups = tree.root.findAll(
      (node) => node.type === View && node.props.testID === "agw-tool-group"
    );

    expect(toolGroups).toHaveLength(1);
    expect(collectInstanceText(toolGroups[0])).toContain("Search");
    expect(markdownTexts(tree)).toContain(
      '\n```json\n{\n  "query": "repo"\n}\n```'
    );
    expect(markdownTexts(tree)).toContain('\n```json\n{\n  "total": 1\n}\n```');
  });

  it("formats standalone function JSON content as a fenced json code block", () => {
    const tree = renderChat([
      createMessage({
        contents: [
          {
            type: "FunctionCallContent",
            content: "{\"path\":\"src\"}",
            additionalProperties: {
              toolName: "ListFiles",
            },
          },
        ],
      }),
    ]);

    expect(markdownTexts(tree)).toContain(
      '\n```json\n{\n  "path": "src"\n}\n```'
    );
  });
});

function renderChat(messages: AgwMessage[]): renderer.ReactTestRenderer {
  let tree: renderer.ReactTestRenderer | undefined;

  act(() => {
    tree = renderer.create(<ChatPanel messages={messages} />);
  });

  return tree!;
}

function createMessage(overrides: Partial<AgwMessage> = {}): AgwMessage {
  return {
    messageId: "message-1",
    author: "Mobile Agent",
    role: "assistant",
    contents: [{ type: "TextContent", content: "Default response" }],
    ...overrides,
  };
}

function markdownTexts(tree: renderer.ReactTestRenderer): string[] {
  return tree.root
    .findAll(
      (node) => node.type === Text && node.props.testID === "agw-markdown"
    )
    .map((node) => collectInstanceText(node));
}

function collectInstanceText(node: renderer.ReactTestInstance): string {
  return node.children
    .map((child) =>
      typeof child === "string"
        ? child
        : collectInstanceText(child as renderer.ReactTestInstance)
    )
    .join("");
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
