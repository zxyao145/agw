import { router } from "expo-router";
import { ChevronLeft, Download, ShieldAlert } from "lucide-react-native";
import React from "react";
import {
  Alert,
  KeyboardAvoidingView,
  Platform,
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
} from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";

import { IconButton } from "@/components/icon-button";
import { ScreenFrame } from "@/components/screen-frame";
import { parseEncodedConfig } from "./config-codec";
import { useSession } from "./session-provider";
import { useWorkspace } from "@/features/workspace/workspace-provider";
import { getErrorMessage } from "@/lib/errors";
import { colors, layout, radius, typography } from "@/theme/tokens";

export function ProfileFormScreen({ profileId }: { profileId?: string }): React.JSX.Element {
  const insets = useSafeAreaInsets();
  const session = useSession();
  const workspace = useWorkspace();
  const profile = profileId ? session.state.profiles.find((item) => item.id === profileId) : null;
  const [name, setName] = React.useState(profile?.name ?? "");
  const [serverUrl, setServerUrl] = React.useState(profile?.serverUrl ?? "");
  const [token, setToken] = React.useState("");
  const [encodedConfig, setEncodedConfig] = React.useState("");
  const [allowInsecureHttp, setAllowInsecureHttp] = React.useState(
    profile?.allowInsecureHttp ?? false,
  );
  const [error, setError] = React.useState<string | null>(null);

  const importConfig = () => {
    try {
      const config = parseEncodedConfig(encodedConfig);
      setServerUrl(config.serverUrl);
      setToken(config.token);
      if (!name.trim()) setName(new URL(config.serverUrl).host);
      setAllowInsecureHttp(false);
      setError(null);
    } catch (caught) {
      setError(getErrorMessage(caught));
    }
  };

  const persist = async (confirmedHttp: boolean) => {
    try {
      setError(null);
      await session.saveProfile({
        id: profile?.id,
        name,
        serverUrl,
        token,
        allowInsecureHttp: confirmedHttp || allowInsecureHttp,
      });
      router.replace("/settings");
    } catch (caught) {
      setError(getErrorMessage(caught));
    }
  };

  const save = () => {
    if (workspace.isExecuting && session.activeProfile?.id === profile?.id) {
      Alert.alert(
        "Execution in progress",
        "Stop the current execution before editing this server.",
      );
      return;
    }
    if (serverUrl.trim().toLowerCase().startsWith("http://") && !allowInsecureHttp) {
      Alert.alert(
        "Unencrypted HTTP connection",
        "Traffic, including the API token, can be observed or changed on the network. Continue only if you trust this connection.",
        [
          { text: "Cancel", style: "cancel" },
          { text: "Continue", style: "destructive", onPress: () => void persist(true) },
        ],
      );
      return;
    }
    void persist(allowInsecureHttp);
  };

  return (
    <ScreenFrame>
      <KeyboardAvoidingView
        style={styles.flex}
        behavior={Platform.OS === "ios" ? "padding" : undefined}
      >
        <View
          style={[
            styles.header,
            { paddingTop: insets.top, height: layout.headerHeight + insets.top },
          ]}
        >
          <IconButton
            icon={ChevronLeft}
            label="Back to settings"
            color={colors.primary}
            onPress={() => router.back()}
          />
          <Text style={styles.headerTitle}>{profile ? "Edit Server" : "Add Server"}</Text>
          <Pressable
            disabled={session.isMutating}
            onPress={save}
            style={({ pressed }) => [
              styles.saveButton,
              pressed && styles.pressed,
              session.isMutating && styles.disabled,
            ]}
          >
            <Text style={styles.saveText}>{session.isMutating ? "Checking…" : "Save"}</Text>
          </Pressable>
        </View>
        <ScrollView
          keyboardShouldPersistTaps="handled"
          contentContainerStyle={[styles.content, { paddingBottom: insets.bottom + 30 }]}
        >
          {!profile ? (
            <View style={styles.importCard}>
              <View style={styles.importTitleRow}>
                <Download color={colors.primary} size={18} />
                <Text style={styles.importTitle}>Import Web configuration</Text>
              </View>
              <Text style={styles.helper}>
                Paste the Base64 configuration copied immediately after creating an API token in Agw
                Web.
              </Text>
              <TextInput
                multiline
                value={encodedConfig}
                onChangeText={setEncodedConfig}
                placeholder="Base64URL configuration"
                placeholderTextColor={colors.subtle}
                autoCapitalize="none"
                autoCorrect={false}
                style={[styles.input, styles.encodedInput]}
              />
              <Pressable
                onPress={importConfig}
                style={({ pressed }) => [styles.secondaryButton, pressed && styles.pressed]}
              >
                <Text style={styles.secondaryButtonText}>Import</Text>
              </Pressable>
            </View>
          ) : null}

          <Field label="PROFILE NAME" value={name} onChangeText={setName} placeholder="Local" />
          <Field
            label="SERVER URL"
            value={serverUrl}
            onChangeText={(value) => {
              setServerUrl(value);
              if (!value.toLowerCase().startsWith("http://")) setAllowInsecureHttp(false);
            }}
            placeholder="https://agw.example.com"
            keyboardType="url"
          />
          <Field
            label={profile ? "API TOKEN (LEAVE BLANK TO KEEP CURRENT)" : "API TOKEN"}
            value={token}
            onChangeText={setToken}
            placeholder="agw_…"
            secureTextEntry
            autoCapitalize="none"
          />

          {serverUrl.trim().toLowerCase().startsWith("http://") ? (
            <View style={styles.httpWarning}>
              <ShieldAlert color={colors.warning} size={20} />
              <Text style={styles.httpText}>
                HTTP is not encrypted. Saving this profile requires an explicit confirmation.
              </Text>
            </View>
          ) : null}
          {error ? <Text style={styles.error}>{error}</Text> : null}
        </ScrollView>
      </KeyboardAvoidingView>
    </ScreenFrame>
  );
}

