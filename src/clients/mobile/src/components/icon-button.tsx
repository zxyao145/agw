import React from "react";
import { Pressable, StyleSheet, type PressableProps } from "react-native";
import type { LucideIcon } from "lucide-react-native";

import { colors, layout } from "@/theme/tokens";

export function IconButton({
  icon: Icon,
  label,
  color = colors.ink,
  size = 22,
  disabled,
  style,
  ...props
}: Omit<PressableProps, "children"> & {
  icon: LucideIcon;
  label: string;
  color?: string;
  size?: number;
}): React.JSX.Element {
  return (
    <Pressable
      {...props}
      accessibilityLabel={label}
      accessibilityRole="button"
      disabled={disabled}
      hitSlop={4}
      style={(state) => [
        styles.button,
        disabled && styles.disabled,
        state.pressed && styles.pressed,
        typeof style === "function" ? style(state) : style,
      ]}
    >
      <Icon color={color} size={size} strokeWidth={2} />
    </Pressable>
  );
}

const styles = StyleSheet.create({
  button: {
    width: layout.minHitTarget,
    height: layout.minHitTarget,
    alignItems: "center",
    justifyContent: "center",
    borderRadius: 22,
  },
  pressed: { backgroundColor: colors.segment },
  disabled: { opacity: 0.38 },
});
