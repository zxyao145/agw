import type { ContextSummary } from "@agw/projects-core";
import { router } from "expo-router";
import { ChevronDown, Pencil, RefreshCw, Settings, Trash2, X } from "lucide-react-native";
import React from "react";
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
import { useSafeAreaInsets } from "react-native-safe-area-context";

import { AgwBrand } from "@/components/agw-brand";
import { IconButton } from "@/components/icon-button";
import { ScreenFrame } from "@/components/screen-frame";
import { useWorkspace } from "@/features/workspace/workspace-provider";
import { getErrorMessage } from "@/lib/errors";
import { colors, layout, radius, typography } from "@/theme/tokens";

export function HistoryScreen(): React.JSX.Element {
  const insets = useSafeAreaInsets();
  const workspace = useWorkspace();
  const [projectPickerOpen, setProjectPickerOpen] = React.useState(false);
  const [editing, setEditing] = React.useState<ContextSummary | null>(null);

  const selectContext = (contextId: string) => {
    try {
      workspace.selectContext(contextId);
      router.replace("/chat");
    } catch (error) {
      Alert.alert("Execution in progress", getErrorMessage(error));
    }
  };
  const selectProject = (projectId: string) => {
    try {
      workspace.selectProject(projectId);
      setProjectPickerOpen(false);
    } catch (error) {
      Alert.alert("Execution in progress", getErrorMessage(error));
    }
  };
  const remove = (context: ContextSummary) => {
    Alert.alert(
      `Delete “${context.title}”?`,
      "This permanently removes the conversation history.",
      [
        { text: "Cancel", style: "cancel" },
        {
          text: "Delete",
          style: "destructive",
          onPress: () => void workspace.deleteContext(context.contextId),
        },
      ],
    );
  };

  return (
    <ScreenFrame>
      <View
        style={[
          styles.header,
          { paddingTop: insets.top, height: layout.headerHeight + insets.top },
        ]}
      >
        <AgwBrand />
        <IconButton
          icon={X}
          label="Close history"
          color={colors.primary}
          onPress={() => router.back()}
        />
      </View>
      <Pressable onPress={() => setProjectPickerOpen(true)} style={styles.projectSelector}>
        <View style={styles.projectCopy}>
          <Text style={styles.eyebrow}>PROJECT</Text>
          <Text numberOfLines={1} style={styles.projectName}>
            {workspace.selectedProject?.name ?? "Select project"}
          </Text>
        </View>
        <ChevronDown color={colors.muted} size={18} />
      </Pressable>
      <View style={styles.sectionHeader}>
        <Text style={styles.sectionTitle}>Conversations</Text>
        <IconButton
          icon={RefreshCw}
          label="Refresh conversations"
          color={colors.primary}
          size={18}
          onPress={() => void workspace.refreshContexts()}
        />
      </View>
      <ScrollView contentContainerStyle={styles.list}>
        {workspace.contexts.length === 0 ? (
          <View style={styles.empty}>
            <Text style={styles.emptyTitle}>No conversations yet</Text>
            <Text style={styles.emptyText}>Start a new chat to create one.</Text>
          </View>
        ) : (
          workspace.contexts.map((context) => {
            const active = context.contextId === workspace.selectedContextId;
            return (
              <Pressable
                key={context.contextId}
                onPress={() => selectContext(context.contextId)}
                style={({ pressed }) => [
                  styles.row,
                  active && styles.rowActive,
                  pressed && styles.pressed,
                ]}
              >
                <View style={styles.rowCopy}>
                  <Text numberOfLines={1} style={styles.rowTitle}>
                    {context.title}
                  </Text>
                  <Text numberOfLines={1} style={styles.rowMeta}>
                    {formatContextMeta(context)}
                  </Text>
                </View>
                <View style={styles.rowActions}>
                  <IconButton
                    icon={Pencil}
                    label={`Rename ${context.title}`}
                    size={17}
                    disabled={workspace.isExecuting}
                    onPress={(event) => {
                      event.stopPropagation();
                      setEditing(context);
                    }}
                  />
                  <IconButton
                    icon={Trash2}
                    label={`Delete ${context.title}`}
                    size={17}
                    color={colors.danger}
                    disabled={workspace.isExecuting}
                    onPress={(event) => {
                      event.stopPropagation();
                      remove(context);
                    }}
                  />
                </View>
              </Pressable>
            );
          })
        )}
      </ScrollView>
      <Pressable
        onPress={() => router.push("/settings")}
        style={[styles.settingsRow, { paddingBottom: Math.max(14, insets.bottom) }]}
      >
        <Settings color={colors.muted} size={21} />
        <Text style={styles.settingsText}>Settings</Text>
      </Pressable>
      <ProjectPicker
        open={projectPickerOpen}
        onClose={() => setProjectPickerOpen(false)}
        onSelect={selectProject}
      />
      <RenameDialog context={editing} onClose={() => setEditing(null)} />
    </ScreenFrame>
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
}): React.JSX.Element {
  const workspace = useWorkspace();
  return (
    <Modal transparent animationType="fade" visible={open} onRequestClose={onClose}>
      <Pressable style={styles.backdrop} onPress={onClose}>
        <View style={styles.sheet}>
          <Text style={styles.dialogTitle}>Choose a project</Text>
          {workspace.projects.map((project) => (
            <Pressable
              key={project.id}
              onPress={() => onSelect(project.id)}
              style={[
                styles.option,
                project.id === workspace.selectedProjectId && styles.optionActive,
              ]}
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
  context,
  onClose,
}: {
  context: ContextSummary | null;
  onClose(): void;
}): React.JSX.Element {
  const workspace = useWorkspace();
  const [title, setTitle] = React.useState("");
  React.useEffect(() => setTitle(context?.title ?? ""), [context]);
  const save = async () => {
    if (!context || !title.trim()) return;
    try {
      await workspace.renameContext(context.contextId, title);
      onClose();
    } catch (error) {
      Alert.alert("Unable to rename", getErrorMessage(error));
    }
  };
  return (
    <Modal transparent animationType="fade" visible={Boolean(context)} onRequestClose={onClose}>
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

function formatContextMeta(context: ContextSummary): string {
  const date = new Date(context.updateTime ?? context.createTime);
  const time = Number.isNaN(date.getTime())
    ? (context.updateTime ?? context.createTime)
    : new Intl.DateTimeFormat(undefined, {
        month: "short",
        day: "numeric",
        hour: "numeric",
        minute: "2-digit",
      }).format(date);
  return `${time} · ${context.messageCount} messages`;
}

const styles = StyleSheet.create({
  header: {
    paddingHorizontal: 16,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderColor: colors.border,
  },
  projectSelector: {
    marginHorizontal: 16,
    marginTop: 14,
    minHeight: 58,
    paddingHorizontal: 15,
    borderRadius: radius.md,
    backgroundColor: colors.surface,
    flexDirection: "row",
    alignItems: "center",
  },
  projectCopy: { flex: 1 },
  eyebrow: {
    color: colors.muted,
    fontFamily: typography.semibold,
    fontSize: 10,
    letterSpacing: 1.2,
  },
  projectName: { color: colors.ink, fontFamily: typography.medium, fontSize: 15, marginTop: 3 },
  sectionHeader: {
    height: 58,
    paddingHorizontal: 18,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
  },
  sectionTitle: { color: colors.ink, fontFamily: typography.semibold, fontSize: 18 },
  list: { paddingHorizontal: 12, paddingBottom: 16 },
  row: {
    minHeight: 68,
    paddingLeft: 14,
    paddingRight: 6,
    flexDirection: "row",
    alignItems: "center",
    borderRadius: radius.md,
  },
  rowActive: { backgroundColor: colors.primarySoft },
  rowCopy: { flex: 1, minWidth: 0 },
  rowTitle: { color: colors.ink, fontFamily: typography.medium, fontSize: 14 },
  rowMeta: { color: colors.muted, fontFamily: typography.regular, fontSize: 11, marginTop: 5 },
  rowActions: { flexDirection: "row" },
  pressed: { opacity: 0.7 },
  empty: { alignItems: "center", paddingTop: 70 },
  emptyTitle: { color: colors.ink, fontFamily: typography.semibold, fontSize: 16 },
  emptyText: { color: colors.muted, fontFamily: typography.regular, fontSize: 13, marginTop: 6 },
  settingsRow: {
    minHeight: 58,
    paddingTop: 12,
    paddingHorizontal: 20,
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderColor: colors.border,
  },
  settingsText: { color: colors.ink, fontFamily: typography.medium, fontSize: 14 },
  backdrop: { flex: 1, justifyContent: "flex-end", backgroundColor: colors.overlay },
  centerBackdrop: { justifyContent: "center", padding: 24 },
  sheet: {
    padding: 18,
    paddingBottom: 30,
    borderTopLeftRadius: 22,
    borderTopRightRadius: 22,
    backgroundColor: colors.background,
  },
  option: {
    minHeight: 52,
    paddingHorizontal: 12,
    justifyContent: "center",
    borderRadius: radius.md,
  },
  optionActive: { backgroundColor: colors.primarySoft },
  optionText: { color: colors.ink, fontFamily: typography.medium, fontSize: 14 },
  dialog: { padding: 20, borderRadius: radius.lg, backgroundColor: colors.background },
  dialogTitle: {
    color: colors.ink,
    fontFamily: typography.semibold,
    fontSize: 18,
    marginBottom: 14,
  },
  input: {
    minHeight: 48,
    paddingHorizontal: 12,
    borderWidth: 1,
    borderColor: colors.border,
    borderRadius: radius.md,
    backgroundColor: colors.white,
    color: colors.ink,
    fontFamily: typography.regular,
    fontSize: 14,
  },
  dialogActions: { flexDirection: "row", justifyContent: "flex-end", gap: 10, marginTop: 18 },
  textButton: {
    minHeight: 40,
    paddingHorizontal: 14,
    alignItems: "center",
    justifyContent: "center",
  },
  textButtonLabel: { color: colors.primary, fontFamily: typography.semibold, fontSize: 13 },
  primaryButton: {
    minHeight: 40,
    paddingHorizontal: 20,
    alignItems: "center",
    justifyContent: "center",
    borderRadius: radius.pill,
    backgroundColor: colors.primary,
  },
  primaryButtonLabel: { color: colors.white, fontFamily: typography.semibold, fontSize: 13 },
});
