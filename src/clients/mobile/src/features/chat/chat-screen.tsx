import { MessageContentType, type AiMessage, type AiMessageContent } from "@agw/api";
import { processMessages, type ProcessedMessageItem } from "@agw/execution-core";
import { Image as ExpoImage } from "expo-image";
import { ChevronDown, ChevronRight, Link as LinkIcon, Wrench } from "lucide-react-native";
import React from "react";
import {
  ActivityIndicator,
  FlatList,
  Linking,
  Pressable,
  StyleSheet,
  Text,
  View,
} from "react-native";
import { useMarkdown } from "react-native-marked";

import { useWorkspace } from "@/features/workspace/workspace-provider";
import { WorkspaceShell } from "@/features/workspace/workspace-shell";
import { colors, radius, typography } from "@/theme/tokens";
import {
  getRenderableMessageContents,
  hasRenderableMessageContent,
  stringifyContentValue,
} from "./message-rendering";

export function ChatScreen(): React.JSX.Element {
  const workspace = useWorkspace();
  const listRef = React.useRef<FlatList<ProcessedMessageItem<AiMessage>>>(null);
  const items = React.useMemo(
    () =>
      processMessages(workspace.messages).filter(
        (item) => item.type === "accordion" || hasRenderableMessageContent(item.message),
      ),
    [workspace.messages],
  );

  React.useEffect(() => {
    if (items.length) requestAnimationFrame(() => listRef.current?.scrollToEnd({ animated: true }));
  }, [items.length]);

  return (
    <WorkspaceShell
      active="chat"
      onScrollToTop={() => listRef.current?.scrollToOffset({ offset: 0, animated: true })}
    >
      {workspace.isChatLoading ? (
        <View style={styles.center}>
          <ActivityIndicator color={colors.primary} />
        </View>
      ) : items.length === 0 ? (
        <View style={styles.empty}>
          <Text style={styles.emptyTitle}>Start a conversation</Text>
          <Text style={styles.emptyText}>
            Choose an agent, then ask Agw to work with the selected project.
          </Text>
        </View>
      ) : (
        <FlatList
          ref={listRef}
          data={items}
          keyExtractor={(item, index) =>
            item.type === "accordion"
              ? `tool-${item.messages[0]?.messageId}-${index}`
              : `${item.type}-${item.message.messageId}-${index}`
          }
          contentContainerStyle={styles.list}
          keyboardDismissMode="interactive"
          keyboardShouldPersistTaps="handled"
          renderItem={({ item, index }) => (
            <>
              {index === 0 ? (
                <View style={styles.datePill}>
                  <Text style={styles.dateText}>Today</Text>
                </View>
              ) : null}
              {item.type === "accordion" ? (
                <ToolGroup item={item} />
              ) : (
                <MessageCard message={item.message} result={item.type === "result"} />
              )}
            </>
          )}
        />
      )}
      {workspace.reconnectState ? (
        <Text style={styles.status}>Reconnecting to the execution…</Text>
      ) : null}
      {workspace.error ? <Text style={styles.error}>{workspace.error}</Text> : null}
    </WorkspaceShell>
  );
}

function MessageCard({
  message,
  result = false,
}: {
  message: AiMessage;
  result?: boolean;
}): React.JSX.Element | null {
  const isUser = message.role === "user";
  const visible = getRenderableMessageContents(message);
  if (visible.length === 0) return null;
  return (
    <View style={[styles.messageRow, isUser && styles.messageRowUser]}>
      {!isUser ? (
        <Text style={styles.author}>{message.author || (result ? "Result" : "Agw")}</Text>
      ) : null}
      <View
        style={[
          styles.bubble,
          isUser ? styles.userBubble : result ? styles.resultBubble : styles.agentBubble,
        ]}
      >
        {visible.map((content, index) => (
          <Content key={`${message.messageId}-${index}`} content={content} />
        ))}
      </View>
    </View>
  );
}

function Content({ content }: { content: AiMessageContent }): React.JSX.Element | null {
  const value = stringifyContentValue(content.content);
  if (content.type === MessageContentType.TextContent || content.type === "text") {
    return <MarkdownText value={value} inverted={false} />;
  }
  if (content.type === MessageContentType.TextReasoningContent) {
    return <Reasoning value={value} />;
  }
  if (content.type === MessageContentType.DataContent && content.uri?.startsWith("data:image/")) {
    return (
      <ExpoImage source={{ uri: content.uri }} contentFit="cover" style={styles.messageImage} />
    );
  }
  if (content.type === MessageContentType.UriContent && content.uri) {
    return (
      <Pressable onPress={() => void Linking.openURL(content.uri!)} style={styles.linkRow}>
        <LinkIcon color={colors.primary} size={15} />
        <Text numberOfLines={2} style={styles.linkText}>
          {content.name || content.uri}
        </Text>
      </Pressable>
    );
  }
  if (content.type === MessageContentType.ErrorContent) {
    return <Text style={styles.contentError}>{value || "Execution error"}</Text>;
  }
  if (!value) return null;
  return <Text style={styles.plainText}>{value}</Text>;
}

