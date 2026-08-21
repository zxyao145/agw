import React from "react";
import { Image, StyleSheet, Text, View } from "react-native";

import { colors, typography } from "@/theme/tokens";

export function AgwBrand(): React.JSX.Element {
  return (
    <View style={styles.row}>
      <Image source={require("../../assets/agw-logo.png")} style={styles.logo} />
      <Text style={styles.label}>Agw</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  row: { flexDirection: "row", alignItems: "center", gap: 8 },
  logo: { width: 30, height: 30, borderRadius: 8 },
  label: { color: colors.primary, fontFamily: typography.semibold, fontSize: 20 },
});
