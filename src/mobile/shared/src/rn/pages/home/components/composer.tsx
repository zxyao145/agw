import React from "react";
import { Pressable, TextInput, View } from "react-native";
import { Icon, IconButton } from "./icons";
import { styles } from "./styles";
import { colors } from "./tokens";

export function Composer({
  disabled = false,
  isSending = false,
  message,
  onMessageChange,
  onSend,
  safeBottom,
}: {
  disabled?: boolean;
  isSending?: boolean;
  message: string;
  onMessageChange: (message: string) => void;
  onSend: () => void;
  safeBottom: number;
}): React.JSX.Element {
  const canSend = !disabled && !isSending && message.trim().length > 0;

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
            editable={!isSending}
            onChangeText={onMessageChange}
            placeholder="Type a message..."
            placeholderTextColor={colors.muted}
            style={styles.messageInput}
            testID="agw-message-input"
            value={message}
          />
        </View>
        <Pressable
          accessibilityLabel="Send message"
          accessibilityRole="button"
          disabled={!canSend}
          onPress={canSend ? onSend : undefined}
          style={({ pressed }) => [
            styles.sendButton,
            !canSend && styles.sendButtonDisabled,
            pressed && styles.pressed,
          ]}
          testID="agw-send-message"
        >
          <Icon color={colors.white} name="send" size={22} />
        </Pressable>
      </View>
    </View>
  );
}
