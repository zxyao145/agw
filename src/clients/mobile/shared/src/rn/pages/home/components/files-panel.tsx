import React from "react";
import {
  ActivityIndicator,
  Pressable,
  ScrollView,
  Switch,
  Text,
  TextInput,
  View,
} from "react-native";
import type { AgwApiClient } from "../../../api/agw-api-client";
import type { AgwFileItem, AgwGitDiffResponse } from "../../../api/agw-api-types";
import {
  deleteFile,
  getFileDiff,
  listFiles,
  readFile,
  resetFile,
} from "../../../api/files";
import { Icon } from "./icons";
import { styles } from "./styles";
import { colors } from "./tokens";
import type { IconName } from "./icons";

type FileIconName = Extract<IconName, "fileImage" | "filePdf" | "fileSheet">;
type FileItem = AgwFileItem & { children?: FileItem[] };
type CommentSide = "current" | "original" | "modified";

type LineComment = {
  content: string;
  filePath: string;
  id: string;
  lineNumber: number;
  side: CommentSide;
  timestamp: Date;
};

type ActionTarget = {
  item: FileItem;
};

const FILE_TYPE = {
  Directory: "directory",
  File: "file",
} as const;

const GIT_STATUS_LABEL: Record<string, string> = {
  added: "A",
  deleted: "D",
  modified: "M",
  untracked: "U",
};

const COMMENT_SIDE_LABEL: Record<CommentSide, string> = {
  current: "current",
  modified: "modified",
  original: "original",
};

