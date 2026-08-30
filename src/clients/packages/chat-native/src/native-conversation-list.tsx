import * as React from "react";
import { ChevronDown, Pencil, RefreshCw, Settings, Trash2, X } from "lucide-react-native";
import {
  Alert,
  Modal,
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
} from "react-native";
import type { ConversationSummary } from "@agw/projects-core";

import { IconButton } from "./icon-button";
import { useNativeChat } from "./native-chat-provider";
import { defaultNativeChatTheme as theme } from "./theme";

export type NativeConversationListProps = {
  safeTop: number;
  safeBottom: number;
  onClose(): void;
  onOpenChat(): void;
  onOpenSettings(): void;
};

export function NativeConversationList({
  safeTop,
  safeBottom,
  onClose,
  onOpenChat,
  onOpenSettings,
}: NativeConversationListProps) {
  const chat = useNativeChat();
  const [projectPickerOpen, setProjectPickerOpen] = React.useState(false);
  const [editing, setEditing] = React.useState<ConversationSummary | null>(null);

  const selectConversation = (conversationId: string) => {
    try {
      chat.selectConversation(conversationId);
      onOpenChat();
    } catch (error) {
      Alert.alert("Execution in progress", toMessage(error));
    }
  };

  const remove = (conversation: ConversationSummary) => {
    Alert.alert(
      `Delete “${conversation.title}”?`,
      "This permanently removes the conversation history.",
      [
        { text: "Cancel", style: "cancel" },
        {
          text: "Delete",
          style: "destructive",
          onPress: () => void chat.deleteConversation(conversation.conversationId),
        },
      ],
    );
  };

  return (
    <View style={styles.frame}>
      <View style={[styles.header, { paddingTop: safeTop, height: 64 + safeTop }]}>
        <Text style={styles.brand}>Agw</Text>
        <IconButton icon={X} label="Close history" color={theme.primary} onPress={onClose} />
      </View>
      <Pressable onPress={() => setProjectPickerOpen(true)} style={styles.projectSelector}>
        <View style={styles.projectCopy}>
          <Text style={styles.eyebrow}>PROJECT</Text>
          <Text numberOfLines={1} style={styles.projectName}>
            {chat.selectedProject?.name ?? "Select project"}
          </Text>
        </View>
        <ChevronDown color={theme.muted} size={18} />
      </Pressable>
      <View style={styles.sectionHeader}>
        <Text style={styles.sectionTitle}>Conversations</Text>
        <IconButton
          icon={RefreshCw}
          label="Refresh conversations"
          color={theme.primary}
          size={18}
          onPress={() => void chat.refreshConversations()}
        />
      </View>
      <ScrollView contentContainerStyle={styles.list}>
        {chat.conversations.length === 0 ? (
          <View style={styles.empty}>
            <Text style={styles.emptyTitle}>No conversations yet</Text>
            <Text style={styles.emptyText}>Start a new chat to create one.</Text>
          </View>
        ) : (
          chat.conversations.map((conversation) => {
            const active = conversation.conversationId === chat.selectedConversationId;
            return (
              <Pressable
                key={conversation.conversationId}
                onPress={() => selectConversation(conversation.conversationId)}
                style={({ pressed }) => [
                  styles.row,
                  active && styles.rowActive,
                  pressed && styles.pressed,
                ]}
              >
                <View style={styles.rowCopy}>
                  <Text numberOfLines={1} style={styles.rowTitle}>
                    {conversation.title}
                  </Text>
                  <Text numberOfLines={1} style={styles.rowMeta}>
                    {formatConversationMeta(conversation)}
                  </Text>
                </View>
                <View style={styles.rowActions}>
                  <IconButton
                    icon={Pencil}
                    label={`Rename ${conversation.title}`}
                    size={17}
                    disabled={chat.isExecuting}
                    onPress={(event) => {
                      event.stopPropagation();
                      setEditing(conversation);
                    }}
                  />
                  <IconButton
                    icon={Trash2}
                    label={`Delete ${conversation.title}`}
                    size={17}
                    color={theme.danger}
                    disabled={chat.isExecuting}
                    onPress={(event) => {
                      event.stopPropagation();
                      remove(conversation);
                    }}
                  />
                </View>
              </Pressable>
            );
          })
        )}
      </ScrollView>
      <Pressable
        onPress={onOpenSettings}
        style={[styles.settingsRow, { paddingBottom: Math.max(14, safeBottom) }]}
      >
        <Settings color={theme.muted} size={21} />
        <Text style={styles.settingsText}>Settings</Text>
      </Pressable>
      <ProjectPicker
        open={projectPickerOpen}
        onClose={() => setProjectPickerOpen(false)}
        onSelect={(projectId) => {
          try {
            chat.selectProject(projectId);
            setProjectPickerOpen(false);
          } catch (error) {
            Alert.alert("Execution in progress", toMessage(error));
          }
        }}
      />
      <RenameDialog conversation={editing} onClose={() => setEditing(null)} />
    </View>
  );
}

