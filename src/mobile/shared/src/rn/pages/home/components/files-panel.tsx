import React from "react";
import { ScrollView, Text, View } from "react-native";
import type { AgwFileItem } from "../../../api/agw-api-types";
import { Icon } from "./icons";
import { styles } from "./styles";
import { colors } from "./tokens";
import type { IconName } from "./icons";

type FileIconName = Extract<IconName, "fileImage" | "filePdf" | "fileSheet">;

export function FilesPanel({
  error,
  files,
  isLoading,
  workspace,
}: {
  error?: string | null;
  files: AgwFileItem[];
  isLoading?: boolean;
  workspace?: string | null;
}): React.JSX.Element {
  if (!workspace) {
    return (
      <View style={styles.emptyPanel}>
        <Text style={styles.emptyPanelText}>No workspace configured</Text>
      </View>
    );
  }

  if (isLoading) {
    return (
      <View style={styles.emptyPanel}>
        <Text style={styles.emptyPanelText}>Loading files</Text>
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

  if (files.length === 0) {
    return (
      <View style={styles.emptyPanel}>
        <Text style={styles.emptyPanelText}>No files found</Text>
      </View>
    );
  }

  return (
    <ScrollView
      alwaysBounceVertical={false}
      contentContainerStyle={styles.filesContent}
      style={styles.panelScroll}
    >
      {files.map((file, index) =>
        file.type === "directory" ? (
          <FolderRow
            bordered={index > 0}
            key={file.path}
            title={file.name}
          />
        ) : (
          <FileRow
            bordered={index > 0}
            icon={getFileIcon(file.name)}
            indent={48}
            key={file.path}
            meta={formatFileMeta(file)}
            title={file.name}
          />
        )
      )}
    </ScrollView>
  );
}

function FolderRow({
  bordered,
  depth = 0,
  expanded = false,
  info = false,
  title,
}: {
  bordered?: boolean;
  depth?: number;
  expanded?: boolean;
  info?: boolean;
  title: string;
}): React.JSX.Element {
  return (
    <View
      style={[
        styles.folderRow,
        bordered && styles.fileRowTopBorder,
        { paddingLeft: 16 + depth * 32 },
      ]}
    >
      <Icon
        color={colors.muted}
        name={expanded ? "chevronDown" : "chevronRight"}
        size={16}
      />
      <Icon color={colors.folder} name="folder" size={20} />
      <Text style={[styles.folderTitle, depth === 0 && styles.folderTitleBold]}>
        {title}
      </Text>
      {info ? (
        <View style={styles.infoIconSlot}>
          <Icon color={colors.border} name="info" size={18} />
        </View>
      ) : null}
    </View>
  );
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
    return `${Math.round(size / 1024)} KB`;
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

function FileRow({
  bordered,
  icon,
  indent,
  meta,
  title,
}: {
  bordered?: boolean;
  icon: FileIconName;
  indent: number;
  meta: string;
  title: string;
}): React.JSX.Element {
  return (
    <View
      style={[
        styles.fileRow,
        bordered && styles.nestedFileRowTopBorder,
        { paddingLeft: indent },
      ]}
    >
      <Icon name={icon} size={20} />
      <View style={styles.fileTextColumn}>
        <Text numberOfLines={1} style={styles.fileTitle}>
          {title}
        </Text>
        <Text numberOfLines={1} style={styles.fileMeta}>
          {meta}
        </Text>
      </View>
    </View>
  );
}
