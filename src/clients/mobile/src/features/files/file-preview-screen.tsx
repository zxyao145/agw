import { router } from "expo-router";
import { ChevronLeft, MoreHorizontal, Trash2 } from "lucide-react-native";
import React from "react";
import {
  ActivityIndicator,
  Alert,
  FlatList,
  Modal,
  Pressable,
  StyleSheet,
  Text,
  TextInput,
  View,
} from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";

import { IconButton } from "@/components/icon-button";
import { ScreenFrame } from "@/components/screen-frame";
import { useWorkspace } from "@/features/workspace/workspace-provider";
import { getErrorMessage } from "@/lib/errors";
import { colors, layout, radius, typography } from "@/theme/tokens";

type LineComment = { id: string; line: number; text: string };
type PreviewLine = {
  key: string;
  line: number;
  text: string;
  kind: "normal" | "added" | "deleted" | "meta";
};

export function FilePreviewScreen({
  path,
  diff,
}: {
  path: string;
  diff: boolean;
}): React.JSX.Element {
  const insets = useSafeAreaInsets();
  const workspace = useWorkspace();
  const [lines, setLines] = React.useState<PreviewLine[]>([]);
  const [comments, setComments] = React.useState<LineComment[]>([]);
  const [selectedLine, setSelectedLine] = React.useState<number | null>(null);
  const [loading, setLoading] = React.useState(true);
  const [error, setError] = React.useState<string | null>(null);

  const load = React.useCallback(async () => {
    if (!workspace.filesService || !workspace.selectedProjectId || !path) return;
    setLoading(true);
    setError(null);
    try {
      const content = diff
        ? (await workspace.filesService.getFileDiff(workspace.selectedProjectId, path)).diff
        : await workspace.filesService.readFile(workspace.selectedProjectId, path);
      setLines(toPreviewLines(content, diff));
    } catch (caught) {
      setError(getErrorMessage(caught));
    } finally {
      setLoading(false);
    }
  }, [diff, path, workspace.filesService, workspace.selectedProjectId]);
  React.useEffect(() => {
    void load();
  }, [load]);

  const deleteFile = () =>
    Alert.alert(`Delete ${fileName(path)}?`, "This cannot be undone.", [
      { text: "Cancel", style: "cancel" },
      {
        text: "Delete",
        style: "destructive",
        onPress: () =>
          void workspace
            .filesService!.deleteFile(workspace.selectedProjectId!, path)
            .then(() => router.back())
            .catch((caught) => setError(getErrorMessage(caught))),
      },
    ]);
  const resetFile = () =>
    Alert.alert(
      `Reset ${fileName(path)}?`,
      "All uncommitted changes in this file will be discarded.",
      [
        { text: "Cancel", style: "cancel" },
        {
          text: "Reset",
          style: "destructive",
          onPress: () =>
            void workspace
              .filesService!.resetFile(workspace.selectedProjectId!, path)
              .then(load)
              .catch((caught) => setError(getErrorMessage(caught))),
        },
      ],
    );
  const showActions = () =>
    Alert.alert(fileName(path), undefined, [
      ...(diff ? [{ text: "Reset to HEAD", onPress: resetFile }] : []),
      { text: "Delete", style: "destructive", onPress: deleteFile },
      { text: "Cancel", style: "cancel" },
    ]);

  return (
    <ScreenFrame>
      <View
        style={[
          styles.header,
          { paddingTop: insets.top, height: layout.headerHeight + insets.top },
        ]}
      >
        <IconButton
          icon={ChevronLeft}
          label="Back to files"
          color={colors.primary}
          onPress={() => router.back()}
        />
        <View style={styles.headerCopy}>
          <Text numberOfLines={1} style={styles.title}>
            {fileName(path)}
          </Text>
          <Text numberOfLines={1} style={styles.path}>
            {path}
          </Text>
        </View>
        <IconButton icon={MoreHorizontal} label="File actions" onPress={showActions} />
      </View>
      {loading ? (
        <View style={styles.center}>
          <ActivityIndicator color={colors.primary} />
        </View>
      ) : error ? (
        <View style={styles.center}>
          <Text style={styles.error}>{error}</Text>
        </View>
      ) : (
        <FlatList
          data={lines}
          keyExtractor={(item) => item.key}
          contentContainerStyle={[styles.lines, { paddingBottom: insets.bottom + 24 }]}
          horizontal={false}
          renderItem={({ item }) => (
            <View>
              <Pressable
                onPress={() => item.kind !== "meta" && setSelectedLine(item.line)}
                style={[
                  styles.line,
                  item.kind === "added" && styles.added,
                  item.kind === "deleted" && styles.deleted,
                  item.kind === "meta" && styles.meta,
                ]}
              >
                <Text style={styles.lineNumber}>{item.kind === "meta" ? "" : item.line}</Text>
                <Text selectable style={[styles.lineText, item.kind === "meta" && styles.metaText]}>
                  {item.text || " "}
                </Text>
              </Pressable>
              {comments
                .filter((comment) => comment.line === item.line)
                .map((comment) => (
                  <View key={comment.id} style={styles.comment}>
                    <Text style={styles.commentText}>{comment.text}</Text>
                    <IconButton
                      icon={Trash2}
                      label="Delete line comment"
                      color={colors.danger}
                      size={15}
                      onPress={() =>
                        setComments((current) => current.filter((item) => item.id !== comment.id))
                      }
                    />
                  </View>
                ))}
            </View>
          )}
        />
      )}
      <CommentDialog
        line={selectedLine}
        onClose={() => setSelectedLine(null)}
        onSave={(text) => {
          setComments((current) => [
            ...current,
            { id: `${Date.now()}-${selectedLine}`, line: selectedLine!, text },
          ]);
          setSelectedLine(null);
        }}
      />
    </ScreenFrame>
  );
}

