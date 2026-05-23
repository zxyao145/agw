import React from "react";
import { Pressable, Text, View } from "react-native";
import type {
  AgwProject,
  AgwTarget,
  AgwTaskSummary,
} from "../../../api/agw-api-types";
import { Icon, IconButton } from "./icons";
import { styles } from "./styles";
import { colors } from "./tokens";

export function HistoryDrawer({
  currentTaskId,
  historyError,
  isLoadingHistory,
  onClose,
  onOpenSettings,
  onProjectSelect,
  onTaskSelect,
  onTargetSelect,
  projects,
  safeBottom,
  safeTop,
  selectedProjectId,
  selectedTargetValue,
  targets,
  tasks,
}: {
  currentTaskId?: string | null;
  historyError?: string | null;
  isLoadingHistory?: boolean;
  onClose: () => void;
  onOpenSettings: () => void;
  onProjectSelect: (projectId: string) => void;
  onTaskSelect: (taskId: string) => void;
  onTargetSelect: (targetValue: string) => void;
  projects: AgwProject[];
  safeBottom: number;
  safeTop: number;
  selectedProjectId?: string | null;
  selectedTargetValue?: string | null;
  targets: AgwTarget[];
  tasks: AgwTaskSummary[];
}): React.JSX.Element {
  const [expandedSelector, setExpandedSelector] = React.useState<
    "project" | "target" | null
  >(null);
  const selectedProject =
    projects.find((project) => project.id === selectedProjectId) ?? null;
  const selectedTarget =
    targets.find((target) => getTargetValue(target) === selectedTargetValue) ??
    null;

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
          <Text style={styles.drawerBrand}>Agw</Text>
          <IconButton
            color={colors.primary}
            icon="close"
            label="Close chat history"
            onPress={onClose}
            size={40}
            testID="agw-close-drawer"
          />
        </View>

        <View style={styles.drawerSelectors}>
          <DrawerSelect
            expanded={expandedSelector === "project"}
            label="PROJECT"
            onPress={() => toggleSelector("project")}
            testID="agw-project-selector"
            value={selectedProject?.name ?? "No project"}
          />
          <DrawerSelect
            expanded={expandedSelector === "target"}
            label="AGENT"
            onPress={() => toggleSelector("target")}
            testID="agw-agent-selector"
            value={selectedTarget?.label ?? "No agent"}
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
          ) : tasks.length === 0 ? (
            <Text style={styles.emptyPanelText}>No chat history yet</Text>
          ) : (
            tasks.map((task) => (
              <HistoryItem
                active={task.id === currentTaskId}
                key={task.id}
                onPress={() => onTaskSelect(task.id)}
                preview={formatTaskPreview(task)}
                title={task.title}
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
            <Icon color={colors.muted} name="settings" size={22} />
            <Text style={styles.settingsText}>Settings</Text>
          </Pressable>
        </View>
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

function formatTaskPreview(task: AgwTaskSummary): string {
  const timestamp = task.updateTime ?? task.createTime;
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
