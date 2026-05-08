import React from "react";
import { Pressable, TextInput, View } from "react-native";
import { Icon, IconButton } from "./icons";
import { styles } from "./styles";
import { colors } from "./tokens";

export function Composer({
  safeBottom,
}: {
  safeBottom: number;
}): React.JSX.Element {
  return (
    <View
      style={[
        styles.composer,
        { paddingBottom: Math.max(32, safeBottom + 20) },
      ]}
    >
      <View style={styles.toolbarRow}>
        <View style={styles.toolbarGroup}>
          <IconButton icon="image" label="Attach image" size={34} />
          <IconButton icon="paperclip" label="Attach file" size={34} />
          <IconButton icon="smile" label="Emoji" size={36} />
        </View>
        <View style={styles.toolbarGroup}>
          <IconButton icon="mic" label="Record voice" size={34} />
          <IconButton icon="circlePlus" label="More tools" size={36} />
        </View>
      </View>
      <View style={styles.inputRow}>
        <View style={styles.inputShell}>
          <TextInput
            accessibilityLabel="Message"
            placeholder="Type a message..."
            placeholderTextColor={colors.muted}
            style={styles.messageInput}
          />
        </View>
        <Pressable
          accessibilityLabel="Send message"
          accessibilityRole="button"
          style={({ pressed }) => [
            styles.sendButton,
            pressed && styles.pressed,
          ]}
        >
          <Icon color={colors.white} name="send" size={22} />
        </Pressable>
      </View>
    </View>
  );
}
