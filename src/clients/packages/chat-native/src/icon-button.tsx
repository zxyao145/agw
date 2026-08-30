import * as React from "react";
import type { LucideIcon } from "lucide-react-native";
import { Pressable, StyleSheet, type PressableProps } from "react-native";

import { defaultNativeChatTheme } from "./theme";

export function IconButton({
  icon: Icon,
  label,
  color = defaultNativeChatTheme.ink,
  size = 22,
  disabled,
  style,
  ...props
}: Omit<PressableProps, "children"> & {
  icon: LucideIcon;
  label: string;
  color?: string;
  size?: number;
}) {
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
    width: 44,
    height: 44,
    alignItems: "center",
    justifyContent: "center",
    borderRadius: 22,
  },
  pressed: { backgroundColor: "#EEEDF3" },
  disabled: { opacity: 0.38 },
});
