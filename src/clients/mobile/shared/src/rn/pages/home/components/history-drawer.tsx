import React from "react";
import { Pressable, Text, View } from "react-native";
import { Bolt } from "lucide-react-native";
import type {
  AgwContextSummary,
  AgwProject,
  AgwTarget,
} from "../../../api/agw-api-types";
import type { AgwLocalConfig } from "../../../config/agw-config";
import { AgwLogo } from "./agw-logo";
import { ConfigSettingsPage } from "./config-settings-sheet";
import {
  DEFAULT_AGENT_LABEL,
  DEFAULT_PROJECT_VALUE,
} from "../lib/default-selections";
import { Icon, IconButton } from "./icons";
import { styles } from "./styles";
import { colors } from "./tokens";

export function HistoryDrawer({
  contexts,
  currentContextId,
  historyError,
  isSettingsOpen,
  isLoadingHistory,
  onClose,
  onCloseSettings,
  onOpenSettings,
  onProjectSelect,
  onSaveSettings,
  onContextSelect,
  onTargetSelect,
  projects,
  safeBottom,
  safeTop,
  selectedProjectId,
  selectedTargetValue,
  settingsConfig,
  targets,
}: {
  contexts: AgwContextSummary[];
  currentContextId?: string | null;
  historyError?: string | null;
  isLoadingHistory?: boolean;
  isSettingsOpen?: boolean;
  onClose: () => void;
  onCloseSettings?: () => void;
  onOpenSettings: () => void;
  onProjectSelect: (projectId: string) => void;
  onSaveSettings?: (config: AgwLocalConfig) => Promise<void>;
  onContextSelect: (contextId: string) => void;
  onTargetSelect: (targetValue: string) => void;
  projects: AgwProject[];
  safeBottom: number;
  safeTop: number;
  selectedProjectId?: string | null;
  selectedTargetValue?: string | null;
  settingsConfig?: AgwLocalConfig | null;
  targets: AgwTarget[];
}): React.JSX.Element {
  const [expandedSelector, setExpandedSelector] = React.useState<
    "project" | "target" | null
  >(null);
  const selectedProject =
    projects.find((project) => project.id === selectedProjectId) ?? null;
  const selectedTarget =
    targets.find((target) => getTargetValue(target) === selectedTargetValue) ??
    null;
  const settingsPage =
    isSettingsOpen && settingsConfig && onCloseSettings && onSaveSettings
      ? {
          config: settingsConfig,
          onClose: onCloseSettings,
          onSave: onSaveSettings,
        }
      : null;

  function toggleSelector(selector: "project" | "target") {
    setExpandedSelector((current) => (current === selector ? null : selector));
  }

  return (
    <View style={styles.drawerLayer}>
      <View style={styles.drawerPanel}>
        <View
          style={[
            styles.drawerHeader,
            { height: 64 + safeTop, paddingTop: safeTop },
          ]}
        >
          {settingsPage ? (
            <View style={styles.drawerTitleRow}>
              <IconButton
                color={colors.primary}
                icon="chevronLeft"
                label="Back to chat history"
                onPress={settingsPage.onClose}
                size={40}
                testID="agw-settings-back"
              />
              <Text style={styles.drawerTitle}>Settings</Text>
            </View>
          ) : (
            <AgwLogo labelStyle={styles.drawerBrand} />
          )}
          <IconButton
            color={colors.primary}
            icon="close"
            label="Close chat history"
            onPress={onClose}
            size={40}
            testID="agw-close-drawer"
          />
        </View>

        {settingsPage ? (
          <ConfigSettingsPage
            config={settingsPage.config}
            onClose={settingsPage.onClose}
            onSave={settingsPage.onSave}
            safeBottom={safeBottom}
          />
        ) : (
          <>
            <View style={styles.drawerSelectors}>
              <DrawerSelect
                expanded={expandedSelector === "project"}
                label="PROJECT"
                onPress={() => toggleSelector("project")}
                testID="agw-project-selector"
                value={selectedProject?.name ?? DEFAULT_PROJECT_VALUE}
              />
              <DrawerSelect
                expanded={expandedSelector === "target"}
                label="AGENT"
                onPress={() => toggleSelector("target")}
                testID="agw-agent-selector"
                value={selectedTarget?.label ?? DEFAULT_AGENT_LABEL}
              />
            </View>

            {expandedSelector === "project" ? (
              <View style={styles.drawerOptions}>
                {projects.map((project) => (
                  <SelectorOption
                    active={project.id === selectedProjectId}
                    key={project.id}
                    label={project.name}
                    onPress={() => {
                      onProjectSelect(project.id);
                      setExpandedSelector(null);
                    }}
                    testID={`agw-project-option-${project.id}`}
                  />
                ))}
              </View>
            ) : null}

            {expandedSelector === "target" ? (
              <View style={styles.drawerOptions}>
                {targets.map((target) => {
                  const targetValue = getTargetValue(target);

                  return (
                    <SelectorOption
                      active={targetValue === selectedTargetValue}
                      key={targetValue}
                      label={target.label}
                      meta={target.type === "agent" ? "Agent" : "Agentflow"}
                      onPress={() => {
                        onTargetSelect(targetValue);
                        setExpandedSelector(null);
                      }}
                      testID={`agw-agent-option-${targetValue}`}
                    />
                  );
                })}
              </View>
            ) : null}

            <View style={styles.historySection}>
              <Text style={styles.sectionLabel}>RECENT HISTORY</Text>
              {isLoadingHistory ? (
                <Text style={styles.emptyPanelText}>Loading history</Text>
              ) : historyError ? (
                <Text style={styles.errorText}>{historyError}</Text>
              ) : contexts.length === 0 ? (
                <Text style={styles.emptyPanelText}>No chat history yet</Text>
              ) : (
                contexts.map((context) => (
                  <HistoryItem
                    active={context.contextId === currentContextId}
                    key={context.contextId}
                    onPress={() => onContextSelect(context.contextId)}
                    preview={formatContextPreview(context)}
                    title={context.title}
                  />
                ))
              )}
            </View>

            <View
              style={[
                styles.drawerFooter,
                { paddingBottom: Math.max(8, safeBottom) },
              ]}
            >
              <Pressable
                accessibilityRole="button"
                onPress={onOpenSettings}
                style={({ pressed }) => [
                  styles.settingsRow,
                  pressed && styles.historyItemPressed,
                ]}
                testID="agw-open-settings"
              >
                <Bolt color={colors.muted} size={22} />
                <Text style={styles.settingsText}>Settings</Text>
              </Pressable>
            </View>
          </>
        )}
      </View>
    </View>
  );
}

