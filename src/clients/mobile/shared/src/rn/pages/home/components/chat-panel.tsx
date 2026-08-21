import React from "react";
import {
  ScrollView,
  StyleSheet,
  Text,
  View,
  type ImageStyle,
  type TextStyle,
  type ViewStyle,
} from "react-native";
import Markdown from "react-native-markdown-display";
import type { AgwMessage } from "../../../api/agw-api-types";
import { processMessages, type ProcessedMessageItem } from "@agw/execution-core";
import { styles } from "./styles";

const MessageContentType = {
  DataContent: "DataContent",
  ErrorContent: "ErrorContent",
  FunctionCallContent: "FunctionCallContent",
  FunctionResultContent: "FunctionResultContent",
  TextContent: "TextContent",
  TextReasoningContent: "TextReasoningContent",
  UriContent: "UriContent",
  UsageContent: "UsageContent",
} as const;

type MessageNode = {
  content: string;
  type: string;
};

type ChatPanelProps = {
  error?: string | null;
  isLoading?: boolean;
  messages: AgwMessage[];
  scrollViewRef?: React.RefObject<ScrollView | null>;
};

function ChatPanelComponent({
  error,
  isLoading,
  messages,
  scrollViewRef,
}: ChatPanelProps): React.JSX.Element {
  // 先让 core 在完整 messages 上配对工具调用与结果（authorless 的 tool result 也能参与），
  // 再在渲染层过滤掉不需要独立展示的 normal/result 项，避免破坏配对。
  const displayItems = React.useMemo(
    () =>
      processMessages(messages).filter((item) => {
        if (item.type === "accordion") {
          return true;
        }

        return shouldDisplayMessage(item.message);
      }),
    [messages],
  );

  if (isLoading) {
    return (
      <View style={styles.emptyPanel}>
        <Text style={styles.emptyPanelText}>Loading chat</Text>
      </View>
    );
  }

  if (error) {
    return (
      <View style={styles.emptyPanel}>
        <Text style={styles.errorText}>{error}</Text>
      </View>
    );
  }

  if (messages.length === 0) {
    return (
      <View style={styles.emptyPanel}>
        <Text style={styles.emptyPanelText}>No chat history yet</Text>
      </View>
    );
  }

  return (
    <ScrollView
      alwaysBounceVertical={false}
      contentContainerStyle={styles.chatContent}
      ref={scrollViewRef}
      style={styles.panelScroll}
    >
      {displayItems.map((item, index) =>
        item.type === "accordion" ? (
          <ToolGroup key={`tool-${index}`} item={item} />
        ) : (
          <AgwMessageComponent
            key={`${item.message.messageId}-${index}`}
            message={item.message}
          />
        )
      )}
    </ScrollView>
  );
}

export const ChatPanel = React.memo(ChatPanelComponent);

function ToolGroup({
  item,
}: {
  item: Extract<ProcessedMessageItem<AgwMessage>, { type: "accordion" }>;
}): React.JSX.Element {
  return (
    <View style={styles.toolGroup} testID="agw-tool-group">
      <View style={styles.toolGroupHeader}>
        <Text style={styles.toolGroupEyebrow}>Tool use</Text>
        {item.toolName ? (
          <Text style={styles.toolGroupTitle}>{item.toolName}</Text>
        ) : null}
      </View>
      <View style={styles.toolGroupMessages}>
        {item.messages.map((message, index) => (
          <AgwMessageComponent
            key={`${message.messageId}-${index}`}
            message={message}
          />
        ))}
      </View>
    </View>
  );
}

function AgwMessageComponent({
  message,
}: {
  message: AgwMessage;
}): React.JSX.Element | null {
  const isResult =
    message.role === "system" && message.additionalProperties?.type === "result";
  if (isResult) {
    return null;
  }

  const contentNodes = groupContentsByType(message);
  if (contentNodes.length === 0) {
    return null;
  }

  const isUser = message.role.toLowerCase() === "user";
  const isToolUse = message.contents.some(
    (content) => content.type === MessageContentType.FunctionCallContent
  );
  const isToolResult = message.contents.some(
    (content) => content.type === MessageContentType.FunctionResultContent
  );
  const isSideRight = isUser && !isToolResult;
  const title = getMessageTitle(message, isUser, isToolUse, isToolResult);

  if (isSideRight) {
    return (
      <View style={styles.selfMessageRow}>
        <View style={[styles.selfBubble, styles.bubbleShadow]}>
          <Text style={[styles.messageTitle, styles.selfMessageTitle]}>
            {title}
          </Text>
          <View style={styles.messageContentContainer}>
            {contentNodes.map((node, index) => (
              <React.Fragment key={`${node.type}-${index}`}>
                {renderContent(node, true)}
              </React.Fragment>
            ))}
          </View>
        </View>
      </View>
    );
  }

  return (
    <View style={styles.receiverGroup}>
      {message.author ? (
        <Text style={styles.senderLabel}>{message.author}</Text>
      ) : null}
      <View style={[styles.receiverBubble, styles.bubbleShadow]}>
        <Text style={styles.messageTitle}>{title}</Text>
        <View style={styles.messageContentContainer}>
          {contentNodes.map((node, index) => (
            <React.Fragment key={`${node.type}-${index}`}>
              {renderContent(node, false)}
            </React.Fragment>
          ))}
        </View>
      </View>
    </View>
  );
}

