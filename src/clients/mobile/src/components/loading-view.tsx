import React from "react";
import { ActivityIndicator, StyleSheet, Text, View } from "react-native";

import { colors, typography } from "@/theme/tokens";

export function LoadingView({ label = "Loading Agw" }: { label?: string }): React.JSX.Element {
  return (
    <View style={styles.container}>
      <ActivityIndicator color={colors.primary} />
      <Text style={styles.label}>{label}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    alignItems: "center",
    justifyContent: "center",
    gap: 12,
    backgroundColor: colors.background,
  },
  label: { color: colors.muted, fontFamily: typography.medium, fontSize: 14 },
});