function CommentDialog({
  line,
  onClose,
  onSave,
}: {
  line: number | null;
  onClose(): void;
  onSave(text: string): void;
}): React.JSX.Element {
  const [text, setText] = React.useState("");
  React.useEffect(() => setText(""), [line]);
  return (
    <Modal transparent animationType="fade" visible={line !== null} onRequestClose={onClose}>
      <View style={styles.backdrop}>
        <View style={styles.dialog}>
          <Text style={styles.dialogTitle}>Comment on line {line}</Text>
          <TextInput
            autoFocus
            multiline
            value={text}
            onChangeText={setText}
            placeholder="Write a comment…"
            placeholderTextColor={colors.subtle}
            style={styles.commentInput}
          />
          <View style={styles.dialogActions}>
            <Pressable onPress={onClose} style={styles.cancelButton}>
              <Text style={styles.cancelText}>Cancel</Text>
            </Pressable>
            <Pressable
              disabled={!text.trim()}
              onPress={() => onSave(text.trim())}
              style={[styles.saveButton, !text.trim() && styles.disabled]}
            >
              <Text style={styles.saveText}>Save</Text>
            </Pressable>
          </View>
        </View>
      </View>
    </Modal>
  );
}

function toPreviewLines(content: string, diff: boolean): PreviewLine[] {
  let lineNumber = 0;
  return content.split("\n").map((text, index) => {
    const meta =
      diff &&
      (text.startsWith("diff --git") ||
        text.startsWith("index ") ||
        text.startsWith("@@") ||
        text.startsWith("---") ||
        text.startsWith("+++"));
    const kind: PreviewLine["kind"] = meta
      ? "meta"
      : diff && text.startsWith("+")
        ? "added"
        : diff && text.startsWith("-")
          ? "deleted"
          : "normal";
    if (!meta) lineNumber += 1;
    return { key: `${index}-${text}`, line: lineNumber, text, kind };
  });
}

function fileName(path: string): string {
  return path.split("/").at(-1) || path || "File";
}

const styles = StyleSheet.create({
  header: {
    paddingHorizontal: 8,
    flexDirection: "row",
    alignItems: "center",
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderColor: colors.border,
  },
  headerCopy: { flex: 1, alignItems: "center", minWidth: 0 },
  title: { color: colors.ink, fontFamily: typography.semibold, fontSize: 15 },
  path: {
    maxWidth: "92%",
    color: colors.muted,
    fontFamily: typography.regular,
    fontSize: 10,
    marginTop: 2,
  },
  center: { flex: 1, alignItems: "center", justifyContent: "center", padding: 24 },
  error: {
    color: colors.danger,
    fontFamily: typography.regular,
    fontSize: 13,
    textAlign: "center",
  },
  lines: { paddingVertical: 10, backgroundColor: colors.white },
  line: { minHeight: 24, flexDirection: "row", paddingRight: 10 },
  lineNumber: {
    width: 45,
    paddingRight: 9,
    color: colors.subtle,
    fontFamily: "Menlo",
    fontSize: 10,
    lineHeight: 20,
    textAlign: "right",
    backgroundColor: colors.surface,
  },
  lineText: {
    flex: 1,
    paddingLeft: 9,
    color: colors.ink,
    fontFamily: "Menlo",
    fontSize: 11,
    lineHeight: 20,
  },
  added: { backgroundColor: colors.successSoft },
  deleted: { backgroundColor: colors.dangerSoft },
  meta: { backgroundColor: colors.primarySoft, paddingHorizontal: 10, marginTop: 4 },
  metaText: { color: colors.primary, fontFamily: "Menlo" },
  comment: {
    marginLeft: 45,
    paddingLeft: 10,
    minHeight: 46,
    flexDirection: "row",
    alignItems: "center",
    backgroundColor: colors.warningSoft,
  },
  commentText: {
    flex: 1,
    color: colors.warning,
    fontFamily: typography.regular,
    fontSize: 12,
    lineHeight: 17,
  },
  backdrop: {
    flex: 1,
    padding: 24,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: colors.overlay,
  },
  dialog: {
    width: "100%",
    padding: 20,
    borderRadius: radius.lg,
    backgroundColor: colors.background,
  },
  dialogTitle: { color: colors.ink, fontFamily: typography.semibold, fontSize: 17 },
  commentInput: {
    minHeight: 90,
    marginTop: 14,
    padding: 12,
    borderWidth: 1,
    borderColor: colors.border,
    borderRadius: radius.md,
    backgroundColor: colors.white,
    color: colors.ink,
    fontFamily: typography.regular,
    fontSize: 14,
    textAlignVertical: "top",
  },
  dialogActions: { marginTop: 14, flexDirection: "row", justifyContent: "flex-end", gap: 10 },
  cancelButton: { minHeight: 40, paddingHorizontal: 14, justifyContent: "center" },
  cancelText: { color: colors.primary, fontFamily: typography.semibold, fontSize: 13 },
  saveButton: {
    minHeight: 40,
    paddingHorizontal: 20,
    alignItems: "center",
    justifyContent: "center",
    borderRadius: radius.pill,
    backgroundColor: colors.primary,
  },
  saveText: { color: colors.white, fontFamily: typography.semibold, fontSize: 13 },
  disabled: { opacity: 0.4 },
});