export function FilesPanel({
  apiClient,
  dependenciesError,
  isDependenciesLoading = false,
  projectId,
}: {
  apiClient: AgwApiClient | null;
  dependenciesError?: string | null;
  isDependenciesLoading?: boolean;
  projectId?: string | null;
}): React.JSX.Element {
  const [onlyDiff, setOnlyDiff] = React.useState(true);
  const [rootItems, setRootItems] = React.useState<FileItem[]>([]);
  const [childItems, setChildItems] = React.useState<Record<string, FileItem[]>>({});
  const [expandedPaths, setExpandedPaths] = React.useState<Set<string>>(
    () => new Set()
  );
  const [selectedFile, setSelectedFile] = React.useState<string | null>(null);
  const [fileContent, setFileContent] = React.useState("");
  const [diffContentData, setDiffContentData] =
    React.useState<AgwGitDiffResponse | null>(null);
  const [comments, setComments] = React.useState<LineComment[]>([]);
  const [actionTarget, setActionTarget] = React.useState<ActionTarget | null>(null);
  const [statusMessage, setStatusMessage] = React.useState<string | null>(null);
  const [isExplorerCollapsed, setIsExplorerCollapsed] = React.useState(false);
  const [isPreviewCollapsed, setIsPreviewCollapsed] = React.useState(false);
  const [isExplorerLoading, setIsExplorerLoading] = React.useState(false);
  const [isContentLoading, setIsContentLoading] = React.useState(false);
  const [explorerError, setExplorerError] = React.useState<string | null>(null);
  const [contentError, setContentError] = React.useState<string | null>(null);

  const recursiveMode = true;

  const rootLoadGenerationRef = React.useRef(0);
  const contentLoadGenerationRef = React.useRef(0);
  const childrenLoadGenerationsRef = React.useRef<Record<string, number>>({});

  const loadRootDirectory = React.useCallback(async () => {
    if (!apiClient || !projectId) {
      setRootItems([]);
      setExplorerError(apiClient ? "No project selected" : null);
      return;
    }

    const generation = ++rootLoadGenerationRef.current;
    setIsExplorerLoading(true);
    setExplorerError(null);
    setStatusMessage(null);

    try {
      const data = await listFiles(
        apiClient,
        projectId,
        "",
        onlyDiff,
        recursiveMode
      );
      if (generation !== rootLoadGenerationRef.current) {
        return;
      }
      const nextItems =
        onlyDiff && recursiveMode
          ? buildFileTree(data.items ?? [], "")
          : data.items ?? [];

      setRootItems(nextItems);
      setChildItems({});
      setExpandedPaths(
        onlyDiff && recursiveMode ? collectDirectoryPaths(nextItems) : new Set()
      );
    } catch (error) {
      if (generation !== rootLoadGenerationRef.current) {
        return;
      }
      setRootItems([]);
      setExplorerError(`Failed to load files: ${getErrorMessage(error)}`);
    } finally {
      if (generation === rootLoadGenerationRef.current) {
        setIsExplorerLoading(false);
      }
    }
  }, [apiClient, onlyDiff, projectId]);

  const loadFileContent = React.useCallback(
    async (filePath: string) => {
      if (!apiClient || !projectId) {
        return;
      }

      const generation = ++contentLoadGenerationRef.current;
      setSelectedFile(filePath);
      setIsContentLoading(true);
      setContentError(null);
      setStatusMessage(null);

      try {
        if (onlyDiff) {
          const diff = await getFileDiff(apiClient, projectId, filePath);
          if (generation !== contentLoadGenerationRef.current) {
            return;
          }
          setDiffContentData(diff);
          setFileContent("");
        } else {
          const content = await readFile(apiClient, projectId, filePath);
          if (generation !== contentLoadGenerationRef.current) {
            return;
          }
          setFileContent(content);
          setDiffContentData(null);
        }
      } catch (error) {
        if (generation !== contentLoadGenerationRef.current) {
          return;
        }
        setContentError(getErrorMessage(error));
        setDiffContentData(null);
        setFileContent("");
      } finally {
        if (generation === contentLoadGenerationRef.current) {
          setIsContentLoading(false);
        }
      }
    },
    [apiClient, onlyDiff, projectId]
  );

  const loadDirectoryChildren = React.useCallback(
    async (item: FileItem) => {
      if (!apiClient || !projectId || item.type !== FILE_TYPE.Directory || childItems[item.path]) {
        return;
      }

      const generation =
        (childrenLoadGenerationsRef.current[item.path] ?? 0) + 1;
      childrenLoadGenerationsRef.current[item.path] = generation;

      setChildItems((current) => ({
        ...current,
        [item.path]: [],
      }));

      try {
        const data = await listFiles(
          apiClient,
          projectId,
          item.path,
          onlyDiff,
          recursiveMode
        );
        if (generation !== childrenLoadGenerationsRef.current[item.path]) {
          return;
        }
        setChildItems((current) => ({
          ...current,
          [item.path]: data.items ?? [],
        }));
      } catch (error) {
        if (generation !== childrenLoadGenerationsRef.current[item.path]) {
          return;
        }
        setExplorerError(`Failed to load directory: ${getErrorMessage(error)}`);
      }
    },
    [apiClient, childItems, onlyDiff, projectId]
  );

  const clearFileContent = React.useCallback(() => {
    contentLoadGenerationRef.current += 1;
    setSelectedFile(null);
    setFileContent("");
    setDiffContentData(null);
    setContentError(null);
    setIsContentLoading(false);
  }, []);

  React.useEffect(() => {
    clearFileContent();
    setComments([]);
    setActionTarget(null);
    void loadRootDirectory();
  }, [clearFileContent, loadRootDirectory]);

  React.useEffect(() => {
    if (selectedFile) {
      void loadFileContent(selectedFile);
    }
  }, [loadFileContent, selectedFile]);

  const handleToggleDiff = React.useCallback((value: boolean) => {
    setOnlyDiff(value);
    clearFileContent();
  }, [clearFileContent]);

  const handleDirectoryPress = React.useCallback(
    (item: FileItem) => {
      setExpandedPaths((current) => {
        const next = new Set(current);

        if (next.has(item.path)) {
          next.delete(item.path);
        } else {
          next.add(item.path);
          void loadDirectoryChildren(item);
        }

        return next;
      });
    },
    [loadDirectoryChildren]
  );

  const handleNodePress = React.useCallback(
    (item: FileItem) => {
      if (item.type === FILE_TYPE.Directory) {
        handleDirectoryPress(item);
        return;
      }

      setSelectedFile((currentFile) =>
        currentFile === item.path ? currentFile : item.path
      );
    },
    [handleDirectoryPress]
  );

  const handleDelete = React.useCallback(async () => {
    if (!apiClient || !projectId || !actionTarget) {
      return;
    }

    const targetPath = actionTarget.item.path;
    setActionTarget(null);
    setStatusMessage(null);

    try {
      const result = await deleteFile(apiClient, projectId, targetPath);

      if (result.success) {
        if (selectedFile === targetPath) {
          clearFileContent();
        }
        setStatusMessage(result.message);
        await loadRootDirectory();
      } else {
        setStatusMessage(result.message || "Failed to delete");
      }
    } catch (error) {
      setStatusMessage(`Failed to delete: ${getErrorMessage(error)}`);
    }
  }, [actionTarget, apiClient, loadRootDirectory, projectId, selectedFile]);

  const handleReset = React.useCallback(async () => {
    if (!apiClient || !projectId || !actionTarget || actionTarget.item.type !== FILE_TYPE.File) {
      return;
    }

    const targetPath = actionTarget.item.path;
    setActionTarget(null);
    setStatusMessage(null);

    try {
      const result = await resetFile(apiClient, projectId, targetPath);

      if (result.success) {
        setStatusMessage(result.message);
        await loadRootDirectory();
        if (selectedFile === targetPath) {
          await loadFileContent(targetPath);
        }
      } else {
        setStatusMessage(result.message || "No changes to reset");
      }
    } catch (error) {
      setStatusMessage(`Failed to reset: ${getErrorMessage(error)}`);
    }
  }, [actionTarget, apiClient, loadFileContent, loadRootDirectory, projectId, selectedFile]);

  if (!projectId) {
    return (
      <View style={styles.emptyPanel}>
        <Text style={styles.emptyPanelText}>No project selected</Text>
      </View>
    );
  }

  if (!apiClient) {
    return (
      <View style={styles.emptyPanel}>
        <Text style={styles.emptyPanelText}>Connect to an Agw backend first</Text>
      </View>
    );
  }

  const effectiveExplorerError = dependenciesError ?? explorerError;
  const isLoading = isDependenciesLoading || isExplorerLoading;

  return (
    <View style={styles.filesWorkspace}>
      <View
        style={[
          styles.explorerPanel,
          isExplorerCollapsed && styles.accordionCollapsedPanel,
        ]}
      >
        <AccordionHeader
          detail="/"
          expanded={!isExplorerCollapsed}
          onPress={() => {
            setIsExplorerCollapsed((current) => {
              const next = !current;
              if (next) {
                setIsPreviewCollapsed(false);
              }
              return next;
            });
          }}
          testID="agw-files-explorer-accordion"
          title="File Explorer"
        />
        {!isExplorerCollapsed ? (
          <>
          <View style={styles.explorerActions}>
            <View style={styles.diffSwitchRow}>
              <Switch
                onValueChange={handleToggleDiff}
                testID="agw-files-diff-switch"
                value={onlyDiff}
              />
              <Text style={styles.diffSwitchLabel}>Diff</Text>
            </View>
            <Pressable
              accessibilityLabel="Refresh files"
              accessibilityRole="button"
              disabled={isLoading}
              onPress={() => {
                void loadRootDirectory();
              }}
              style={({ pressed }) => [
                styles.refreshButton,
                pressed && styles.pressed,
                isLoading && styles.refreshButtonDisabled,
              ]}
              testID="agw-files-refresh"
            >
              {isLoading ? (
                <ActivityIndicator color={colors.muted} size="small" />
              ) : (
                <Text style={styles.refreshButtonText}>Refresh</Text>
              )}
            </Pressable>
          </View>

          {effectiveExplorerError ? (
            <View style={styles.filesStateBlock}>
              <Text style={styles.errorText}>{effectiveExplorerError}</Text>
            </View>
          ) : rootItems.length === 0 && !isLoading ? (
            <View style={styles.filesStateBlock}>
              <Text style={styles.emptyPanelText}>No files found</Text>
            </View>
          ) : (
            <ScrollView
              alwaysBounceVertical={false}
              contentContainerStyle={styles.filesTreeContent}
              style={styles.filesTreeScroll}
            >
              {rootItems.map((item, index) => (
                <FileTreeNode
                  childItems={childItems}
                  expandedPaths={expandedPaths}
                  item={item}
                  key={item.path}
                  level={0}
                  onLongPress={(nextItem) => setActionTarget({ item: nextItem })}
                  onPress={handleNodePress}
                  selectedFile={selectedFile}
                  showTopBorder={index > 0}
                />
              ))}
            </ScrollView>
          )}
          </>
        ) : null}
      </View>

      {statusMessage ? (
        <View style={styles.fileStatusBanner}>
          <Text style={styles.fileStatusText}>{statusMessage}</Text>
        </View>
      ) : null}

      <FileContent
        comments={comments}
        contentError={contentError}
        diffContentData={diffContentData}
        fileContent={fileContent}
        isCollapsed={isPreviewCollapsed}
        isLoadingContent={isContentLoading}
        onToggleCollapsed={() => {
          setIsPreviewCollapsed((current) => {
            const next = !current;
            if (next) {
              setIsExplorerCollapsed(false);
            }
            return next;
          });
        }}
        onlyDiff={onlyDiff}
        selectedFile={selectedFile}
        setComments={setComments}
      />

      {actionTarget ? (
        <View style={styles.fileActionSheet}>
          <Text numberOfLines={1} style={styles.fileActionTitle}>
            {actionTarget.item.name}
          </Text>
          <View style={styles.fileActionRow}>
            <Pressable
              accessibilityRole="button"
              onPress={handleDelete}
              style={styles.fileActionButtonDanger}
              testID="agw-file-action-delete"
            >
              <Text style={styles.fileActionButtonDangerText}>Delete</Text>
            </Pressable>
            {actionTarget.item.type === FILE_TYPE.File ? (
              <Pressable
                accessibilityRole="button"
                onPress={handleReset}
                style={styles.fileActionButton}
                testID="agw-file-action-reset"
              >
                <Text style={styles.fileActionButtonText}>Reset to HEAD</Text>
              </Pressable>
            ) : null}
            <Pressable
              accessibilityRole="button"
              onPress={() => setActionTarget(null)}
              style={styles.fileActionButton}
              testID="agw-file-action-cancel"
            >
              <Text style={styles.fileActionButtonText}>Cancel</Text>
            </Pressable>
          </View>
        </View>
      ) : null}
    </View>
  );
}

