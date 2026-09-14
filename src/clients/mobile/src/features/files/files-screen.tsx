import { getProjectDirectories, type FileItem } from "@agw/projects-core";
import { router } from "expo-router";
import {
  ChevronDown,
  ChevronRight,
  File,
  FileCode2,
  Folder,
  FolderOpen,
  RefreshCw,
} from "lucide-react-native";
import React from "react";
import {
  ActivityIndicator,
  Alert,
  FlatList,
  Pressable,
  StyleSheet,
  Switch,
  Text,
  View,
} from "react-native";

import { IconButton } from "@/components/icon-button";
import { useWorkspace } from "@/features/workspace/workspace-provider";
import type { WorkspacePaneHandle } from "@/features/workspace/workspace-types";
import { getErrorMessage } from "@/lib/errors";
import { colors, radius, typography } from "@/theme/tokens";

type TreeItem = FileItem & { depth: number };

export const FilesScreen = React.forwardRef<WorkspacePaneHandle>(function FilesScreen(_, ref) {
  const workspace = useWorkspace();
  const [onlyChanged, setOnlyChanged] = React.useState(true);
  const [items, setItems] = React.useState<FileItem[]>([]);
  const [expanded, setExpanded] = React.useState<Set<string>>(new Set());
  const [loading, setLoading] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const listRef = React.useRef<FlatList<TreeItem>>(null);
  const [directoryMenuOpen, setDirectoryMenuOpen] = React.useState(false);
  const directories = workspace.selectedProject
    ? getProjectDirectories(workspace.selectedProject)
    : [];
  const selectedDirectory =
    directories.find((directory) => directory.id === workspace.selectedDirectoryId) ??
    directories[0];
  const directoryId = selectedDirectory?.id;
  const loadGenerationRef = React.useRef(0);
  const selectionKey = `${workspace.selectedProjectId}:${directoryId ?? "primary"}:${selectedDirectory?.path ?? ""}`;
  const currentSelectionRef = React.useRef(selectionKey);
  currentSelectionRef.current = selectionKey;

  const load = React.useCallback(async () => {
    if (!workspace.filesService || !workspace.selectedProjectId) {
      setItems([]);
      return;
    }
    if (currentSelectionRef.current !== selectionKey) return;
    const generation = ++loadGenerationRef.current;
    setLoading(true);
    setError(null);
    try {
      const response = await workspace.filesService.listFiles(
        workspace.selectedProjectId,
        "",
        onlyChanged,
        true,
        directoryId,
      );
      if (generation !== loadGenerationRef.current || currentSelectionRef.current !== selectionKey)
        return;
      const tree = onlyChanged ? buildFileTree(response.items) : response.items;
      setItems(tree);
      setExpanded(onlyChanged ? new Set(collectDirectories(tree)) : new Set());
    } catch (caught) {
      if (generation !== loadGenerationRef.current || currentSelectionRef.current !== selectionKey)
        return;
      setItems([]);
      setError(getErrorMessage(caught));
    } finally {
      if (generation === loadGenerationRef.current && currentSelectionRef.current === selectionKey)
        setLoading(false);
    }
  }, [onlyChanged, workspace.filesService, workspace.selectedProjectId, directoryId, selectionKey]);

  React.useEffect(() => {
    setItems([]);
    setDirectoryMenuOpen(false);
    void load();
    return () => {
      loadGenerationRef.current += 1;
    };
  }, [load]);

  React.useImperativeHandle(
    ref,
    () => ({
      scrollToTop: () => listRef.current?.scrollToOffset({ offset: 0, animated: true }),
    }),
    [],
  );

  const toggleDirectory = async (item: FileItem) => {
    if (expanded.has(item.path)) {
      setExpanded((current) => {
        const next = new Set(current);
        next.delete(item.path);
        return next;
      });
      return;
    }
    if (!item.children && workspace.filesService && workspace.selectedProjectId) {
      try {
        const response = await workspace.filesService.listFiles(
          workspace.selectedProjectId,
          item.path,
          onlyChanged,
          false,
          directoryId,
        );
        if (currentSelectionRef.current !== selectionKey) return;
        setItems((current) => updateChildren(current, item.path, response.items));
      } catch (caught) {
        if (currentSelectionRef.current === selectionKey) setError(getErrorMessage(caught));
        return;
      }
    }
    setExpanded((current) => new Set(current).add(item.path));
  };

  const action = (item: FileItem) => {
    const buttons = [
      ...(item.type === "file"
        ? [
            {
              text: "Reset to HEAD",
              onPress: () => confirmReset(item),
            },
          ]
        : []),
      { text: "Delete", style: "destructive" as const, onPress: () => confirmDelete(item) },
      { text: "Cancel", style: "cancel" as const },
    ];
    Alert.alert(item.name, undefined, buttons);
  };
  const confirmDelete = (item: FileItem) =>
    Alert.alert(`Delete ${item.name}?`, "This cannot be undone.", [
      { text: "Cancel", style: "cancel" },
      {
        text: "Delete",
        style: "destructive",
        onPress: () =>
          void workspace
            .filesService!.deleteFile(workspace.selectedProjectId!, item.path, directoryId)
            .then(load)
            .catch((caught) => {
              if (currentSelectionRef.current === selectionKey) setError(getErrorMessage(caught));
            }),
      },
    ]);
  const confirmReset = (item: FileItem) =>
    Alert.alert(`Reset ${item.name}?`, "All uncommitted changes in this file will be discarded.", [
      { text: "Cancel", style: "cancel" },
      {
        text: "Reset",
        style: "destructive",
        onPress: () =>
          void workspace
            .filesService!.resetFile(workspace.selectedProjectId!, item.path, directoryId)
            .then(load)
            .catch((caught) => {
              if (currentSelectionRef.current === selectionKey) setError(getErrorMessage(caught));
            }),
      },
    ]);

  const flattened = React.useMemo(() => flattenTree(items, expanded), [expanded, items]);
  return (
    <>
      <View style={styles.titleRow}>
        <View>
          <Text style={styles.title}>Project files</Text>
          <Text numberOfLines={1} style={styles.subtitle}>
            {workspace.selectedProject?.name ?? "No project selected"}
          </Text>
        </View>
        <View style={styles.changedControl}>
          <Text style={styles.changedLabel}>Changed</Text>
          <Switch
            style={styles.changedSwitch}
            value={onlyChanged}
            onValueChange={setOnlyChanged}
            trackColor={{ false: colors.border, true: colors.primarySoft }}
            thumbColor={onlyChanged ? colors.primary : colors.white}
          />
          <IconButton
            icon={RefreshCw}
            label="Refresh files"
            size={18}
            onPress={() => void load()}
          />
        </View>
      </View>
      {directories.length > 1 && (
        <View style={{ paddingHorizontal: 20, paddingBottom: 12 }}>
          <Pressable
            accessibilityRole="button"
            accessibilityLabel="Select project directory"
            accessibilityState={{ expanded: directoryMenuOpen }}
            onPress={() => setDirectoryMenuOpen((open) => !open)}
            style={{
              borderWidth: 1,
              borderColor: colors.border,
              borderRadius: 8,
              padding: 10,
              flexDirection: "row",
              justifyContent: "space-between",
            }}
          >
            <View style={{ flex: 1 }}>
              <Text style={{ color: colors.ink }}>
                {selectedDirectory?.name}
                {selectedDirectory?.isPrimary ? " · Primary" : ""}
              </Text>
              <Text numberOfLines={1} style={styles.subtitle}>
                {selectedDirectory?.path}
              </Text>
            </View>
            <ChevronDown size={18} color={colors.primary} />
          </Pressable>
          {directoryMenuOpen &&
            directories.map((directory) => (
              <Pressable
                key={directory.id ?? "primary"}
                accessibilityRole="button"
                onPress={() => {
                  workspace.selectDirectory(directory.id);
                  setDirectoryMenuOpen(false);
                }}
                style={{ padding: 10, borderBottomWidth: 1, borderBottomColor: colors.border }}
              >
                <Text style={{ color: directory.id === directoryId ? colors.primary : colors.ink }}>
                  {directory.name}
                  {directory.isPrimary ? " · Primary" : ""}
                </Text>
                <Text numberOfLines={1} style={styles.subtitle}>
                  {directory.path}
                </Text>
              </Pressable>
            ))}
        </View>
      )}
      {loading ? (
        <View style={styles.center}>
          <ActivityIndicator color={colors.primary} />
        </View>
      ) : error ? (
        <View style={styles.center}>
          <Text style={styles.error}>{error}</Text>
        </View>
      ) : flattened.length === 0 ? (
        <View style={styles.center}>
          <Text style={styles.empty}>
            {onlyChanged ? "No changed files" : "This project has no files"}
          </Text>
        </View>
      ) : (
        <FlatList
          ref={listRef}
          data={flattened}
          keyExtractor={(item) => item.path}
          contentContainerStyle={styles.list}
          renderItem={({ item }) => (
            <FileRow
              item={item}
              expanded={expanded.has(item.path)}
              onPress={() =>
                item.type === "directory"
                  ? void toggleDirectory(item)
                  : router.push({
                      pathname: "/file-preview",
                      params: {
                        path: item.path,
                        diff: String(onlyChanged),
                        projectId: workspace.selectedProjectId!,
                        directoryId: directoryId ?? "",
                      },
                    })
              }
              onLongPress={() => action(item)}
            />
          )}
        />
      )}
    </>
  );
});

