import React from "react";
import {
  Pressable,
  ScrollView,
  Text,
  TextInput,
  View,
} from "react-native";
import { ArrowUp, Eraser, Square, Zap } from "lucide-react-native";
import { Icon } from "./icons";
import { styles } from "./styles";
import { colors } from "./tokens";

export interface QuickTextOption {
  id: string;
  label: string;
  text: string;
  description?: string;
}

const defaultQuickCommands: QuickTextOption[] = [
  {
    id: "analyze",
    label: "Analyze Code",
    description: "Request code analysis and insights",
    text: "Please analyze the code in this file and provide insights about",
  },
  {
    id: "refactor",
    label: "Refactor",
    description: "Request code refactoring",
    text: "Please refactor this code to improve",
  },
  {
    id: "explain",
    label: "Explain",
    description: "Request code explanation",
    text: "Please explain how this code works",
  },
  {
    id: "test",
    label: "Write Tests",
    description: "Request test generation",
    text: "Please write unit tests for this code",
  },
  {
    id: "debug",
    label: "Debug",
    description: "Request debugging assistance",
    text: "Please help me debug this issue",
  },
  {
    id: "optimize",
    label: "Optimize",
    description: "Request code optimization",
    text: "Please optimize this code for better performance",
  },
  {
    id: "document",
    label: "Add Docs",
    description: "Request code documentation",
    text: "Please add documentation comments to this code",
  },
  {
    id: "review",
    label: "Review",
    description: "Request code review",
    text: "Please review this code for potential issues",
  },
];

export function Composer({
  disabled = false,
  isSending = false,
  message,
  onClear,
  onMessageChange,
  onScrollToTop,
  onSend,
  onStop,
  safeBottom,
}: {
  disabled?: boolean;
  isSending?: boolean;
  message: string;
  onClear?: () => void;
  onMessageChange: (message: string) => void;
  onScrollToTop?: () => void;
  onSend: () => void;
  onStop?: () => void;
  safeBottom: number;
}): React.JSX.Element {
  const canSend = !disabled && !isSending && message.trim().length > 0;
  const inputRef = React.useRef<TextInput | null>(null);
  const [selection, setSelection] = React.useState<
    { end: number; start: number } | undefined
  >(undefined);

  function handleMessageChange(nextMessage: string) {
    setSelection(undefined);
    onMessageChange(nextMessage);
  }

  function handleQuickCommand(text: string) {
    const nextMessage = `${text} `;
    onMessageChange(nextMessage);
    setSelection({ end: nextMessage.length, start: nextMessage.length });
    setTimeout(() => {
      inputRef.current?.focus();
    }, 0);
  }

  return (
    <View
      style={[
        styles.composer,
        {
          paddingBottom: 0,
        },
      ]}
    >
      <View style={styles.toolbarRow}>
        <View style={styles.toolbarSpacer} />
        <View style={styles.composerTopRightGroup}>
          <QuickTextDialog onCommandSelect={handleQuickCommand} />
          <View style={styles.toolbarSeparator} />
          <ComposerToolbarButton
            disabled={isSending}
            label="Clear session"
            onPress={onClear}
            testID="agw-clear-session"
          >
            <Eraser color={colors.icon} size={18} />
          </ComposerToolbarButton>
          <View style={styles.toolbarSeparator} />
          <ComposerToolbarButton
            label="Scroll to top"
            onPress={onScrollToTop}
            testID="agw-scroll-to-top"
          >
            <ArrowUp color={colors.icon} size={18} />
          </ComposerToolbarButton>
        </View>
      </View>
      <View style={styles.inputRow}>
        <View style={styles.inputShell}>
          <TextInput
            accessibilityLabel="Message"
            editable={!isSending}
            onChangeText={handleMessageChange}
            placeholder="Type a message..."
            placeholderTextColor={colors.muted}
            ref={inputRef}
            selection={selection}
            style={styles.messageInput}
            testID="agw-message-input"
            value={message}
          />
        </View>
        <Pressable
          accessibilityLabel={isSending ? "Stop generating" : "Send message"}
          accessibilityRole="button"
          disabled={isSending ? !onStop : !canSend}
          onPress={isSending ? (onStop ?? undefined) : canSend ? onSend : undefined}
          style={({ pressed }) => [
            styles.sendButton,
            (isSending ? !onStop : !canSend) && styles.sendButtonDisabled,
            pressed && styles.pressed,
          ]}
          testID={isSending ? "agw-stop-message" : "agw-send-message"}
        >
          {isSending ? (
            <Square color={colors.white} size={20} />
          ) : (
            <Icon color={colors.white} name="send" size={22} />
          )}
        </Pressable>
      </View>
    </View>
  );
}

function ComposerToolbarButton({
  children,
  disabled = false,
  label,
  onPress,
  testID,
}: {
  children: React.ReactNode;
  disabled?: boolean;
  label: string;
  onPress?: () => void;
  testID: string;
}): React.JSX.Element {
  return (
    <Pressable
      accessibilityLabel={label}
      accessibilityRole="button"
      disabled={disabled}
      onPress={disabled ? undefined : onPress}
      style={({ pressed }) => [
        styles.composerToolbarButton,
        disabled && styles.composerToolbarButtonDisabled,
        pressed && styles.pressed,
      ]}
      testID={testID}
    >
      {children}
    </Pressable>
  );
}

function QuickTextDialog({
  onCommandSelect,
  quickCommands = defaultQuickCommands,
}: {
  onCommandSelect: (text: string) => void;
  quickCommands?: QuickTextOption[];
}): React.JSX.Element {
  const [open, setOpen] = React.useState(false);

  function handleSelect(text: string) {
    setOpen(false);
    onCommandSelect(text);
  }

  return (
    <View style={styles.quickTextContainer}>
      <ComposerToolbarButton
        label="Quick Text Insert"
        onPress={() => setOpen((isOpen) => !isOpen)}
        testID="agw-quick-text-open"
      >
        <Zap color={colors.icon} size={18} />
      </ComposerToolbarButton>
      {open ? (
        <View style={styles.quickTextOverlay} testID="agw-quick-text-dialog">
          <View style={styles.quickTextSheet}>
            <View style={styles.quickTextHeader}>
              <Text style={styles.quickTextTitle}>Quick Text Insert</Text>
              <ComposerToolbarButton
                label="Close quick text"
                onPress={() => setOpen(false)}
                testID="agw-quick-text-close"
              >
                <Icon color={colors.icon} name="close" size={18} />
              </ComposerToolbarButton>
            </View>
            <ScrollView
              alwaysBounceVertical={false}
              contentContainerStyle={styles.quickTextList}
            >
              {quickCommands.map((option) => (
                <Pressable
                  accessibilityLabel={option.label}
                  accessibilityRole="button"
                  key={option.id}
                  onPress={() => handleSelect(option.text)}
                  style={({ pressed }) => [
                    styles.quickTextOption,
                    pressed && styles.historyItemPressed,
                  ]}
                  testID={`agw-quick-text-option-${option.id}`}
                >
                  <Text style={styles.quickTextOptionLabel}>{option.label}</Text>
                  {option.description ? (
                    <Text style={styles.quickTextOptionDescription}>
                      {option.description}
                    </Text>
                  ) : null}
                </Pressable>
              ))}
            </ScrollView>
          </View>
        </View>
      ) : null}
    </View>
  );
}