function AccordionHeader({
  detail,
  expanded,
  onPress,
  testID,
  title,
}: {
  detail?: string | null;
  expanded: boolean;
  onPress: () => void;
  testID: string;
  title: string;
}): React.JSX.Element {
  return (
    <Pressable
      accessibilityLabel={title}
      onPress={onPress}
      style={({ pressed }) => [
        styles.accordionHeader,
        pressed && styles.pressed,
      ]}
      testID={testID}
    >
      <Icon
        color={colors.muted}
        name={expanded ? "chevronDown" : "chevronRight"}
        size={16}
      />
      <View style={styles.accordionTitleColumn}>
        <Text style={styles.explorerTitle}>{title}</Text>
        {detail ? (
          <Text numberOfLines={1} style={styles.explorerPath}>
            {detail}
          </Text>
        ) : null}
      </View>
    </Pressable>
  );
}

function FileTreeNode({
  childItems,
  expandedPaths,
  item,
  level,
  onLongPress,
  onPress,
  selectedFile,
  showTopBorder,
}: {
  childItems: Record<string, FileItem[]>;
  expandedPaths: Set<string>;
  item: FileItem;
  level: number;
  onLongPress: (item: FileItem) => void;
  onPress: (item: FileItem) => void;
  selectedFile: string | null;
  showTopBorder?: boolean;
}): React.JSX.Element {
  const isDirectory = item.type === FILE_TYPE.Directory;
  const isExpanded = isDirectory && expandedPaths.has(item.path);
  const children = item.children ?? childItems[item.path] ?? [];
  const gitStatus = item.gitStatus ?? undefined;
  const isSelected = selectedFile === item.path;

  return (
    <View>
      <Pressable
        accessibilityRole="button"
        onLongPress={() => onLongPress(item)}
        onPress={() => onPress(item)}
        style={({ pressed }) => [
          isDirectory ? styles.folderRow : styles.fileRow,
          showTopBorder && styles.fileRowTopBorder,
          isSelected && styles.fileRowSelected,
          pressed && styles.pressed,
          { paddingLeft: 16 + level * 20 },
        ]}
        testID={`agw-file-node-${item.path}`}
      >
        {isDirectory ? (
          <>
            <Icon
              color={colors.muted}
              name={isExpanded ? "chevronDown" : "chevronRight"}
              size={16}
            />
            <Icon color={colors.folder} name="folder" size={20} />
            <Text
              numberOfLines={1}
              style={[styles.folderTitle, level === 0 && styles.folderTitleBold]}
            >
              {item.name}
            </Text>
          </>
        ) : (
          <>
            <View style={styles.fileLeafSpacer} />
            <Icon name={getFileIcon(item.name)} size={20} />
            <View style={styles.fileTextColumn}>
              <Text
                numberOfLines={1}
                style={[
                  styles.fileTitle,
                  gitStatus === "deleted" && styles.deletedFileTitle,
                ]}
              >
                {item.name}
              </Text>
              <Text numberOfLines={1} style={styles.fileMeta}>
                {formatFileMeta(item)}
              </Text>
            </View>
          </>
        )}
        {gitStatus ? <GitStatusBadge status={gitStatus} /> : null}
      </Pressable>
      {isExpanded
        ? children.map((child, index) => (
            <FileTreeNode
              childItems={childItems}
              expandedPaths={expandedPaths}
              item={child}
              key={child.path}
              level={level + 1}
              onLongPress={onLongPress}
              onPress={onPress}
              selectedFile={selectedFile}
              showTopBorder={index > 0}
            />
          ))
        : null}
    </View>
  );
}