function MarkdownText({
  value,
  inverted,
}: {
  value: string;
  inverted: boolean;
}): React.JSX.Element {
  const foreground = inverted ? colors.white : colors.ink;
  const nodes = useMarkdown(value, {
    styles: {
      text: { color: foreground, fontFamily: typography.regular, fontSize: 14, lineHeight: 21 },
      paragraph: { marginTop: 0, marginBottom: 7 },
      strong: { color: foreground, fontFamily: typography.semibold },
      em: { color: foreground },
      link: { color: inverted ? colors.white : colors.primary, textDecorationLine: "underline" },
      code: {
        backgroundColor: inverted ? "rgba(255,255,255,0.16)" : colors.code,
        borderRadius: 7,
        padding: 9,
      },
      codespan: {
        color: foreground,
        backgroundColor: inverted ? "rgba(255,255,255,0.16)" : colors.code,
      },
      h1: { color: foreground, fontFamily: typography.semibold, fontSize: 20 },
      h2: { color: foreground, fontFamily: typography.semibold, fontSize: 18 },
      h3: { color: foreground, fontFamily: typography.semibold, fontSize: 16 },
      li: { color: foreground, fontFamily: typography.regular, fontSize: 14, lineHeight: 21 },
    },
  });
  return <View>{nodes}</View>;
}

function Reasoning({ value }: { value: string }): React.JSX.Element {
  const [open, setOpen] = React.useState(false);
  return (
    <View style={styles.reasoning}>
      <Pressable onPress={() => setOpen((current) => !current)} style={styles.reasoningHeader}>
        {open ? (
          <ChevronDown color={colors.muted} size={15} />
        ) : (
          <ChevronRight color={colors.muted} size={15} />
        )}
        <Text style={styles.reasoningTitle}>Reasoning</Text>
      </Pressable>
      {open ? <MarkdownText value={value} inverted={false} /> : null}
    </View>
  );
}

function ToolGroup({
  item,
}: {
  item: Extract<ProcessedMessageItem<AiMessage>, { type: "accordion" }>;
}): React.JSX.Element {
  const [open, setOpen] = React.useState(false);
  return (
    <View style={styles.toolCard}>
      <Pressable onPress={() => setOpen((current) => !current)} style={styles.toolHeader}>
        <Wrench color={colors.primary} size={16} />
        <Text numberOfLines={1} style={styles.toolTitle}>
          {item.toolName || "Tool"}
        </Text>
        {open ? (
          <ChevronDown color={colors.muted} size={16} />
        ) : (
          <ChevronRight color={colors.muted} size={16} />
        )}
      </Pressable>
      {open
        ? item.messages.map((message, index) => (
            <View key={`${message.messageId}-${index}`} style={styles.toolBody}>
              {getRenderableMessageContents(message).map((content, contentIndex) => (
                <Content key={contentIndex} content={content} />
              ))}
            </View>
          ))
        : null}
    </View>
  );
}

const styles = StyleSheet.create({
  center: { flex: 1, alignItems: "center", justifyContent: "center" },
  empty: { flex: 1, paddingHorizontal: 42, alignItems: "center", justifyContent: "center" },
  emptyTitle: { color: colors.ink, fontFamily: typography.semibold, fontSize: 20 },
  emptyText: {
    color: colors.muted,
    fontFamily: typography.regular,
    fontSize: 14,
    lineHeight: 21,
    textAlign: "center",
    marginTop: 8,
  },
  list: { paddingHorizontal: 16, paddingTop: 12, paddingBottom: 16, gap: 12 },
  datePill: {
    alignSelf: "center",
    paddingHorizontal: 12,
    paddingVertical: 5,
    borderRadius: radius.pill,
    backgroundColor: colors.datePill,
    marginBottom: 4,
  },
  dateText: { color: colors.muted, fontFamily: typography.medium, fontSize: 11 },
  messageRow: { maxWidth: "88%", alignSelf: "flex-start", gap: 4 },
  messageRowUser: { alignSelf: "flex-end", alignItems: "flex-end" },
  author: { color: colors.muted, fontFamily: typography.medium, fontSize: 11, marginLeft: 4 },
  bubble: {
    paddingHorizontal: 13,
    paddingVertical: 10,
    borderRadius: radius.lg,
    overflow: "hidden",
  },
  userBubble: { backgroundColor: "#f3f3f4" },
  agentBubble: { backgroundColor: "transparent", paddingHorizontal: 0, paddingVertical: 0 },
  resultBubble: { backgroundColor: colors.surface, borderWidth: 1, borderColor: colors.border },
  plainText: { color: colors.ink, fontFamily: typography.regular, fontSize: 14, lineHeight: 21 },
  contentError: {
    color: colors.danger,
    fontFamily: typography.medium,
    fontSize: 13,
    lineHeight: 19,
  },
  messageImage: {
    width: 220,
    height: 160,
    borderRadius: radius.md,
    backgroundColor: colors.surface,
    marginBottom: 6,
  },
  linkRow: { flexDirection: "row", alignItems: "center", gap: 7 },
  linkText: { flex: 1, color: colors.primary, fontFamily: typography.medium, fontSize: 13 },
  reasoning: { gap: 6 },
  reasoningHeader: { flexDirection: "row", alignItems: "center", gap: 5 },
  reasoningTitle: { color: colors.muted, fontFamily: typography.semibold, fontSize: 12 },
  toolCard: {
    borderWidth: 1,
    borderColor: colors.border,
    borderRadius: radius.md,
    backgroundColor: colors.white,
    overflow: "hidden",
  },
  toolHeader: {
    minHeight: 48,
    paddingHorizontal: 12,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
  },
  toolTitle: { flex: 1, color: colors.ink, fontFamily: typography.semibold, fontSize: 13 },
  toolBody: { padding: 12, borderTopWidth: StyleSheet.hairlineWidth, borderColor: colors.border },
  status: {
    paddingHorizontal: 16,
    paddingVertical: 5,
    color: colors.warning,
    backgroundColor: colors.warningSoft,
    fontFamily: typography.medium,
    fontSize: 11,
  },
  error: {
    paddingHorizontal: 16,
    paddingVertical: 6,
    color: colors.danger,
    backgroundColor: colors.dangerSoft,
    fontFamily: typography.regular,
    fontSize: 11,
  },
});
