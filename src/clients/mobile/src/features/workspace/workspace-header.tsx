import { router } from "expo-router";
import { Menu, MessageSquarePlus, MoreHorizontal } from "lucide-react-native";
import React from "react";
import { Alert, Pressable, StyleSheet, Text, View } from "react-native";

import { IconButton } from "@/components/icon-button";
import { useComposer } from "@/features/chat/composer-provider";
import { useWorkspace } from "./workspace-provider";
import { colors, radius, typography } from "@/theme/tokens";

export function WorkspaceHeader({
  active,
  safeTop,
  onScrollToTop,
}: {
  active: "chat" | "files";
  safeTop: number;
  onScrollToTop?: () => void;
}): React.JSX.Element {
  const workspace = useWorkspace();
  const composer = useComposer();

  const newChat = () => {
    try {
      workspace.newChat();
      router.replace("/chat");
    } catch (error) {
      Alert.alert(
        "Execution in progress",
        error instanceof Error ? error.message : "Stop the current execution first.",
      );
    }
  };
  const showMore = () => {
    Alert.alert("Conversation actions", undefined, [
      { text: "Quick Text", onPress: composer.openQuickText },
      ...(workspace.selectedContextId && !workspace.isExecuting
        ? [
            {
              text: "Clear Conversation",
              style: "destructive" as const,
              onPress: () =>
                Alert.alert(
                  "Clear conversation?",
                  "Messages and execution records will be removed.",
                  [
                    { text: "Cancel", style: "cancel" },
                    {
                      text: "Clear",
                      style: "destructive",
                      onPress: () => void workspace.clearCurrentContext(),
                    },
                  ],
                ),
            },
          ]
        : []),
      ...(onScrollToTop ? [{ text: "Scroll to Top", onPress: onScrollToTop }] : []),
      { text: "Cancel", style: "cancel" },
    ]);
  };

  return (
    <View style={[styles.header, { paddingTop: safeTop, height: 64 + safeTop }]}>
      <IconButton icon={Menu} label="Open chat history" onPress={() => router.push("/history")} />
      <View accessibilityRole="tablist" style={styles.tabs}>
        <Tab active={active === "chat"} label="Chat" onPress={() => router.replace("/chat")} />
        <Tab active={active === "files"} label="Files" onPress={() => router.replace("/files")} />
      </View>
      <View style={styles.actions}>
        <IconButton icon={MessageSquarePlus} label="New chat" size={20} onPress={newChat} />
        <IconButton
          icon={MoreHorizontal}
          label="More conversation actions"
          size={21}
          onPress={showMore}
        />
      </View>
    </View>
  );
}

function Tab({
  active,
  label,
  onPress,
}: {
  active: boolean;
  label: string;
  onPress(): void;
}): React.JSX.Element {
  return (
    <Pressable
      accessibilityRole="tab"
      accessibilityState={{ selected: active }}
      onPress={onPress}
      style={[styles.tab, active && styles.tabActive]}
    >
      <Text style={[styles.tabText, active && styles.tabTextActive]}>{label}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  header: {
    paddingHorizontal: 8,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderColor: colors.border,
    backgroundColor: colors.background,
  },
  tabs: {
    flex: 1,
    maxWidth: 164,
    height: 38,
    marginHorizontal: 6,
    padding: 3,
    borderRadius: radius.pill,
    backgroundColor: colors.segment,
    flexDirection: "row",
  },
  tab: { flex: 1, alignItems: "center", justifyContent: "center", borderRadius: radius.pill },
  tabActive: { backgroundColor: colors.white },
  tabText: { color: colors.muted, fontFamily: typography.medium, fontSize: 13 },
  tabTextActive: { color: colors.ink, fontFamily: typography.semibold },
  actions: { flexDirection: "row" },
});
