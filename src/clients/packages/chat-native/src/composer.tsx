import {
  replaceSuggestion,
  resolveInputSuggestions,
  toFileSuggestions,
  type SuggestionItem,
} from "@agw/chat-core";
import type { PermissionMode } from "@agw/execution-core";
import { Image as ExpoImage } from "expo-image";
import {
  ArrowDown,
  ArrowUp,
  ChevronDown,
  ImagePlus,
  Lightbulb,
  Plus,
  ShieldAlert,
  Sparkles,
  Square,
  Wrench,
  X,
  type LucideIcon,
} from "lucide-react-native";
import React from "react";
import {
  ActivityIndicator,
  Modal,
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
  type NativeSyntheticEvent,
  type TextInputSelectionChangeEventData,
} from "react-native";

import { groupChatTargets } from "./chat-targets";
import { IconButton } from "./icon-button";
import { useNativeChat, useNativeComposer } from "./native-chat-provider";

const colors = {
  background: "#FFFFFF",
  surface: "#F4F3F8",
  segment: "#EEEDF3",
  border: "#C1C6D7",
  primary: "#0058BC",
  primaryBright: "#0070EB",
  primarySoft: "#D7E8FF",
  ink: "#1A1B1F",
  muted: "#414755",
  subtle: "#727783",
  white: "#FFFFFF",
  black: "#000000",
  danger: "#BA1A1A",
  overlay: "rgba(26, 27, 31, 0.32)",
} as const;
const radius = { sm: 8, md: 12, lg: 16, pill: 999 } as const;
const typography = {
  regular: "Inter_400Regular",
  medium: "Inter_500Medium",
  semibold: "Inter_600SemiBold",
} as const;

const quickCommands = [
  ["Analyze Code", "Please analyze the code and provide actionable insights about"],
  ["Refactor", "Please refactor this code to improve"],
  ["Explain", "Please explain how this code works"],
  ["Write Tests", "Please write unit tests for this code"],
  ["Debug", "Please help me debug this issue"],
  ["Optimize", "Please optimize this code for better performance"],
  ["Add Docs", "Please add documentation comments to this code"],
  ["Review", "Please review this code for potential issues"],
] as const;

const permissionLabels: Record<PermissionMode, string> = {
  fullAccess: "Full access",
  alwaysAsk: "Always ask",
  allowSameArguments: "Allow same arguments",
};

