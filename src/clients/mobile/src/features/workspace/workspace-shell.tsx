import React from "react";
import { KeyboardAvoidingView, Platform, StyleSheet, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";

import { ScreenFrame } from "@/components/screen-frame";
import { Composer } from "@/features/chat/composer";
import { WorkspaceHeader } from "./workspace-header";
import type { WorkspaceTab } from "./workspace-types";

export function WorkspaceShell({
  active,
  children,
  onScrollToTop,
  onTabChange,
}: {
  active: WorkspaceTab;
  children: React.ReactNode;
  onScrollToTop?: () => void;
  onTabChange(tab: WorkspaceTab): void;
}): React.JSX.Element {
  const insets = useSafeAreaInsets();
  return (
    <ScreenFrame>
      <KeyboardAvoidingView
        style={styles.flex}
        behavior={Platform.OS === "ios" ? "padding" : undefined}
      >
        <WorkspaceHeader
          active={active}
          safeTop={insets.top}
          onScrollToTop={onScrollToTop}
          onTabChange={onTabChange}
        />
        <View style={styles.content}>{children}</View>
        <Composer safeBottom={insets.bottom} onScrollToTop={onScrollToTop} />
      </KeyboardAvoidingView>
    </ScreenFrame>
  );
}

const styles = StyleSheet.create({ flex: { flex: 1 }, content: { flex: 1, minHeight: 0 } });
