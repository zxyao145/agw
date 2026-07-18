import React from "react";
import {
  Image,
  StyleSheet,
  Text,
  View,
  type StyleProp,
  type TextStyle,
  type ViewStyle,
} from "react-native";

const logoSource = require("../../../../../assets/agw-logo.png");

export function AgwLogo({
  label = "Agw",
  labelStyle,
  showLabel = true,
  size = 32,
  style,
}: {
  label?: string;
  labelStyle?: StyleProp<TextStyle>;
  showLabel?: boolean;
  size?: number;
  style?: StyleProp<ViewStyle>;
}): React.JSX.Element {
  return (
    <View
      accessibilityLabel={showLabel ? undefined : label}
      accessibilityRole={showLabel ? undefined : "image"}
      accessible={!showLabel}
      style={[styles.container, style]}
    >
      <Image
        accessibilityIgnoresInvertColors
        accessible={false}
        resizeMode="contain"
        source={logoSource}
        style={{ height: size, width: size }}
      />
      {showLabel ? <Text style={[styles.label, labelStyle]}>{label}</Text> : null}
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    alignItems: "center",
    flexDirection: "row",
    gap: 8,
  },
  label: {
    fontWeight: "700",
  },
});