export function NativeChatComposer({
  safeBottom,
  onScrollToBottom,
  onScrollToTop,
}: {
  safeBottom: number;
  onScrollToBottom?: () => void;
  onScrollToTop?: () => void;
}): React.JSX.Element {
  const workspace = useNativeChat();
  const composer = useNativeComposer();
  const [targetPickerOpen, setTargetPickerOpen] = React.useState(false);
  const [permissionPickerOpen, setPermissionPickerOpen] = React.useState(false);
  const [addPanelOpen, setAddPanelOpen] = React.useState(false);
  const [suggestions, setSuggestions] = React.useState<SuggestionItem[]>([]);
  const [caretIndex, setCaretIndex] = React.useState(0);
  const [selectionOverride, setSelectionOverride] = React.useState<
    { start: number; end: number } | undefined
  >();
  const inputRef = React.useRef<TextInput>(null);
  const suggestionRequestRef = React.useRef(0);
  const canSend = Boolean(
    workspace.selectedProjectId &&
    workspace.selectedTarget &&
    (composer.text.trim() || composer.attachments.length > 0),
  );

  React.useEffect(() => {
    const requestId = suggestionRequestRef.current + 1;
    suggestionRequestRef.current = requestId;

    if (workspace.isExecuting) {
      setSuggestions([]);
      return;
    }

    const result = resolveInputSuggestions(
      composer.text,
      caretIndex,
      workspace.commandSource,
      async (keyword) => {
        if (!workspace.selectedProjectId || !workspace.filesService) {
          return [];
        }

        const response = await workspace.filesService.searchFiles(
          workspace.selectedProjectId,
          "",
          keyword,
          true,
        );
        return toFileSuggestions(response.results);
      },
    );

    if (result instanceof Promise) {
      setSuggestions([]);
      void result
        .then((nextSuggestions) => {
          if (suggestionRequestRef.current === requestId) {
            setSuggestions(nextSuggestions);
          }
        })
        .catch(() => {
          if (suggestionRequestRef.current === requestId) {
            setSuggestions([]);
          }
        });
      return;
    }

    setSuggestions(result);
  }, [
    caretIndex,
    composer.text,
    workspace.commandSource,
    workspace.filesService,
    workspace.isExecuting,
    workspace.selectedProjectId,
    workspace.selectedTargetValue,
  ]);

  React.useEffect(() => {
    suggestionRequestRef.current += 1;
    setSuggestions([]);
  }, [workspace.selectedContextId]);

  const handleSelectionChange = (
    event: NativeSyntheticEvent<TextInputSelectionChangeEventData>,
  ) => {
    const selection = event.nativeEvent.selection;
    setCaretIndex(selection.start);
    if (selectionOverride) setSelectionOverride(undefined);
  };

  const handleTextChange = (value: string) => {
    if (selectionOverride) setSelectionOverride(undefined);
    composer.setText(value);
  };

  const handleSuggestionSelect = (suggestion: SuggestionItem) => {
    const replacement = replaceSuggestion(composer.text, suggestion.text, caretIndex);
    suggestionRequestRef.current += 1;
    setSuggestions([]);
    setCaretIndex(replacement.caretIndex);
    setSelectionOverride({
      start: replacement.caretIndex,
      end: replacement.caretIndex,
    });
    composer.setText(replacement.value);
    inputRef.current?.focus();
  };

  return (
    <View style={[styles.container, { paddingBottom: Math.max(8, safeBottom) }]}>
      <View style={styles.topToolbar}>
        <Pressable
          accessibilityLabel="Choose an agent"
          accessibilityRole="button"
          disabled={workspace.isExecuting}
          onPress={() => setTargetPickerOpen(true)}
          style={({ pressed }) => [
            styles.utilityTarget,
            workspace.isExecuting && styles.disabled,
            pressed && styles.utilityPressed,
          ]}
        >
          <Text numberOfLines={1} style={styles.utilityTargetText}>
            {workspace.selectedTarget?.label ?? "Select agent"}
          </Text>
          <ChevronDown color={colors.subtle} size={15} />
        </Pressable>
        <View style={styles.utilityToolbar}>
          <UtilityButton
            icon={ImagePlus}
            label="Attach images"
            divided={false}
            disabled={workspace.isExecuting}
            onPress={() => void composer.pickImages()}
          />
          <UtilityButton
            icon={ArrowDown}
            label="Scroll to bottom"
            disabled={!onScrollToBottom}
            onPress={onScrollToBottom}
          />
          <UtilityButton
            icon={ArrowUp}
            label="Scroll to top"
            disabled={!onScrollToTop}
            onPress={onScrollToTop}
          />
        </View>
      </View>

      <View style={styles.inputStack}>
        <ComposerSuggestions suggestions={suggestions} onSelect={handleSuggestionSelect} />
        <View style={styles.inputCard}>
          {composer.attachments.length ? (
            <ScrollView
              horizontal
              showsHorizontalScrollIndicator={false}
              contentContainerStyle={styles.attachments}
            >
              {composer.attachments.map((attachment) => (
                <View key={attachment.id} style={styles.attachment}>
                  <ExpoImage
                    source={{ uri: attachment.dataUrl }}
                    style={styles.attachmentImage}
                    contentFit="cover"
                  />
                  <Pressable
                    accessibilityLabel={`Remove ${attachment.name}`}
                    disabled={workspace.isExecuting}
                    onPress={() => composer.removeAttachment(attachment.id)}
                    style={({ pressed }) => [
                      styles.removeAttachment,
                      workspace.isExecuting && styles.disabled,
                      pressed && styles.pressed,
                    ]}
                  >
                    <X color={colors.black} size={18} strokeWidth={2.5} />
                  </Pressable>
                </View>
              ))}
            </ScrollView>
          ) : null}

          <TextInput
            ref={inputRef}
            accessibilityLabel="Message"
            multiline
            editable={!workspace.isExecuting}
            placeholder="Type your message..."
            placeholderTextColor={colors.subtle}
            value={composer.text}
            selection={selectionOverride}
            onChangeText={handleTextChange}
            onSelectionChange={handleSelectionChange}
            style={styles.input}
          />

          <View style={styles.bottomBar}>
            <View style={styles.bottomLeft}>
              <Pressable
                accessibilityLabel="Add"
                accessibilityRole="button"
                disabled={workspace.isExecuting}
                onPress={() => setAddPanelOpen(true)}
                style={({ pressed }) => [
                  styles.addButton,
                  workspace.isExecuting && styles.disabled,
                  pressed && styles.utilityPressed,
                ]}
              >
                <Plus color={colors.ink} size={25} strokeWidth={2} />
              </Pressable>
              <Pressable
                accessibilityLabel="Tool permission mode"
                accessibilityRole="button"
                disabled={workspace.isExecuting}
                onPress={() => setPermissionPickerOpen(true)}
                style={({ pressed }) => [
                  styles.permissionButton,
                  workspace.isExecuting && styles.disabled,
                  pressed && styles.utilityPressed,
                ]}
              >
                <ShieldAlert color={colors.subtle} size={18} />
                <Text
                  numberOfLines={1}
                  style={[
                    styles.permissionText,
                    workspace.permissionMode === "fullAccess" && styles.fullAccessText,
                  ]}
                >
                  {permissionLabels[workspace.permissionMode]}
                </Text>
                <ChevronDown color={colors.subtle} size={15} />
              </Pressable>
            </View>

            <Pressable
              accessibilityLabel={workspace.isExecuting ? "Stop generating" : "Send message"}
              accessibilityRole="button"
              disabled={workspace.isExecuting ? false : !canSend}
              onPress={
                workspace.isExecuting ? workspace.stopExecution : () => void composer.submit()
              }
              style={({ pressed }) => [
                styles.sendButton,
                !workspace.isExecuting && !canSend && styles.sendDisabled,
                pressed && styles.pressed,
              ]}
            >
              {workspace.isExecuting ? (
                <Square color={colors.white} fill={colors.white} size={16} />
              ) : (
                <ArrowUp color={colors.white} size={24} strokeWidth={2.2} />
              )}
            </Pressable>
          </View>
        </View>
      </View>

      {composer.error ? <Text style={styles.error}>{composer.error}</Text> : null}
      <TargetPicker open={targetPickerOpen} onClose={() => setTargetPickerOpen(false)} />
      <PermissionPicker
        open={permissionPickerOpen}
        onClose={() => setPermissionPickerOpen(false)}
      />
      <AddPanel
        open={addPanelOpen}
        bottomOffset={Math.max(8, safeBottom) + 52}
        onClose={() => setAddPanelOpen(false)}
      />
      <QuickTextPicker />
    </View>
  );
}