function MarkdownText({
  content,
  isUser,
  subdued = false,
}: {
  content: string;
  isUser: boolean;
  subdued?: boolean;
}): React.JSX.Element {
  const markdownStyles = React.useMemo(
    (): Record<string, ImageStyle | TextStyle | ViewStyle> => ({
      body: StyleSheet.flatten([
        styles.markdownBody,
        isUser ? styles.selfMarkdownBody : styles.receiverMarkdownBody,
        subdued ? styles.reasoningMarkdownBody : null,
      ]) as TextStyle,
      bullet_list: styles.markdownList,
      code_block: StyleSheet.flatten([
        styles.markdownCodeBlock,
        isUser ? styles.selfMarkdownCodeBlock : null,
      ]) as TextStyle,
      code_inline: StyleSheet.flatten([
        styles.markdownCodeInline,
        isUser ? styles.selfMarkdownCodeInline : null,
      ]) as TextStyle,
      fence: StyleSheet.flatten([
        styles.markdownCodeBlock,
        isUser ? styles.selfMarkdownCodeBlock : null,
      ]) as TextStyle,
      list_item: styles.markdownListItem,
      ordered_list: styles.markdownList,
      paragraph: styles.markdownParagraph,
      strong: styles.markdownStrong,
      text: StyleSheet.flatten([
        styles.markdownBody,
        isUser ? styles.selfMarkdownBody : styles.receiverMarkdownBody,
        subdued ? styles.reasoningMarkdownBody : null,
      ]) as TextStyle,
    }),
    [isUser, subdued]
  );

  return (
    <Markdown style={markdownStyles}>{content}</Markdown>
  );
}

function renderContent(
  node: MessageNode,
  isUser: boolean
): React.ReactNode {
  if (!node.content) {
    return null;
  }

  if (isTextNode(node.type)) {
    return (
      <View style={styles.messageContent}>
        <MarkdownText content={node.content} isUser={isUser} />
      </View>
    );
  }

  if (node.type === MessageContentType.TextReasoningContent) {
    return (
      <View style={styles.messageContent}>
        <MarkdownText content={node.content} isUser={isUser} subdued />
      </View>
    );
  }

  if (
    node.type === MessageContentType.UriContent ||
    node.type === MessageContentType.UsageContent
  ) {
    return (
      <Text
        style={isUser ? styles.selfBubbleText : styles.receiverBubbleText}
      >
        {node.content}
      </Text>
    );
  }

  return null;
}

function shouldDisplayMessage(message: AgwMessage): boolean {
  if (!message.author) {
    return false;
  }

  if (message.role.toLowerCase() === "system") {
    return false;
  }

  return message.contents.length > 0;
}

function getMessageTitle(
  message: AgwMessage,
  isUser: boolean,
  isToolUse: boolean,
  isToolResult: boolean
): string {
  if (isToolResult) {
    return "Tool result";
  }

  if (isToolUse) {
    return "Tool use";
  }

  if (isUser) {
    return "You";
  }

  return `${message.role} (${message.author ?? "-"})`;
}

function groupContentsByType(message: AgwMessage): MessageNode[] {
  const nodes: MessageNode[] = [];
  let currentContent = "";
  let lastType = "";

  for (const content of message.contents) {
    const { type } = content;

    if (lastType && type !== lastType) {
      nodes.push({ content: currentContent, type: lastType });
      currentContent = "";
    }

    currentContent +=
      (currentContent ? "" : getNodePrefix(type)) +
      buildContentNode(content, message);
    lastType = type;
  }

  if (lastType) {
    nodes.push({ content: currentContent, type: lastType });
  }

  return nodes;
}

function buildContentNode(
  content: AgwMessage["contents"][number],
  message: AgwMessage
): string {
  const { type, content: value } = content;

  if (type === MessageContentType.UsageContent) {
    const usage = value as unknown as {
      inputTokenCount?: number;
      outputTokenCount?: number;
    };
    let result = `inputToken: ${usage?.inputTokenCount ?? 0} | outputToken: ${
      usage?.outputTokenCount ?? 0
    }`;
    const usd = message.additionalProperties?.totalCostUsd;
    if (typeof usd === "number") {
      result += ` | totalCost: ${usd} (USD)`;
    }
    return result;
  }

  let processed = stringifyContentValue(value);

  if (
    type === MessageContentType.FunctionCallContent ||
    type === MessageContentType.FunctionResultContent
  ) {
    const trimmed = processed.trim();
    if (trimmed.startsWith("{") && trimmed.endsWith("}")) {
      try {
        processed = `\n\`\`\`json\n${JSON.stringify(
          JSON.parse(trimmed),
          null,
          2
        )}\n\`\`\``;
      } catch {
        // Keep original content if it is not valid JSON.
      }
    }
  }

  if (processed.startsWith("<local-command-stdout>")) {
    return stripCommandTags(processed);
  }

  return processed;
}

function stringifyContentValue(value: string | null | undefined): string {
  if (typeof value === "string") {
    return value;
  }

  if (value == null) {
    return "";
  }

  return String(value);
}

function stripCommandTags(value: string): string {
  return value
    .replace("<local-command-stdout>", "")
    .replace("</local-command-stdout>", "");
}

function getNodePrefix(type: string): string {
  if (type === MessageContentType.ErrorContent) {
    return "ERROR: ";
  }

  if (type === MessageContentType.UsageContent) {
    return "Usage: ";
  }

  return "";
}

function isTextNode(type: string): boolean {
  return (
    [
      MessageContentType.TextContent,
      "text",
      MessageContentType.FunctionCallContent,
      MessageContentType.FunctionResultContent,
      MessageContentType.DataContent,
      MessageContentType.ErrorContent,
    ] as string[]
  ).includes(type);
}