function GitStatusBadge({ status }: { status: string }): React.JSX.Element {
  return (
    <View
      style={[
        styles.gitStatusBadge,
        status === "added" && styles.gitStatusAdded,
        status === "modified" && styles.gitStatusModified,
        status === "deleted" && styles.gitStatusDeleted,
        status === "untracked" && styles.gitStatusUntracked,
      ]}
    >
      <Text style={styles.gitStatusText}>{GIT_STATUS_LABEL[status] ?? status}</Text>
    </View>
  );
}

function FileContent({
  comments,
  contentError,
  diffContentData,
  fileContent,
  isCollapsed,
  isLoadingContent,
  onToggleCollapsed,
  onlyDiff,
  selectedFile,
  setComments,
}: {
  comments: LineComment[];
  contentError: string | null;
  diffContentData: AgwGitDiffResponse | null;
  fileContent: string;
  isCollapsed: boolean;
  isLoadingContent: boolean;
  onToggleCollapsed: () => void;
  onlyDiff: boolean;
  selectedFile: string | null;
  setComments: React.Dispatch<React.SetStateAction<LineComment[]>>;
}): React.JSX.Element {
  if (!selectedFile) {
    return (
      <View
        style={[
          styles.fileContentPanel,
          isCollapsed && styles.accordionCollapsedPanel,
        ]}
      >
        <AccordionHeader
          expanded={!isCollapsed}
          onPress={onToggleCollapsed}
          testID="agw-files-preview-accordion"
          title="File Preview"
        />
        {!isCollapsed ? (
          <View style={styles.fileContentEmpty}>
            <Icon color={colors.border} name="fileSheet" size={36} />
            <Text style={styles.emptyPanelText}>
              Select a file to view its contents
            </Text>
          </View>
        ) : null}
      </View>
    );
  }

  return (
    <View
      style={[
        styles.fileContentPanel,
        isCollapsed && styles.accordionCollapsedPanel,
      ]}
    >
      <AccordionHeader
        detail={selectedFile}
        expanded={!isCollapsed}
        onPress={onToggleCollapsed}
        testID="agw-files-preview-accordion"
        title={getPathName(selectedFile)}
      />
      {!isCollapsed ? (
        isLoadingContent ? (
        <View style={styles.fileContentState}>
          <ActivityIndicator color={colors.muted} size="small" />
        </View>
        ) : contentError ? (
        <View style={styles.fileContentState}>
          <Text style={styles.errorText}>Error loading file: {contentError}</Text>
        </View>
        ) : onlyDiff && diffContentData ? (
        diffContentData.unchanged ? (
          <UnchangedFile
            comments={comments}
            diffContentData={diffContentData}
            selectedFile={selectedFile}
            setComments={setComments}
          />
        ) : (
          <DiffViewer
            comments={comments}
            diff={diffContentData.diff}
            filePath={selectedFile}
            setComments={setComments}
          />
        )
        ) : (
        <CodeViewer
          commentSide="current"
          comments={comments}
          content={fileContent}
          filePath={selectedFile}
          isDiffView={false}
          setComments={setComments}
        />
        )
      ) : null}
    </View>
  );
}

