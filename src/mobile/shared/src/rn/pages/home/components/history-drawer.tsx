import React from "react";
import { Pressable, Text, View } from "react-native";
import { Icon, IconButton } from "./icons";
import { styles } from "./styles";
import { colors } from "./tokens";

export function HistoryDrawer({
  onClose,
  safeBottom,
  safeTop,
}: {
  onClose: () => void;
  safeBottom: number;
  safeTop: number;
}): React.JSX.Element {
  return (
    <View style={styles.drawerLayer}>
      <View style={styles.drawerPanel}>
        <View
          style={[
            styles.drawerHeader,
            { height: 64 + safeTop, paddingTop: safeTop },
          ]}
        >
          <Text style={styles.drawerBrand}>Agw</Text>
          <IconButton
            color={colors.primary}
            icon="close"
            label="Close chat history"
            onPress={onClose}
            size={40}
            testID="agw-close-drawer"
          />
        </View>

        <View style={styles.drawerSelectors}>
          <DrawerSelect label="PROJECT" value="Project Alpha" />
          <DrawerSelect label="AGENT" value="Design" />
        </View>

        <View style={styles.historySection}>
          <Text style={styles.sectionLabel}>RECENT HISTORY</Text>
          <HistoryItem
            preview="I have analyzed the current design..."
            title="UI Refresh Strategy"
          />
          <HistoryItem
            preview="The SQL indexing pattern you requested..."
            title="Database Optimization"
          />
          <HistoryItem
            preview="Here are three variations of the headline..."
            title="Marketing Copy"
          />
          <HistoryItem
            preview="Setting initial parameters for workspace..."
            title="Project Kickoff"
          />
        </View>

        <View
          style={[
            styles.drawerFooter,
            { paddingBottom: Math.max(8, safeBottom) },
          ]}
        >
          <View style={styles.settingsRow}>
            <Icon color={colors.muted} name="settings" size={22} />
            <Text style={styles.settingsText}>Settings</Text>
          </View>
        </View>
      </View>
    </View>
  );
}

function DrawerSelect({
  label,
  value,
}: {
  label: string;
  value: string;
}): React.JSX.Element {
  return (
    <View style={styles.drawerSelectColumn}>
      <Text style={styles.selectLabel}>{label}</Text>
      <View style={styles.selectBox}>
        <Text numberOfLines={1} style={styles.selectValue}>
          {value}
        </Text>
        <Icon color={colors.muted} name="chevronDown" size={14} />
      </View>
    </View>
  );
}

function HistoryItem({
  preview,
  title,
}: {
  preview: string;
  title: string;
}): React.JSX.Element {
  return (
    <Pressable
      accessibilityRole="button"
      style={({ pressed }) => [
        styles.historyItem,
        pressed && styles.historyItemPressed,
      ]}
    >
      <Text numberOfLines={1} style={styles.historyTitle}>
        {title}
      </Text>
      <Text numberOfLines={1} style={styles.historyPreview}>
        {preview}
      </Text>
    </Pressable>
  );
}
