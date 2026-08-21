import React from "react";
import { StyleSheet, View, type ViewProps } from "react-native";

import { colors, layout } from "@/theme/tokens";

export function ScreenFrame({ children, style, ...props }: ViewProps): React.JSX.Element {
  return (
    <View style={styles.outer}>
      <View {...props} style={[styles.inner, style]}>
        {children}
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  outer: {
    flex: 1,
    alignItems: "center",
    backgroundColor: colors.background,
  },
  inner: {
    flex: 1,
    width: "100%",
    maxWidth: layout.maxPhoneWidth,
    backgroundColor: colors.background,
  },
});