function DiffViewer({
  comments,
  diff,
  filePath,
  setComments,
}: {
  comments: LineComment[];
  diff: string;
  filePath: string;
  setComments: React.Dispatch<React.SetStateAction<LineComment[]>>;
}): React.JSX.Element {
  const { modified, original } = React.useMemo(() => parseDiffToFiles(diff), [diff]);

  if (!diff.trim()) {
    return (
      <View style={styles.fileContentState}>
        <Text style={styles.emptyPanelText}>No changes detected</Text>
      </View>
    );
  }

  return (
    <ScrollView style={styles.fileViewerScroll}>
      <View style={styles.diffSection}>
        <Text style={[styles.diffSectionTitle, styles.diffOriginalTitle]}>
          Original
        </Text>
        <CodeViewer
          commentSide="original"
          comments={comments}
          content={original}
          filePath={filePath}
          isDiffView
          setComments={setComments}
        />
      </View>
      <View style={styles.diffSection}>
        <Text style={[styles.diffSectionTitle, styles.diffModifiedTitle]}>
          Modified
        </Text>
        <CodeViewer
          commentSide="modified"
          comments={comments}
          content={modified}
          filePath={filePath}
          isDiffView
          setComments={setComments}
        />
      </View>
    </ScrollView>
  );
}