export { NativeChatComposer as Composer };

function ComposerSuggestions({
  suggestions,
  onSelect,
}: {
  suggestions: SuggestionItem[];
  onSelect(suggestion: SuggestionItem): void;
}): React.JSX.Element | null {
  if (suggestions.length === 0) return null;

  return (
    <View accessibilityLabel="Suggestions" style={styles.suggestionsPanel}>
      <Text style={styles.suggestionsTitle}>Suggestions</Text>
      <ScrollView
        bounces={false}
        keyboardShouldPersistTaps="handled"
        showsVerticalScrollIndicator={false}
      >
        {suggestions.map((suggestion, index) => (
          <Pressable
            key={`${suggestion.kind ?? "file"}:${suggestion.text}:${index}`}
            accessibilityLabel={`Use suggestion ${suggestion.text}`}
            accessibilityRole="button"
            onPress={() => onSelect(suggestion)}
            style={({ pressed }) => [styles.suggestionRow, pressed && styles.utilityPressed]}
          >
            <View style={styles.suggestionHeader}>
              <Text numberOfLines={1} style={styles.suggestionText}>
                {suggestion.text}
              </Text>
              {suggestion.kind ? (
                <Text style={styles.suggestionKind}>{suggestion.kind}</Text>
              ) : null}
            </View>
            {suggestion.description ? (
              <Text numberOfLines={2} style={styles.suggestionDescription}>
                {suggestion.description}
              </Text>
            ) : null}
          </Pressable>
        ))}
      </ScrollView>
    </View>
  );
}

function UtilityButton({
  icon: Icon,
  label,
  divided = true,
  disabled = false,
  onPress,
}: {
  icon: LucideIcon;
  label: string;
  divided?: boolean;
  disabled?: boolean;
  onPress?: () => void;
}): React.JSX.Element {
  return (
    <Pressable
      accessibilityLabel={label}
      accessibilityRole="button"
      disabled={disabled}
      onPress={onPress}
      style={({ pressed }) => [
        styles.utilityButton,
        divided && styles.utilityDivider,
        disabled && styles.disabled,
        pressed && styles.utilityPressed,
      ]}
    >
      <Icon color={colors.ink} size={20} strokeWidth={2} />
    </Pressable>
  );
}

