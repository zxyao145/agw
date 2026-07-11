import React from "react";
import { Pressable, Text, View } from "react-native";
import { IconButton } from "./icons";
import { styles } from "./styles";
import type { AgwTabName } from "./types";

export function TopBar({
  activeTab,
  onOpenDrawer,
  onTabChange,
  safeTop,
}: {
  activeTab: AgwTabName;
  onOpenDrawer: () => void;
  onTabChange: (tab: AgwTabName) => void;
  safeTop: number;
}): React.JSX.Element {
  return (
    <View
      style={[styles.topBar, { height: 64 + safeTop, paddingTop: safeTop }]}
    >
      <IconButton
        icon="menu"
        label="Open chat history"
        onPress={onOpenDrawer}
        size={34}
        testID="agw-open-drawer"
      />
      <View accessibilityRole="tablist" style={styles.segmentedTabs}>
        <SegmentTab
          active={activeTab === "chat"}
          label="Chat"
          onPress={() => onTabChange("chat")}
          testID="agw-tab-chat"
        />
        <SegmentTab
          active={activeTab === "files"}
          label="Files"
          onPress={() => onTabChange("files")}
          testID="agw-tab-files"
        />
      </View>
      <View style={styles.headerActions}>
        <IconButton icon="plus" label="New item" size={40} />
        <IconButton icon="more" label="More actions" size={32} />
      </View>
    </View>
  );
}

function SegmentTab({
  active,
  label,
  onPress,
  testID,
}: {
  active: boolean;
  label: string;
  onPress: () => void;
  testID: string;
}): React.JSX.Element {
  return (
    <Pressable
      accessibilityRole="tab"
      accessibilityState={{ selected: active }}
      onPress={onPress}
      style={({ pressed }) => [
        styles.segmentTab,
        active && styles.segmentTabActive,
        pressed && styles.pressed,
      ]}
      testID={testID}
    >
      <Text style={[styles.segmentText, active && styles.segmentTextActive]}>
        {label}
      </Text>
    </Pressable>
  );
}