function Field({
  label,
  ...props
}: React.ComponentProps<typeof TextInput> & { label: string }): React.JSX.Element {
  return (
    <View style={styles.field}>
      <Text style={styles.label}>{label}</Text>
      <TextInput
        {...props}
        placeholderTextColor={colors.subtle}
        autoCorrect={false}
        style={styles.input}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  header: {
    paddingHorizontal: 8,
    flexDirection: "row",
    alignItems: "center",
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderColor: colors.border,
  },
  headerTitle: {
    flex: 1,
    color: colors.ink,
    fontFamily: typography.semibold,
    fontSize: 17,
    textAlign: "center",
  },
  saveButton: {
    minWidth: 64,
    minHeight: 40,
    borderRadius: radius.pill,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: colors.primary,
  },
  saveText: { color: colors.white, fontFamily: typography.semibold, fontSize: 13 },
  content: { padding: 16, gap: 18 },
  importCard: { padding: 16, borderRadius: radius.lg, backgroundColor: colors.surface, gap: 10 },
  importTitleRow: { flexDirection: "row", alignItems: "center", gap: 8 },
  importTitle: { color: colors.ink, fontFamily: typography.semibold, fontSize: 15 },
  helper: { color: colors.muted, fontFamily: typography.regular, fontSize: 12, lineHeight: 18 },
  encodedInput: { minHeight: 82, textAlignVertical: "top" },
  field: { gap: 7 },
  label: { color: colors.muted, fontFamily: typography.semibold, fontSize: 10, letterSpacing: 1 },
  input: {
    minHeight: 48,
    paddingHorizontal: 13,
    paddingVertical: 11,
    borderWidth: 1,
    borderColor: colors.border,
    borderRadius: radius.md,
    backgroundColor: colors.white,
    color: colors.ink,
    fontFamily: typography.regular,
    fontSize: 14,
  },
  secondaryButton: {
    alignSelf: "flex-start",
    minHeight: 38,
    paddingHorizontal: 18,
    borderWidth: 1,
    borderColor: colors.primary,
    borderRadius: radius.pill,
    alignItems: "center",
    justifyContent: "center",
  },
  secondaryButtonText: { color: colors.primary, fontFamily: typography.semibold, fontSize: 13 },
  httpWarning: {
    flexDirection: "row",
    gap: 10,
    padding: 12,
    borderRadius: radius.md,
    backgroundColor: colors.warningSoft,
  },
  httpText: {
    flex: 1,
    color: colors.warning,
    fontFamily: typography.regular,
    fontSize: 12,
    lineHeight: 18,
  },
  error: { color: colors.danger, fontFamily: typography.regular, fontSize: 13, lineHeight: 19 },
  pressed: { opacity: 0.72 },
  disabled: { opacity: 0.5 },
});