function DrawerSelect({
  expanded,
  label,
  onPress,
  testID,
  value,
}: {
  expanded?: boolean;
  label: string;
  onPress: () => void;
  testID: string;
  value: string;
}): React.JSX.Element {
  return (
    <View style={styles.drawerSelectColumn}>
      <Text style={styles.selectLabel}>{label}</Text>
      <Pressable
        accessibilityRole="button"
        onPress={onPress}
        style={({ pressed }) => [
          styles.selectBox,
          expanded && styles.selectBoxActive,
          pressed && styles.historyItemPressed,
        ]}
        testID={testID}
      >
        <Text numberOfLines={1} style={styles.selectValue}>
          {value}
        </Text>
        <Icon color={colors.muted} name="chevronDown" size={14} />
      </Pressable>
    </View>
  );
}

function SelectorOption({
  active,
  label,
  meta,
  onPress,
  testID,
}: {
  active?: boolean;
  label: string;
  meta?: string;
  onPress: () => void;
  testID: string;
}): React.JSX.Element {
  return (
    <Pressable
      accessibilityRole="button"
      onPress={onPress}
      style={({ pressed }) => [
        styles.selectorOption,
        active && styles.historyItemActive,
        pressed && styles.historyItemPressed,
      ]}
      testID={testID}
    >
      <Text numberOfLines={1} style={styles.selectorOptionText}>
        {label}
      </Text>
      {meta ? (
        <Text numberOfLines={1} style={styles.selectorOptionMeta}>
          {meta}
        </Text>
      ) : null}
    </Pressable>
  );
}

function HistoryItem({
  active,
  onPress,
  preview,
  title,
}: {
  active?: boolean;
  onPress: () => void;
  preview: string;
  title: string;
}): React.JSX.Element {
  return (
    <Pressable
      accessibilityRole="button"
      onPress={onPress}
      style={({ pressed }) => [
        styles.historyItem,
        active && styles.historyItemActive,
        pressed && styles.historyItemPressed,
      ]}
    >
      <Text numberOfLines={1} style={styles.historyTitle}>
        {title}
      </Text>
      <Text numberOfLines={1} style={styles.historyPreview}>
        {preview}
      </Text>
    </Pressable>
  );
}

function formatContextPreview(context: AgwContextSummary): string {
  const timestamp = context.updateTime ?? context.createTime;
  const date = new Date(timestamp);

  if (Number.isNaN(date.getTime())) {
    return timestamp;
  }

  return date.toLocaleString(undefined, {
    day: "numeric",
    hour: "numeric",
    minute: "2-digit",
    month: "short",
  });
}

function getTargetValue(target: AgwTarget): string {
  return `${target.type}:${target.id}`;
}
