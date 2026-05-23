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
  const [serverDomain, setServerDomain] = React.useState(config.serverDomain);
  const [apiKey, setApiKey] = React.useState(config.apiKey);
  const [error, setError] = React.useState<string | null>(null);
  const [isSaving, setIsSaving] = React.useState(false);

  React.useEffect(() => {
    setServerDomain(config.serverDomain);
    setApiKey(config.apiKey);
    setError(null);
  }, [config]);

  async function handleSave() {
    if (isSaving) {
      return;
    }

    setIsSaving(true);
    setError(null);

    try {
      await onSave(createLocalConfig({ apiKey, serverDomain }));
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
    <View
      style={styles.settingsPage}
      testID="agw-settings-page"
    >
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
            onChangeText={setServerDomain}
            placeholder="https://api.example.com"
            placeholderTextColor="#7b8190"
            style={styles.settingsTextInput}
            testID="agw-settings-domain-input"
            value={serverDomain}
          />
        </View>
        <View style={styles.settingsField}>
          <Text style={styles.settingsFieldLabel}>API Key</Text>
          <TextInput
            autoCapitalize="none"
            autoCorrect={false}
            onChangeText={setApiKey}
            placeholder="agw_api_key"
            placeholderTextColor="#7b8190"
            style={styles.settingsTextInput}
            testID="agw-settings-api-key-input"
            value={apiKey}
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
          style={({ pressed }) => [
            styles.configSecondaryButton,
            pressed && styles.pressed,
          ]}
          testID="agw-settings-cancel"
        >
          <Text style={styles.configSecondaryButtonText}>Cancel</Text>
        </Pressable>
        <Pressable
          accessibilityRole="button"
          onPress={handleSave}
          style={({ pressed }) => [
            styles.configPrimaryButton,
            pressed && styles.pressed,
          ]}
          testID="agw-settings-save"
        >
          <Text style={styles.configPrimaryButtonText}>
            {isSaving ? "Saving" : "Save"}
          </Text>
        </Pressable>
      </View>
    </View>
  );
}
