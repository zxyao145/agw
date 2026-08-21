import { router } from "expo-router";
import {
  Check,
  ChevronDown,
  Pencil,
  Plus,
  RefreshCw,
  Server,
  Trash2,
  X,
} from "lucide-react-native";
import React from "react";
import { Alert, Pressable, ScrollView, StyleSheet, Text, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";

import { AgwBrand } from "@/components/agw-brand";
import { IconButton } from "@/components/icon-button";
import { ScreenFrame } from "@/components/screen-frame";
import { useSession } from "@/features/servers/session-provider";
import type { ServerProfile } from "@/features/servers/types";
import { useWorkspace } from "@/features/workspace/workspace-provider";
import { getErrorMessage } from "@/lib/errors";
import { colors, layout, radius, typography } from "@/theme/tokens";

export default function SettingsScreen(): React.JSX.Element {
  const insets = useSafeAreaInsets();
  const session = useSession();
  const workspace = useWorkspace();
  const canClose = session.status === "authenticated";

  const activate = (profile: ServerProfile) => {
    if (workspace.isExecuting) {
      Alert.alert("Execution in progress", "Stop the current execution before switching servers.");
      return;
    }
    const run = async () => {
      try {
        if (profile.serverUrl.startsWith("http://") && !profile.allowInsecureHttp) {
          await session.confirmInsecureHttp(profile.id);
        } else {
          await session.activateProfile(profile.id);
        }
        router.replace("/chat");
      } catch (error) {
        Alert.alert("Unable to connect", getErrorMessage(error));
      }
    };
    if (profile.serverUrl.startsWith("http://") && !profile.allowInsecureHttp) {
      Alert.alert(
        "Unencrypted HTTP connection",
        "Traffic, including the API token, can be observed or changed on the network. Continue only if you trust this connection.",
        [
          { text: "Cancel", style: "cancel" },
          { text: "Continue", style: "destructive", onPress: () => void run() },
        ],
      );
    } else {
      void run();
    }
  };

  const remove = (profile: ServerProfile) => {
    if (workspace.isExecuting && session.activeProfile?.id === profile.id) {
      Alert.alert(
        "Execution in progress",
        "Stop the current execution before deleting this server.",
      );
      return;
    }
    Alert.alert(
      `Delete ${profile.name}?`,
      "This removes the profile and token from this device. It does not revoke the token on the Agw Server.",
      [
        { text: "Cancel", style: "cancel" },
        {
          text: "Delete",
          style: "destructive",
          onPress: () => void session.deleteProfile(profile.id),
        },
      ],
    );
  };

  return (
    <ScreenFrame>
      <View
        style={[
          styles.header,
          { paddingTop: insets.top, height: layout.headerHeight + insets.top },
        ]}
      >
        <AgwBrand />
        {canClose ? (
          <IconButton
            icon={X}
            label="Close settings"
            color={colors.primary}
            onPress={() => router.replace("/history")}
          />
        ) : null}
      </View>
      <ScrollView contentContainerStyle={[styles.content, { paddingBottom: insets.bottom + 24 }]}>
        {session.status === "authenticated" && workspace.selectedProject ? (
          <Pressable style={styles.projectSelector} onPress={() => router.push("/history")}>
            <View>
              <Text style={styles.eyebrow}>PROJECT</Text>
              <Text style={styles.projectName}>{workspace.selectedProject.name}</Text>
            </View>
            <ChevronDown color={colors.muted} size={18} />
          </Pressable>
        ) : null}

        {session.error ? (
          <View style={styles.errorCard}>
            <Text style={styles.errorText}>{session.error}</Text>
            {session.state.activeProfileId ? (
              <Pressable
                style={styles.retryButton}
                onPress={() => void session.retryActiveProfile()}
              >
                <RefreshCw color={colors.primary} size={16} />
                <Text style={styles.retryText}>Retry</Text>
              </Pressable>
            ) : null}
          </View>
        ) : null}

        <View style={styles.sectionHeader}>
          <Text style={styles.sectionTitle}>Servers</Text>
          <IconButton
            icon={Plus}
            label="Add server"
            color={colors.primary}
            onPress={() => router.push("/settings/server/new")}
          />
        </View>
        {session.migratedProfileId ? (
          <View style={styles.warningCard}>
            <Text style={styles.warningText}>
              A previous Mobile configuration was migrated. Confirm its HTTP warning before
              connecting.
            </Text>
          </View>
        ) : null}
        <View style={styles.serverList}>
          {session.state.profiles.length === 0 ? (
            <View style={styles.emptyState}>
              <Server color={colors.muted} size={24} />
              <Text style={styles.emptyTitle}>No servers configured</Text>
              <Text style={styles.emptyText}>
                Add a Server URL and the API token created in Agw Web settings.
              </Text>
            </View>
          ) : (
            session.state.profiles.map((profile) => {
              const active =
                session.activeProfile?.id === profile.id && session.status === "authenticated";
              return (
                <Pressable
                  accessibilityRole="button"
                  key={profile.id}
                  onPress={() => activate(profile)}
                  style={({ pressed }) => [
                    styles.serverRow,
                    active && styles.serverRowActive,
                    pressed && styles.pressed,
                  ]}
                >
                  <View style={[styles.serverIcon, active && styles.serverIconActive]}>
                    {active ? (
                      <Check color={colors.primary} size={18} />
                    ) : (
                      <Server color={colors.muted} size={18} />
                    )}
                  </View>
                  <View style={styles.serverInfo}>
                    <View style={styles.serverTitleRow}>
                      <Text numberOfLines={1} style={styles.serverName}>
                        {profile.name}
                      </Text>
                      {active ? <Text style={styles.activeBadge}>Active</Text> : null}
                    </View>
                    <Text numberOfLines={1} style={styles.serverUrl}>
                      {profile.serverUrl}
                    </Text>
                  </View>
                  <IconButton
                    icon={Pencil}
                    label={`Edit ${profile.name}`}
                    size={18}
                    disabled={workspace.isExecuting && active}
                    onPress={(event) => {
                      event.stopPropagation();
                      router.push({
                        pathname: "/settings/server/[profileId]",
                        params: { profileId: profile.id },
                      });
                    }}
                  />
                  <IconButton
                    icon={Trash2}
                    label={`Delete ${profile.name}`}
                    color={colors.danger}
                    size={18}
                    disabled={session.isMutating || (workspace.isExecuting && active)}
                    onPress={(event) => {
                      event.stopPropagation();
                      remove(profile);
                    }}
                  />
                </Pressable>
              );
            })
          )}
        </View>

        <View style={styles.aboutSection}>
          <Text style={styles.sectionTitle}>About</Text>
          <View style={styles.aboutRow}>
            <AgwBrand />
            <View style={styles.aboutCopy}>
              <Text style={styles.aboutName}>Agw Mobile</Text>
              <Text style={styles.aboutVersion}>Version 0.1.0</Text>
            </View>
          </View>
        </View>
      </ScrollView>
    </ScreenFrame>
  );
}

const styles = StyleSheet.create({
  header: {
    paddingHorizontal: 16,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderColor: colors.border,
  },
  content: { paddingHorizontal: 16, paddingTop: 12 },
  projectSelector: {
    minHeight: 58,
    paddingHorizontal: 16,
    paddingVertical: 10,
    borderRadius: radius.md,
    backgroundColor: colors.surface,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    marginBottom: 22,
  },
  eyebrow: {
    color: colors.muted,
    fontFamily: typography.semibold,
    fontSize: 10,
    letterSpacing: 1.2,
  },
  projectName: { color: colors.ink, fontFamily: typography.medium, fontSize: 15, marginTop: 3 },
  sectionHeader: {
    height: 48,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
  },
  sectionTitle: { color: colors.ink, fontFamily: typography.semibold, fontSize: 18 },
  serverList: {
    borderWidth: 1,
    borderColor: colors.border,
    borderRadius: radius.lg,
    overflow: "hidden",
    backgroundColor: colors.white,
  },
  serverRow: {
    minHeight: 72,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    paddingLeft: 12,
    paddingRight: 4,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderColor: colors.border,
  },
  serverRowActive: { backgroundColor: colors.primarySoft },
  pressed: { opacity: 0.72 },
  serverIcon: {
    width: 34,
    height: 34,
    borderRadius: 17,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: colors.surface,
  },
  serverIconActive: { backgroundColor: colors.white },
  serverInfo: { flex: 1, minWidth: 0 },
  serverTitleRow: { flexDirection: "row", alignItems: "center", gap: 8 },
  serverName: { flexShrink: 1, color: colors.ink, fontFamily: typography.semibold, fontSize: 15 },
  serverUrl: { color: colors.muted, fontFamily: typography.regular, fontSize: 12, marginTop: 4 },
  activeBadge: {
    color: colors.primary,
    fontFamily: typography.semibold,
    fontSize: 10,
    paddingHorizontal: 7,
    paddingVertical: 3,
    borderRadius: radius.pill,
    backgroundColor: colors.white,
  },
  emptyState: { alignItems: "center", padding: 28, gap: 8 },
  emptyTitle: { color: colors.ink, fontFamily: typography.semibold, fontSize: 15 },
  emptyText: {
    color: colors.muted,
    fontFamily: typography.regular,
    fontSize: 13,
    textAlign: "center",
    lineHeight: 19,
  },
  errorCard: {
    padding: 12,
    borderRadius: radius.md,
    backgroundColor: colors.dangerSoft,
    marginBottom: 12,
    gap: 8,
  },
  errorText: { color: colors.danger, fontFamily: typography.regular, fontSize: 13, lineHeight: 18 },
  retryButton: { flexDirection: "row", alignSelf: "flex-start", alignItems: "center", gap: 6 },
  retryText: { color: colors.primary, fontFamily: typography.semibold, fontSize: 13 },
  warningCard: {
    padding: 12,
    backgroundColor: colors.warningSoft,
    borderRadius: radius.md,
    marginBottom: 10,
  },
  warningText: {
    color: colors.warning,
    fontFamily: typography.regular,
    fontSize: 12,
    lineHeight: 17,
  },
  aboutSection: { marginTop: 30, gap: 14 },
  aboutRow: {
    flexDirection: "row",
    alignItems: "center",
    padding: 16,
    borderRadius: radius.lg,
    backgroundColor: colors.surface,
  },
  aboutCopy: { marginLeft: "auto", alignItems: "flex-end" },
  aboutName: { color: colors.ink, fontFamily: typography.medium, fontSize: 14 },
  aboutVersion: { color: colors.muted, fontFamily: typography.regular, fontSize: 12, marginTop: 3 },
});