function AddPanel({
  open,
  bottomOffset,
  onClose,
}: {
  open: boolean;
  bottomOffset: number;
  onClose(): void;
}): React.JSX.Element {
  const workspace = useNativeChat();
  const composer = useNativeComposer();
  const skills = workspace.agentSuggestions.filter((suggestion) => suggestion.kind === "skill");
  const tools = workspace.agentSuggestions.filter(
    (suggestion) =>
      suggestion.kind === "tool" &&
      suggestion.text !== "/mode_get" &&
      suggestion.text !== "/mode_set",
  );
  const hasActions = workspace.supportsAgentMode || skills.length > 0 || tools.length > 0;

  const selectCommand = (command: string) => {
    composer.selectQuickText(command);
    onClose();
  };

  return (
    <Modal animationType="fade" transparent visible={open} onRequestClose={onClose}>
      <Pressable style={styles.addBackdrop} onPress={onClose}>
        <Pressable
          accessibilityRole="menu"
          onPress={(event) => event.stopPropagation()}
          style={[styles.addPanel, { marginBottom: bottomOffset }]}
        >
          <Text style={styles.addTitle}>Add</Text>
          <ScrollView
            bounces={false}
            showsVerticalScrollIndicator
            contentContainerStyle={styles.addContent}
          >
            {workspace.isSuggestionsLoading ? (
              <View style={styles.addStatus}>
                <ActivityIndicator color={colors.muted} size="small" />
                <Text style={styles.addStatusText}>Loading actions…</Text>
              </View>
            ) : null}

            {!workspace.isSuggestionsLoading && workspace.supportsAgentMode ? (
              <CapabilityRow
                icon={Lightbulb}
                title="Plan mode"
                description={`Turn plan mode ${workspace.agentMode === "plan" ? "off" : "on"}`}
                selected={workspace.agentMode === "plan"}
                onPress={() => {
                  workspace.setAgentMode(workspace.agentMode === "plan" ? "execute" : "plan");
                  onClose();
                }}
              />
            ) : null}

            {!workspace.isSuggestionsLoading && skills.length > 0 ? (
              <CapabilityGroup
                label="Skills"
                icon={Sparkles}
                items={skills}
                onSelect={selectCommand}
              />
            ) : null}

            {!workspace.isSuggestionsLoading && tools.length > 0 ? (
              <CapabilityGroup label="Tools" icon={Wrench} items={tools} onSelect={selectCommand} />
            ) : null}

            {!workspace.isSuggestionsLoading && workspace.suggestionsError ? (
              <Text style={styles.addError}>{workspace.suggestionsError}</Text>
            ) : null}

            {!workspace.isSuggestionsLoading && !workspace.suggestionsError && !hasActions ? (
              <Text style={styles.addEmpty}>No actions are available for this target.</Text>
            ) : null}
          </ScrollView>
        </Pressable>
      </Pressable>
    </Modal>
  );
}

function CapabilityGroup({
  label,
  icon,
  items,
  onSelect,
}: {
  label: "Skills" | "Tools";
  icon: LucideIcon;
  items: ReturnType<typeof useNativeChat>["agentSuggestions"];
  onSelect(command: string): void;
}): React.JSX.Element {
  return (
    <View style={styles.capabilityGroup}>
      <Text style={styles.capabilityLabel}>{label}</Text>
      {items.map((item) => (
        <CapabilityRow
          key={`${item.kind}:${item.text}`}
          icon={icon}
          title={item.text}
          description={item.description}
          onPress={() => onSelect(item.text)}
        />
      ))}
    </View>
  );
}