function UnchangedFile({
  comments,
  diffContentData,
  selectedFile,
  setComments,
}: {
  comments: LineComment[];
  diffContentData: AgwGitDiffResponse;
  selectedFile: string;
  setComments: React.Dispatch<React.SetStateAction<LineComment[]>>;
}): React.JSX.Element {
  return (
    <ScrollView style={styles.fileViewerScroll}>
      <View style={styles.unchangedBanner}>
        <Text style={styles.unchangedBannerText}>
          {diffContentData.message || "No changes detected"}
        </Text>
      </View>
      {diffContentData.originalContent ? (
        <CodeViewer
          commentSide="original"
          comments={comments}
          content={diffContentData.originalContent}
          filePath={selectedFile}
          isDiffView
          setComments={setComments}
        />
      ) : null}
    </ScrollView>
  );
}

function CodeViewer({
  comments,
  commentSide,
  content,
  filePath,
  isDiffView,
  setComments,
}: {
  comments: LineComment[];
  commentSide: CommentSide;
  content: string;
  filePath: string;
  isDiffView: boolean;
  setComments: React.Dispatch<React.SetStateAction<LineComment[]>>;
}): React.JSX.Element {
  const [activeLine, setActiveLine] = React.useState<number | null>(null);
  const [draft, setDraft] = React.useState("");
  const lines = React.useMemo(() => content.split("\n"), [content]);

  const getLineComments = React.useCallback(
    (lineNumber: number) =>
      comments.filter(
        (comment) =>
          comment.filePath === filePath &&
          comment.side === commentSide &&
          comment.lineNumber === lineNumber
      ),
    [commentSide, comments, filePath]
  );

  const handleSave = React.useCallback(
    (lineNumber: number) => {
      const nextContent = draft.trim();

      if (!nextContent) {
        setActiveLine(null);
        setDraft("");
        return;
      }

      setComments((current) => [
        ...current,
        {
          content: nextContent,
          filePath,
          id: `${Date.now()}-${Math.random().toString(36).slice(2)}`,
          lineNumber,
          side: commentSide,
          timestamp: new Date(),
        },
      ]);
      setActiveLine(null);
      setDraft("");
    },
    [commentSide, draft, filePath, setComments]
  );

  const handleDeleteComment = React.useCallback(
    (commentId: string) => {
      setComments((current) => current.filter((comment) => comment.id !== commentId));
    },
    [setComments]
  );

  const handleUpdateComment = React.useCallback(
    (commentId: string, nextContent: string) => {
      const trimmedContent = nextContent.trim();

      setComments((current) =>
        trimmedContent
          ? current.map((comment) =>
              comment.id === commentId
                ? { ...comment, content: trimmedContent }
                : comment
            )
          : current.filter((comment) => comment.id !== commentId)
      );
    },
    [setComments]
  );

  return (
    <View style={styles.codeViewer}>
      {lines.map((line, index) => {
        const lineNumber = index + 1;
        const lineComments = getLineComments(lineNumber);
        const isActive = activeLine === lineNumber;

        return (
          <View key={`${commentSide}-${lineNumber}`}>
            <Pressable
              onPress={() => {
                setActiveLine(isActive ? null : lineNumber);
                setDraft("");
              }}
              style={[
                styles.codeLine,
                (isActive || lineComments.length > 0) && styles.codeLineActive,
              ]}
              testID={`agw-file-line-${commentSide}-${lineNumber}`}
            >
              <Text style={styles.codeLineNumber}>{lineNumber}</Text>
              <ScrollView horizontal showsHorizontalScrollIndicator={false}>
                <Text style={styles.codeLineText}>{line || " "}</Text>
              </ScrollView>
            </Pressable>
            {lineComments.length > 0 || isActive ? (
              <View style={styles.commentSection}>
                {lineComments.map((comment) => (
                  <EditableComment
                    comment={comment}
                    key={comment.id}
                    onDelete={() => handleDeleteComment(comment.id)}
                    onSave={(contentValue) =>
                      handleUpdateComment(comment.id, contentValue)
                    }
                  />
                ))}
                {isActive ? (
                  <View style={styles.commentInputGroup}>
                    <TextInput
                      multiline
                      onChangeText={setDraft}
                      placeholder={
                        isDiffView
                          ? `Write a comment for ${COMMENT_SIDE_LABEL[commentSide]}...`
                          : "Write a comment..."
                      }
                      style={styles.commentInput}
                      testID={`agw-comment-input-${commentSide}-${lineNumber}`}
                      value={draft}
                    />
                    <View style={styles.commentActionRow}>
                      <Pressable
                        accessibilityRole="button"
                        onPress={() => handleSave(lineNumber)}
                        style={styles.commentSaveButton}
                        testID={`agw-comment-save-${commentSide}-${lineNumber}`}
                      >
                        <Text style={styles.commentSaveButtonText}>Save</Text>
                      </Pressable>
                      <Pressable
                        accessibilityRole="button"
                        onPress={() => {
                          setActiveLine(null);
                          setDraft("");
                        }}
                        style={styles.commentCancelButton}
                      >
                        <Text style={styles.commentCancelButtonText}>Cancel</Text>
                      </Pressable>
                    </View>
                  </View>
                ) : null}
              </View>
            ) : null}
          </View>
        );
      })}
    </View>
  );
}