function ProjectPicker({
  open,
  onClose,
  onSelect,
}: {
  open: boolean;
  onClose(): void;
  onSelect(id: string): void;
}) {
  const chat = useNativeChat();
  return (
    <Modal transparent animationType="fade" visible={open} onRequestClose={onClose}>
      <Pressable style={styles.backdrop} onPress={onClose}>
        <View style={styles.sheet}>
          <Text style={styles.dialogTitle}>Choose a project</Text>
          {chat.projects.map((project) => (
            <Pressable
              key={project.id}
              onPress={() => onSelect(project.id)}
              style={[styles.option, project.id === chat.selectedProjectId && styles.optionActive]}
            >
              <Text style={styles.optionText}>{project.name}</Text>
            </Pressable>
          ))}
        </View>
      </Pressable>
    </Modal>
  );
}

function RenameDialog({
  conversation,
  onClose,
}: {
  conversation: ConversationSummary | null;
  onClose(): void;
}) {
  const chat = useNativeChat();
  const [title, setTitle] = React.useState("");
  React.useEffect(() => setTitle(conversation?.title ?? ""), [conversation]);
  const save = async () => {
    if (!conversation || !title.trim()) return;
    try {
      await chat.renameConversation(conversation.conversationId, title);
      onClose();
    } catch (error) {
      Alert.alert("Unable to rename", toMessage(error));
    }
  };
  return (
    <Modal
      transparent
      animationType="fade"
      visible={Boolean(conversation)}
      onRequestClose={onClose}
    >
      <View style={[styles.backdrop, styles.centerBackdrop]}>
        <View style={styles.dialog}>
          <Text style={styles.dialogTitle}>Rename conversation</Text>
          <TextInput
            autoFocus
            value={title}
            onChangeText={setTitle}
            selectTextOnFocus
            style={styles.input}
          />
          <View style={styles.dialogActions}>
            <Pressable onPress={onClose} style={styles.textButton}>
              <Text style={styles.textButtonLabel}>Cancel</Text>
            </Pressable>
            <Pressable onPress={() => void save()} style={styles.primaryButton}>
              <Text style={styles.primaryButtonLabel}>Save</Text>
            </Pressable>
          </View>
        </View>
      </View>
    </Modal>
  );
}

function formatConversationMeta(conversation: ConversationSummary): string {
  const date = new Date(conversation.updateTime ?? conversation.createTime);
  const time = Number.isNaN(date.getTime())
    ? (conversation.updateTime ?? conversation.createTime)
    : new Intl.DateTimeFormat(undefined, {
        month: "short",
        day: "numeric",
        hour: "numeric",
        minute: "2-digit",
      }).format(date);
  return `${time} · ${conversation.messageCount} messages`;
}

function toMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

