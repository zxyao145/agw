import React from "react";
import { ScrollView, Text, View } from "react-native";
import type { AgwMessage } from "../../../api/agw-api-types";
import { styles } from "./styles";

export function ChatPanel({
  error,
  isLoading,
  messages,
}: {
  error?: string | null;
  isLoading?: boolean;
  messages: AgwMessage[];
}): React.JSX.Element {
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
      style={styles.panelScroll}
    >
      {messages.map((message) => (
        <ChatMessageRow key={message.messageId} message={message} />
      ))}
    </ScrollView>
  );
}

function ChatMessageRow({
  message,
}: {
  message: AgwMessage;
}): React.JSX.Element | null {
  const text = getMessageText(message);
  if (!text) {
    return null;
  }

  if (message.role.toLowerCase() === "user") {
    return (
      <View style={styles.selfMessageRow}>
        <View style={[styles.selfBubble, styles.bubbleShadow]}>
          <Text style={styles.selfBubbleText}>{text}</Text>
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
        <Text style={styles.receiverBubbleText}>{text}</Text>
      </View>
    </View>
  );
}

function getMessageText(message: AgwMessage): string {
  const textContent = message.contents.find(
    (content) =>
      typeof content.content === "string" &&
      (content.type === "TextContent" || content.type === "text")
  );

  return textContent?.content?.trim() ?? "";
}