function EditableComment({
  comment,
  onDelete,
  onSave,
}: {
  comment: LineComment;
  onDelete: () => void;
  onSave: (content: string) => void;
}): React.JSX.Element {
  const [isEditing, setIsEditing] = React.useState(false);
  const [draft, setDraft] = React.useState(comment.content);

  if (isEditing) {
    return (
      <View style={styles.commentInputGroup}>
        <TextInput
          multiline
          onChangeText={setDraft}
          style={styles.commentInput}
          value={draft}
        />
        <View style={styles.commentActionRow}>
          <Pressable
            accessibilityRole="button"
            onPress={() => {
              onSave(draft);
              setIsEditing(false);
            }}
            style={styles.commentSaveButton}
          >
            <Text style={styles.commentSaveButtonText}>Save</Text>
          </Pressable>
          <Pressable
            accessibilityRole="button"
            onPress={() => {
              setDraft(comment.content);
              setIsEditing(false);
            }}
            style={styles.commentCancelButton}
          >
            <Text style={styles.commentCancelButtonText}>Cancel</Text>
          </Pressable>
        </View>
      </View>
    );
  }

  return (
    <View style={styles.commentItem}>
      <Pressable
        onPress={() => setIsEditing(true)}
        style={styles.commentTextColumn}
      >
        <Text style={styles.commentTimestamp}>
          {comment.timestamp.toLocaleTimeString()}
        </Text>
        <Text style={styles.commentText}>{comment.content}</Text>
      </Pressable>
      <Pressable
        accessibilityRole="button"
        onPress={onDelete}
        style={styles.commentDeleteButton}
      >
        <Text style={styles.commentDeleteButtonText}>Delete</Text>
      </Pressable>
    </View>
  );
}

function buildFileTree(items: AgwFileItem[], rootPath: string): FileItem[] {
  const normalizedRoot = normalizePath(rootPath).replace(/\/$/, "");
  const dirMap = new Map<string, FileItem & { children: FileItem[] }>();
  const sortedItems = [...items].sort((a, b) => {
    const aDepth = normalizePath(a.path).split("/").length;
    const bDepth = normalizePath(b.path).split("/").length;
    return aDepth - bDepth;
  });
  const fileStatusesMap = new Map<string, Set<string>>();

  sortedItems.forEach((item) => {
    let currentPath = normalizePath(item.path);

    while (currentPath !== normalizedRoot && currentPath !== "") {
      if (!fileStatusesMap.has(currentPath)) {
        fileStatusesMap.set(currentPath, new Set());
      }

      if (item.gitStatus) {
        fileStatusesMap.get(currentPath)?.add(item.gitStatus);
      }

      currentPath = currentPath.substring(0, currentPath.lastIndexOf("/"));
      if (currentPath === "") {
        break;
      }
    }
  });

  const getDirGitStatus = (path: string): string | undefined => {
    const statuses = fileStatusesMap.get(path);

    if (!statuses || statuses.size === 0) {
      return undefined;
    }

    if (statuses.has("modified")) {
      return "modified";
    }

    if (statuses.has("added")) {
      return "added";
    }

    if (statuses.has("untracked")) {
      return "untracked";
    }

    if (statuses.has("deleted")) {
      return "deleted";
    }

    return undefined;
  };

  sortedItems.forEach((item) => {
    const normalizedPath = normalizePath(item.path);
    const parentPath = normalizedPath.substring(0, normalizedPath.lastIndexOf("/"));
    let currentPath = parentPath;
    const pathsToCreate: string[] = [];

    while (currentPath !== normalizedRoot && currentPath !== "") {
      if (!dirMap.has(currentPath)) {
        pathsToCreate.unshift(currentPath);
      }

      currentPath = currentPath.substring(0, currentPath.lastIndexOf("/"));
      if (currentPath === "") {
        break;
      }
    }

    pathsToCreate.forEach((path) => {
      dirMap.set(path, {
        children: [],
        gitStatus: getDirGitStatus(path),
        name: getPathName(path),
        path,
        type: FILE_TYPE.Directory,
      });
    });

    if (item.type === FILE_TYPE.Directory && !dirMap.has(normalizedPath)) {
      dirMap.set(normalizedPath, {
        ...item,
        children: [],
        path: normalizedPath,
      });
    }
  });

  const buildTree = (dirPath: string | null): FileItem[] => {
    const result: FileItem[] = [];
    const levelMap = new Map<string, FileItem>();

    dirMap.forEach((dir, path) => {
      const parentPath =
        path.substring(0, path.lastIndexOf("/")) || normalizedRoot;
      const normalizedParent = normalizePath(parentPath);

      if (
        (dirPath === null && normalizedParent === normalizedRoot) ||
        (dirPath !== null && normalizedParent === dirPath)
      ) {
        levelMap.set(path, { ...dir });
      }
    });

    sortedItems.forEach((item) => {
      const normalizedPath = normalizePath(item.path);
      const parentPath =
        normalizedPath.substring(0, normalizedPath.lastIndexOf("/")) ||
        normalizedRoot;
      const normalizedParent = normalizePath(parentPath);

      if (
        item.type === FILE_TYPE.File &&
        ((dirPath === null && normalizedParent === normalizedRoot) ||
          (dirPath !== null && normalizedParent === dirPath))
      ) {
        levelMap.set(`file-${normalizedPath}`, { ...item });
      }
    });

    levelMap.forEach((item) => {
      if (item.type === FILE_TYPE.Directory) {
        item.children = buildTree(item.path);
      }

      result.push(item);
    });

    return result.sort((a, b) => {
      if (a.type === b.type) {
        return a.name.localeCompare(b.name, undefined, {
          numeric: true,
          sensitivity: "base",
        });
      }

      return a.type === FILE_TYPE.Directory ? -1 : 1;
    });
  };

  return buildTree(null);
}