const styles = StyleSheet.create({
  frame: { flex: 1, backgroundColor: theme.background },
  header: {
    paddingHorizontal: 16,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderColor: theme.border,
  },
  brand: { color: theme.ink, fontFamily: theme.fontSemibold, fontSize: 20 },
  projectSelector: {
    marginHorizontal: 16,
    marginTop: 14,
    minHeight: 58,
    paddingHorizontal: 15,
    borderRadius: 12,
    backgroundColor: theme.surface,
    flexDirection: "row",
    alignItems: "center",
  },
  projectCopy: { flex: 1 },
  eyebrow: { color: theme.muted, fontFamily: theme.fontSemibold, fontSize: 10, letterSpacing: 1.2 },
  projectName: { color: theme.ink, fontFamily: theme.fontMedium, fontSize: 15, marginTop: 3 },
  sectionHeader: {
    height: 58,
    paddingHorizontal: 18,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
  },
  sectionTitle: { color: theme.ink, fontFamily: theme.fontSemibold, fontSize: 18 },
  list: { paddingHorizontal: 12, paddingBottom: 16 },
  row: {
    minHeight: 68,
    paddingLeft: 14,
    paddingRight: 6,
    flexDirection: "row",
    alignItems: "center",
    borderRadius: 12,
  },
  rowActive: { backgroundColor: "#D7E8FF" },
  rowCopy: { flex: 1, minWidth: 0 },
  rowTitle: { color: theme.ink, fontFamily: theme.fontMedium, fontSize: 14 },
  rowMeta: { color: theme.muted, fontFamily: theme.fontRegular, fontSize: 11, marginTop: 5 },
  rowActions: { flexDirection: "row" },
  pressed: { opacity: 0.7 },
  empty: { alignItems: "center", paddingTop: 70 },
  emptyTitle: { color: theme.ink, fontFamily: theme.fontSemibold, fontSize: 16 },
  emptyText: { color: theme.muted, fontFamily: theme.fontRegular, fontSize: 13, marginTop: 6 },
  settingsRow: {
    minHeight: 58,
    paddingTop: 12,
    paddingHorizontal: 20,
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderColor: theme.border,
  },
  settingsText: { color: theme.ink, fontFamily: theme.fontMedium, fontSize: 14 },
  backdrop: { flex: 1, justifyContent: "flex-end", backgroundColor: "rgba(26, 27, 31, 0.32)" },
  centerBackdrop: { justifyContent: "center", padding: 24 },
  sheet: {
    padding: 18,
    paddingBottom: 30,
    borderTopLeftRadius: 22,
    borderTopRightRadius: 22,
    backgroundColor: theme.background,
  },
  option: { minHeight: 52, paddingHorizontal: 12, justifyContent: "center", borderRadius: 12 },
  optionActive: { backgroundColor: "#D7E8FF" },
  optionText: { color: theme.ink, fontFamily: theme.fontMedium, fontSize: 14 },
  dialog: { padding: 20, borderRadius: 16, backgroundColor: theme.background },
  dialogTitle: { color: theme.ink, fontFamily: theme.fontSemibold, fontSize: 18, marginBottom: 14 },
  input: {
    minHeight: 48,
    paddingHorizontal: 12,
    borderWidth: 1,
    borderColor: theme.border,
    borderRadius: 12,
    backgroundColor: theme.white,
    color: theme.ink,
    fontFamily: theme.fontRegular,
    fontSize: 14,
  },
  dialogActions: { flexDirection: "row", justifyContent: "flex-end", gap: 10, marginTop: 18 },
  textButton: {
    minHeight: 40,
    paddingHorizontal: 14,
    alignItems: "center",
    justifyContent: "center",
  },
  textButtonLabel: { color: theme.primary, fontFamily: theme.fontSemibold, fontSize: 13 },
  primaryButton: {
    minHeight: 40,
    paddingHorizontal: 20,
    alignItems: "center",
    justifyContent: "center",
    borderRadius: 999,
    backgroundColor: theme.primary,
  },
  primaryButtonLabel: { color: theme.white, fontFamily: theme.fontSemibold, fontSize: 13 },
});