function FileRow({
  item,
  expanded,
  onPress,
  onLongPress,
}: {
  item: TreeItem;
  expanded: boolean;
  onPress(): void;
  onLongPress(): void;
}): React.JSX.Element {
  const directory = item.type === "directory";
  const status = item.gitUnstagedStatus ?? item.gitStagedStatus ?? item.gitStatus;
  return (
    <Pressable
      onPress={onPress}
      onLongPress={onLongPress}
      style={({ pressed }) => [
        styles.fileRow,
        { paddingLeft: 10 + item.depth * 18 },
        pressed && styles.pressed,
      ]}
    >
      {directory ? (
        expanded ? (
          <ChevronDown color={colors.muted} size={15} />
        ) : (
          <ChevronRight color={colors.muted} size={15} />
        )
      ) : (
        <View style={styles.chevronPlaceholder} />
      )}
      {directory ? (
        expanded ? (
          <FolderOpen color={colors.primary} size={20} />
        ) : (
          <Folder color={colors.primary} size={20} />
        )
      ) : item.name.match(/\.(ts|tsx|js|jsx|cs|json|md)$/iu) ? (
        <FileCode2 color={colors.muted} size={19} />
      ) : (
        <File color={colors.muted} size={19} />
      )}
      <View style={styles.fileCopy}>
        <Text numberOfLines={1} style={styles.fileName}>
          {item.name}
        </Text>
        {!directory ? (
          <Text numberOfLines={1} style={styles.fileMeta}>
            {formatSize(item.size)}
            {item.modifiedTime ? ` · ${formatTime(item.modifiedTime)}` : ""}
          </Text>
        ) : null}
      </View>
      {status ? (
        <Text style={[styles.status, status === "deleted" && styles.statusDeleted]}>
          {statusLetter(status)}
        </Text>
      ) : null}
    </Pressable>
  );
}