function CapabilityRow({
  icon: Icon,
  title,
  description,
  selected = false,
  onPress,
}: {
  icon: LucideIcon;
  title: string;
  description?: string;
  selected?: boolean;
  onPress(): void;
}): React.JSX.Element {
  return (
    <Pressable
      accessibilityRole="menuitem"
      onPress={onPress}
      style={({ pressed }) => [
        styles.capabilityRow,
        selected && styles.capabilityRowSelected,
        pressed && styles.utilityPressed,
      ]}
    >
      <Icon color={colors.muted} size={20} strokeWidth={1.8} />
      <View style={styles.capabilityCopy}>
        <Text numberOfLines={1} style={styles.capabilityTitle}>
          {title}
        </Text>
        {description ? (
          <Text numberOfLines={1} style={styles.capabilityDescription}>
            {description}
          </Text>
        ) : null}
      </View>
    </Pressable>
  );
}

function TargetPicker({ open, onClose }: { open: boolean; onClose(): void }): React.JSX.Element {
  const workspace = useNativeChat();
  const groups = groupChatTargets(workspace.targets);
  return (
    <Modal animationType="fade" transparent visible={open} onRequestClose={onClose}>
      <Pressable style={styles.modalBackdrop} onPress={onClose}>
        <View style={styles.sheet}>
          <Text style={styles.sheetTitle}>Choose an agent</Text>
          <ScrollView
            bounces={false}
            showsVerticalScrollIndicator
            style={styles.targetList}
            contentContainerStyle={styles.targetListContent}
          >
            {groups.map((group) => (
              <View key={group.type} style={styles.targetGroup}>
                <Text style={styles.targetGroupLabel}>{group.label}</Text>
                {group.targets.map((target) => {
                  const value = `${target.type}:${target.id}`;
                  return (
                    <Pressable
                      key={value}
                      onPress={() => {
                        workspace.selectTarget(value);
                        onClose();
                      }}
                      style={styles.option}
                    >
                      <Text style={styles.optionTitle}>{target.label}</Text>
                      {workspace.selectedTargetValue === value ? (
                        <View style={styles.selectedDot} />
                      ) : null}
                    </Pressable>
                  );
                })}
              </View>
            ))}
          </ScrollView>
        </View>
      </Pressable>
    </Modal>
  );
}

function PermissionPicker({
  open,
  onClose,
}: {
  open: boolean;
  onClose(): void;
}): React.JSX.Element {
  const workspace = useNativeChat();
  return (
    <Modal animationType="fade" transparent visible={open} onRequestClose={onClose}>
      <Pressable style={styles.modalBackdrop} onPress={onClose}>
        <View style={styles.sheet}>
          <Text style={styles.sheetTitle}>Tool permission</Text>
          {(Object.keys(permissionLabels) as PermissionMode[]).map((mode) => (
            <Pressable
              key={mode}
              onPress={() => {
                workspace.setPermissionMode(mode);
                onClose();
              }}
              style={styles.option}
            >
              <View style={styles.permissionOptionCopy}>
                <ShieldAlert color={mode === "fullAccess" ? "#E45100" : colors.muted} size={18} />
                <Text style={[styles.optionTitle, mode === "fullAccess" && styles.fullAccessText]}>
                  {permissionLabels[mode]}
                </Text>
              </View>
              {workspace.permissionMode === mode ? <View style={styles.selectedDot} /> : null}
            </Pressable>
          ))}
        </View>
      </Pressable>
    </Modal>
  );
}

function QuickTextPicker(): React.JSX.Element {
  const composer = useNativeComposer();
  return (
    <Modal
      animationType="slide"
      transparent
      visible={composer.quickTextOpen}
      onRequestClose={composer.closeQuickText}
    >
      <Pressable style={styles.modalBackdrop} onPress={composer.closeQuickText}>
        <View style={styles.sheet}>
          <View style={styles.sheetHeader}>
            <Text style={styles.sheetTitle}>Quick Text</Text>
            <IconButton icon={X} label="Close quick text" onPress={composer.closeQuickText} />
          </View>
          {quickCommands.map(([label, value]) => (
            <Pressable
              key={label}
              onPress={() => composer.selectQuickText(value)}
              style={styles.option}
            >
              <Text style={styles.optionTitle}>{label}</Text>
            </Pressable>
          ))}
        </View>
      </Pressable>
    </Modal>
  );
}

