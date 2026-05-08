import React from "react";
import { ScrollView, Text, View } from "react-native";
import { Icon } from "./icons";
import { styles } from "./styles";
import { colors } from "./tokens";
import type { IconName } from "./icons";

type FileIconName = Extract<IconName, "fileImage" | "filePdf" | "fileSheet">;

export function FilesPanel(): React.JSX.Element {
  return (
    <ScrollView
      alwaysBounceVertical={false}
      contentContainerStyle={styles.filesContent}
      style={styles.panelScroll}
    >
      <FolderRow expanded info title="Project Alpha" />
      <FolderRow depth={1} expanded title="Designs" />
      <FileRow
        icon="fileImage"
        indent={146}
        meta="5.8 MB - 3 days ago"
        title="Brand_Assets_Hero.png"
      />
      <FolderRow depth={1} title="Documents" />
      <FileRow
        bordered
        icon="filePdf"
        indent={76}
        meta="2.4 MB - 2h ago"
        title="Q4_Marketing_Strategy.pdf"
      />
      <FileRow
        bordered
        icon="fileSheet"
        indent={76}
        meta="1.1 MB - Yesterday"
        title="Project_Budget_2024.xlsx"
      />
      <FolderRow bordered title="Shared with Me" />
      <FolderRow bordered title="Archive" />
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