function collectDirectoryPaths(items: FileItem[]): Set<string> {
  const paths = new Set<string>();

  items.forEach((item) => {
    if (item.type !== FILE_TYPE.Directory) {
      return;
    }

    paths.add(item.path);
    collectDirectoryPaths(item.children ?? []).forEach((path) => paths.add(path));
  });

  return paths;
}

function parseDiffToFiles(diffText: string): { modified: string; original: string } {
  const lines = diffText.split("\n");
  const originalLines: string[] = [];
  const modifiedLines: string[] = [];

  lines.forEach((line) => {
    if (
      line.startsWith("diff --git") ||
      line.startsWith("index ") ||
      line.startsWith("--- ") ||
      line.startsWith("+++ ") ||
      line.startsWith("@@")
    ) {
      return;
    }

    if (line.startsWith("-")) {
      originalLines.push(line.substring(1));
      return;
    }

    if (line.startsWith("+")) {
      modifiedLines.push(line.substring(1));
      return;
    }

    if (line.startsWith(" ")) {
      const content = line.substring(1);
      originalLines.push(content);
      modifiedLines.push(content);
    }
  });

  return {
    modified: modifiedLines.join("\n"),
    original: originalLines.join("\n"),
  };
}

function getFileIcon(fileName: string): FileIconName {
  const extension = fileName.split(".").pop()?.toLowerCase();

  if (extension === "png" || extension === "jpg" || extension === "jpeg") {
    return "fileImage";
  }

  if (extension === "pdf") {
    return "filePdf";
  }

  return "fileSheet";
}

function formatFileMeta(file: AgwFileItem): string {
  const parts: string[] = [];

  if (typeof file.size === "number") {
    parts.push(formatFileSize(file.size));
  }

  if (file.modifiedTime) {
    parts.push(formatDate(file.modifiedTime));
  }

  return parts.join(" - ");
}

function formatFileSize(size: number): string {
  if (size < 1024) {
    return `${size} B`;
  }

  if (size < 1024 * 1024) {
    return `${(size / 1024).toFixed(1)} KB`;
  }

  return `${(size / 1024 / 1024).toFixed(1)} MB`;
}

function formatDate(value: string): string {
  const date = new Date(value);

  if (Number.isNaN(date.getTime())) {
    return value;
  }

  return date.toLocaleDateString(undefined, {
    day: "numeric",
    month: "short",
  });
}

function getPathName(path: string): string {
  const normalizedPath = normalizePath(path);
  return normalizedPath.substring(normalizedPath.lastIndexOf("/") + 1);
}

function normalizePath(path: string): string {
  return path.replace(/\\/g, "/");
}

function getErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : "Unknown error.";
}