const styles = StyleSheet.create({
  container: {
    paddingHorizontal: 12,
    paddingTop: 8,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderColor: colors.border,
    backgroundColor: colors.background,
  },
  topToolbar: {
    minWidth: 0,
    marginBottom: 8,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 8,
  },
  utilityToolbar: {
    height: 40,
    flexShrink: 0,
    flexDirection: "row",
    alignItems: "stretch",
    overflow: "hidden",
    borderWidth: 1,
    borderColor: colors.border,
    borderRadius: 12,
    backgroundColor: colors.white,
  },
  utilityTarget: {
    minWidth: 108,
    maxWidth: 166,
    height: 40,
    flexShrink: 1,
    paddingHorizontal: 12,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 7,
    overflow: "hidden",
    borderWidth: 1,
    borderColor: colors.border,
    borderRadius: 12,
    backgroundColor: colors.white,
  },
  utilityTargetText: {
    flexShrink: 1,
    color: colors.muted,
    fontFamily: typography.medium,
    fontSize: 13,
  },
  utilityButton: {
    width: 42,
    alignItems: "center",
    justifyContent: "center",
  },
  utilityDivider: {
    borderLeftWidth: StyleSheet.hairlineWidth,
    borderColor: colors.border,
  },
  utilityPressed: { backgroundColor: colors.segment },
  inputStack: { position: "relative", zIndex: 20 },
  suggestionsPanel: {
    position: "absolute",
    left: 0,
    right: 0,
    bottom: "100%",
    maxHeight: 280,
    paddingHorizontal: 6,
    paddingTop: 8,
    paddingBottom: 6,
    overflow: "hidden",
    borderWidth: 1,
    borderColor: colors.border,
    borderRadius: 16,
    backgroundColor: colors.white,
    shadowColor: colors.ink,
    shadowOffset: { width: 0, height: 8 },
    shadowOpacity: 0.16,
    shadowRadius: 18,
    elevation: 14,
  },
  suggestionsTitle: {
    paddingHorizontal: 8,
    paddingBottom: 5,
    color: colors.subtle,
    fontFamily: typography.semibold,
    fontSize: 11,
    letterSpacing: 0.6,
    textTransform: "uppercase",
  },
  suggestionRow: {
    minHeight: 48,
    paddingHorizontal: 9,
    paddingVertical: 7,
    justifyContent: "center",
    gap: 3,
    borderRadius: 10,
  },
  suggestionHeader: {
    minWidth: 0,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
  },
  suggestionText: {
    flex: 1,
    color: colors.ink,
    fontFamily: typography.medium,
    fontSize: 13,
  },
  suggestionKind: {
    flexShrink: 0,
    paddingHorizontal: 6,
    paddingVertical: 2,
    overflow: "hidden",
    borderRadius: radius.pill,
    color: colors.primary,
    backgroundColor: colors.primarySoft,
    fontFamily: typography.medium,
    fontSize: 10,
    textTransform: "uppercase",
  },
  suggestionDescription: {
    color: colors.subtle,
    fontFamily: typography.regular,
    fontSize: 11,
    lineHeight: 15,
  },
  inputCard: {
    minHeight: 120,
    paddingHorizontal: 8,
    paddingTop: 12,
    paddingBottom: 4,
    borderWidth: 1,
    borderColor: colors.border,
    borderRadius: 20,
    backgroundColor: colors.white,
    shadowColor: colors.ink,
    shadowOffset: { width: 0, height: 2 },
    shadowOpacity: 0.1,
    shadowRadius: 5,
    elevation: 3,
  },
  attachments: { gap: 8, paddingBottom: 8, paddingRight: 6 },
  attachment: { width: 72, height: 72, borderRadius: 12, overflow: "visible" },
  attachmentImage: {
    width: 72,
    height: 72,
    borderRadius: 12,
    backgroundColor: colors.surface,
  },
  removeAttachment: {
    position: "absolute",
    right: 4,
    top: 4,
    width: 24,
    height: 24,
    borderRadius: 12,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: colors.white,
  },
  input: {
    minHeight: 62,
    maxHeight: 120,
    paddingHorizontal: 4,
    paddingTop: 8,
    paddingBottom: 4,
    color: colors.ink,
    fontFamily: typography.regular,
    fontSize: 16,
    lineHeight: 22,
    textAlignVertical: "top",
  },
  bottomBar: {
    minHeight: 42,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 8,
  },
  bottomLeft: { flex: 1, minWidth: 0, flexDirection: "row", alignItems: "center", gap: 2 },
  addButton: {
    width: 38,
    height: 38,
    flexShrink: 0,
    alignItems: "center",
    justifyContent: "center",
    borderRadius: 19,
  },
  permissionButton: {
    minWidth: 0,
    maxWidth: 206,
    height: 38,
    paddingHorizontal: 8,
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    borderRadius: radius.pill,
  },
  permissionText: {
    flexShrink: 1,
    color: colors.muted,
    fontFamily: typography.medium,
    fontSize: 14,
  },
  fullAccessText: { color: "#E45100" },
  sendButton: {
    width: 32,
    height: 32,
    flexShrink: 0,
    borderRadius: 16,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: colors.ink,
  },
  sendDisabled: { opacity: 0.28 },
  pressed: { opacity: 0.72 },
  disabled: { opacity: 0.38 },
  error: {
    marginTop: 6,
    color: colors.danger,
    fontFamily: typography.regular,
    fontSize: 11,
  },
  modalBackdrop: { flex: 1, justifyContent: "flex-end", backgroundColor: colors.overlay },
  sheet: {
    maxHeight: "72%",
    paddingHorizontal: 16,
    paddingTop: 18,
    paddingBottom: 28,
    borderTopLeftRadius: 22,
    borderTopRightRadius: 22,
    backgroundColor: colors.background,
  },
  sheetHeader: { flexDirection: "row", alignItems: "center", justifyContent: "space-between" },
  sheetTitle: { color: colors.ink, fontFamily: typography.semibold, fontSize: 18, marginBottom: 8 },
  targetList: { flexShrink: 1 },
  targetListContent: { paddingBottom: 4 },
  targetGroup: { paddingTop: 8 },
  targetGroupLabel: {
    paddingHorizontal: 8,
    paddingBottom: 4,
    color: colors.muted,
    fontFamily: typography.regular,
    fontSize: 14,
  },
  option: {
    minHeight: 54,
    paddingHorizontal: 8,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderColor: colors.border,
  },
  optionTitle: { color: colors.ink, fontFamily: typography.medium, fontSize: 14 },
  permissionOptionCopy: { flexDirection: "row", alignItems: "center", gap: 8 },
  selectedDot: { width: 10, height: 10, borderRadius: 5, backgroundColor: colors.primary },
  addBackdrop: {
    flex: 1,
    justifyContent: "flex-end",
    backgroundColor: "rgba(24, 26, 34, 0.18)",
  },
  addPanel: {
    maxHeight: "72%",
    marginHorizontal: 16,
    overflow: "hidden",
    borderWidth: 1,
    borderColor: colors.border,
    borderRadius: 20,
    backgroundColor: colors.white,
    shadowColor: colors.ink,
    shadowOffset: { width: 0, height: 12 },
    shadowOpacity: 0.18,
    shadowRadius: 24,
    elevation: 12,
  },
  addTitle: {
    paddingHorizontal: 18,
    paddingTop: 16,
    paddingBottom: 8,
    color: colors.muted,
    fontFamily: typography.regular,
    fontSize: 14,
  },
  addContent: { paddingHorizontal: 8, paddingBottom: 10 },
  addStatus: {
    minHeight: 64,
    paddingHorizontal: 12,
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
  },
  addStatusText: { color: colors.muted, fontFamily: typography.regular, fontSize: 14 },
  addEmpty: {
    paddingHorizontal: 12,
    paddingVertical: 18,
    color: colors.muted,
    fontFamily: typography.regular,
    fontSize: 14,
  },
  addError: {
    paddingHorizontal: 12,
    paddingVertical: 18,
    color: colors.danger,
    fontFamily: typography.regular,
    fontSize: 13,
  },
  capabilityGroup: {
    marginTop: 6,
    paddingTop: 8,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderColor: colors.border,
  },
  capabilityLabel: {
    paddingHorizontal: 10,
    paddingBottom: 4,
    color: colors.muted,
    fontFamily: typography.regular,
    fontSize: 14,
  },
  capabilityRow: {
    minHeight: 58,
    paddingHorizontal: 10,
    paddingVertical: 8,
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
    borderRadius: 14,
  },
  capabilityRowSelected: { backgroundColor: colors.segment },
  capabilityCopy: { flex: 1, minWidth: 0 },
  capabilityTitle: {
    color: colors.ink,
    fontFamily: typography.medium,
    fontSize: 16,
    lineHeight: 20,
  },
  capabilityDescription: {
    marginTop: 2,
    color: colors.muted,
    fontFamily: typography.regular,
    fontSize: 13,
    lineHeight: 17,
  },
});