function flattenTree(items: FileItem[], expanded: Set<string>, depth = 0): TreeItem[] {
  const result: TreeItem[] = [];
  for (const item of items) {
    result.push({ ...item, depth });
    if (item.type === "directory" && expanded.has(item.path) && item.children) {
      result.push(...flattenTree(item.children, expanded, depth + 1));
    }
  }
  return result;
}

function buildFileTree(items: FileItem[]): FileItem[] {
  const roots: FileItem[] = [];
  const directories = new Map<string, FileItem>();
  const sorted = [...items].sort((a, b) => a.path.localeCompare(b.path));
  for (const item of sorted) {
    const parts = item.path.split("/").filter(Boolean);
    let parentChildren = roots;
    let currentPath = "";
    for (let index = 0; index < parts.length - 1; index += 1) {
      currentPath = currentPath ? `${currentPath}/${parts[index]}` : parts[index];
      let directory = directories.get(currentPath);
      if (!directory) {
        directory = { name: parts[index], path: currentPath, type: "directory", children: [] };
        directories.set(currentPath, directory);
        parentChildren.push(directory);
      }
      directory.children ??= [];
      parentChildren = directory.children;
    }
    const next = {
      ...item,
      children: item.children ? [...item.children] : item.type === "directory" ? [] : undefined,
    };
    parentChildren.push(next);
    if (next.type === "directory") directories.set(next.path, next);
  }
  return roots;
}

function collectDirectories(items: FileItem[]): string[] {
  return items.flatMap((item) =>
    item.type === "directory" ? [item.path, ...collectDirectories(item.children ?? [])] : [],
  );
}

function updateChildren(items: FileItem[], path: string, children: FileItem[]): FileItem[] {
  return items.map((item) =>
    item.path === path
      ? { ...item, children }
      : item.children
        ? { ...item, children: updateChildren(item.children, path, children) }
        : item,
  );
}

function formatSize(size?: number): string {
  if (size == null) return "";
  if (size < 1024) return `${size} B`;
  if (size < 1024 * 1024) return `${(size / 1024).toFixed(1)} KB`;
  return `${(size / (1024 * 1024)).toFixed(1)} MB`;
}
function formatTime(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? value
    : new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric" }).format(date);
}
function statusLetter(value: string): string {
  return value === "added" ? "A" : value === "deleted" ? "D" : value === "untracked" ? "U" : "M";
}

const styles = StyleSheet.create({
  titleRow: {
    minHeight: 66,
    paddingHorizontal: 16,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderColor: colors.border,
  },
  title: { color: colors.ink, fontFamily: typography.semibold, fontSize: 16 },
  subtitle: {
    maxWidth: 150,
    color: colors.muted,
    fontFamily: typography.regular,
    fontSize: 11,
    marginTop: 3,
  },
  changedControl: { flexDirection: "row", alignItems: "center", gap: 4 },
  changedLabel: { color: colors.muted, fontFamily: typography.medium, fontSize: 11 },
  changedSwitch: { alignSelf: "center" },
  list: { padding: 8, paddingBottom: 20 },
  fileRow: {
    minHeight: 52,
    paddingRight: 10,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    borderRadius: radius.sm,
  },
  pressed: { backgroundColor: colors.surface },
  chevronPlaceholder: { width: 15 },
  fileCopy: { flex: 1, minWidth: 0 },
  fileName: { color: colors.ink, fontFamily: typography.medium, fontSize: 13 },
  fileMeta: { color: colors.subtle, fontFamily: typography.regular, fontSize: 10, marginTop: 3 },
  status: { color: colors.primary, fontFamily: typography.semibold, fontSize: 11 },
  statusDeleted: { color: colors.danger },
  center: { flex: 1, alignItems: "center", justifyContent: "center", padding: 24 },
  error: {
    color: colors.danger,
    fontFamily: typography.regular,
    fontSize: 13,
    textAlign: "center",
  },
  empty: { color: colors.muted, fontFamily: typography.regular, fontSize: 13 },
});
