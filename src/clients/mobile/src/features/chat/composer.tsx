import type { PermissionMode } from "@agw/execution-core";
import { Image as ExpoImage } from "expo-image";
import {
  ArrowUp,
  ChevronDown,
  Eraser,
  ImagePlus,
  Lightbulb,
  Plus,
  ShieldAlert,
  Sparkles,
  Square,
  Wrench,
  X,
  Zap,
  type LucideIcon,
} from "lucide-react-native";
import React from "react";
import {
  ActivityIndicator,
  Alert,
  Modal,
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
} from "react-native";

import { IconButton } from "@/components/icon-button";
import { useWorkspace } from "@/features/workspace/workspace-provider";
import { colors, radius, typography } from "@/theme/tokens";
import { useComposer } from "./composer-provider";

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

export function Composer({
  safeBottom,
  onScrollToTop,
}: {
  safeBottom: number;
  onScrollToTop?: () => void;
}): React.JSX.Element {
  const workspace = useWorkspace();
  const composer = useComposer();
  const [targetPickerOpen, setTargetPickerOpen] = React.useState(false);
  const [permissionPickerOpen, setPermissionPickerOpen] = React.useState(false);
  const [addPanelOpen, setAddPanelOpen] = React.useState(false);
  const canSend = Boolean(
    workspace.selectedProjectId &&
    workspace.selectedTarget &&
    (composer.text.trim() || composer.attachments.length > 0),
  );

  const clearConversation = () => {
    if (!workspace.selectedContextId || workspace.isExecuting) return;
    Alert.alert("Clear conversation?", "Messages and execution records will be removed.", [
      { text: "Cancel", style: "cancel" },
      {
        text: "Clear",
        style: "destructive",
        onPress: () => void workspace.clearCurrentContext(),
      },
    ]);
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
            icon={Zap}
            label="Open quick text"
            disabled={workspace.isExecuting}
            onPress={composer.openQuickText}
          />
          <UtilityButton
            icon={Eraser}
            label="Clear conversation"
            disabled={!workspace.selectedContextId || workspace.isExecuting}
            onPress={clearConversation}
          />
          <UtilityButton
            icon={ArrowUp}
            label="Scroll to top"
            disabled={!onScrollToTop}
            onPress={onScrollToTop}
          />
        </View>
      </View>

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
          accessibilityLabel="Message"
          multiline
          editable={!workspace.isExecuting}
          placeholder="Type your message..."
          placeholderTextColor={colors.subtle}
          value={composer.text}
          onChangeText={composer.setText}
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
            onPress={workspace.isExecuting ? workspace.stopExecution : () => void composer.submit()}
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
  const workspace = useWorkspace();
  const composer = useComposer();
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
  items: ReturnType<typeof useWorkspace>["agentSuggestions"];
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
  const workspace = useWorkspace();
  return (
    <Modal animationType="fade" transparent visible={open} onRequestClose={onClose}>
      <Pressable style={styles.modalBackdrop} onPress={onClose}>
        <View style={styles.sheet}>
          <Text style={styles.sheetTitle}>Choose an agent</Text>
          {workspace.targets.map((target) => {
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
                <View>
                  <Text style={styles.optionTitle}>{target.label}</Text>
                  <Text style={styles.optionMeta}>
                    {target.type === "agentflow" ? "Agentflow" : "Agent"}
                  </Text>
                </View>
                {workspace.selectedTargetValue === value ? (
                  <View style={styles.selectedDot} />
                ) : null}
              </Pressable>
            );
          })}
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
  const workspace = useWorkspace();
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
  const composer = useComposer();
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
  optionMeta: { color: colors.muted, fontFamily: typography.regular, fontSize: 11, marginTop: 2 },
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
