import React from "react";
import { Pressable, Text, TextInput, View } from "react-native";
import type { AgwLocalConfig } from "../../../config/agw-config";
import { AgwConfigError, createLocalConfig } from "../../../config/agw-config";
import { IconButton } from "./icons";
import { styles } from "./styles";
import { colors } from "./tokens";

export function ConfigSettingsSheet({
  config,
  onClose,
  onSave,
  safeBottom,
  safeTop,
}: {
  config: AgwLocalConfig;
  onClose: () => void;
  onSave: (config: AgwLocalConfig) => Promise<void>;
  safeBottom: number;
  safeTop: number;
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
      accessibilityViewIsModal
      style={[styles.configOverlay, { paddingTop: safeTop + 18 }]}
      testID="agw-settings-sheet"
    >
      <View style={styles.configSheet}>
        <View style={styles.settingsHeader}>
          <View style={styles.settingsTitleColumn}>
            <Text style={styles.configEyebrow}>SETTINGS</Text>
            <Text style={styles.configTitle}>Local Configuration</Text>
          </View>
          <IconButton
            color={colors.primary}
            icon="close"
            label="Close settings"
            onPress={onClose}
            size={40}
            testID="agw-settings-close"
          />
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
            { paddingBottom: Math.max(0, safeBottom - 4) },
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
    </View>
  );
}
