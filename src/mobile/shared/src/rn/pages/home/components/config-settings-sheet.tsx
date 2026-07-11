import React from "react";
import { Pressable, Text, TextInput, View } from "react-native";
import type { AgwLocalConfig } from "../../../config/agw-config";
import { AgwConfigError, createLocalConfig } from "../../../config/agw-config";
import { styles } from "./styles";

export function ConfigSettingsPage({
  config,
  onClose,
  onSave,
  safeBottom,
}: {
  config: AgwLocalConfig;
  onClose: () => void;
  onSave: (config: AgwLocalConfig) => Promise<void>;
  safeBottom: number;
}): React.JSX.Element {
  const [serverUrl, setServerUrl] = React.useState(config.serverUrl);
  const [token, setToken] = React.useState(config.token);
  const [error, setError] = React.useState<string | null>(null);
  const [isSaving, setIsSaving] = React.useState(false);

  React.useEffect(() => {
    setServerUrl(config.serverUrl);
    setToken(config.token);
    setError(null);
  }, [config]);

  async function handleSave() {
    if (isSaving) {
      return;
    }

    setIsSaving(true);
    setError(null);

    try {
      await onSave(createLocalConfig({ token, serverUrl }));
      onClose();
    } catch (saveError) {
      const message =
        saveError instanceof AgwConfigError || saveError instanceof Error
          ? saveError.message
          : "Configuration could not be saved.";

      setError(message);
    } finally {
      setIsSaving(false);
    }
  }

  return (
    <View style={styles.settingsPage} testID="agw-settings-page">
      <View style={styles.settingsHeader}>
        <View style={styles.settingsTitleColumn}>
          <Text style={styles.configEyebrow}>SETTINGS</Text>
          <Text style={styles.configTitle}>Local Configuration</Text>
        </View>
      </View>

      <View style={styles.settingsFields}>
        <View style={styles.settingsField}>
          <Text style={styles.settingsFieldLabel}>Server Domain</Text>
          <TextInput
            autoCapitalize="none"
            autoCorrect={false}
            onChangeText={setServerUrl}
            placeholder="https://api.example.com"
            placeholderTextColor="#7b8190"
            style={styles.settingsTextInput}
            testID="agw-settings-domain-input"
            value={serverUrl}
          />
        </View>
        <View style={styles.settingsField}>
          <Text style={styles.settingsFieldLabel}>API Key</Text>
          <TextInput
            autoCapitalize="none"
            autoCorrect={false}
            onChangeText={setToken}
            placeholder="agw_…"
            placeholderTextColor="#7b8190"
            style={styles.settingsTextInput}
            testID="agw-settings-token-input"
            value={token}
          />
        </View>
      </View>

      {error ? (
        <Text accessibilityLiveRegion="polite" style={styles.configError}>
          {error}
        </Text>
      ) : null}

      <View
        style={[
          styles.configActionRow,
          styles.settingsActionRow,
          { paddingBottom: Math.max(24, safeBottom + 16) },
        ]}
      >
        <Pressable
          accessibilityRole="button"
          onPress={onClose}
          style={({ pressed }) => [styles.configSecondaryButton, pressed && styles.pressed]}
          testID="agw-settings-cancel"
        >
          <Text style={styles.configSecondaryButtonText}>Cancel</Text>
        </Pressable>
        <Pressable
          accessibilityRole="button"
          onPress={handleSave}
          style={({ pressed }) => [styles.configPrimaryButton, pressed && styles.pressed]}
          testID="agw-settings-save"
        >
          <Text style={styles.configPrimaryButtonText}>{isSaving ? "Saving" : "Save"}</Text>
        </Pressable>
      </View>
    </View>
  );
}
