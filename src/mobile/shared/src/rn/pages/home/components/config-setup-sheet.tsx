import React from "react";
import { Pressable, Text, TextInput, View } from "react-native";
import type { AgwLocalConfig } from "../../../config/agw-config";
import { AgwConfigError, parseEncodedConfig } from "../../../config/agw-config";
import { styles } from "./styles";

export function ConfigSetupSheet({
  initialError,
  onSave,
  safeBottom,
  safeTop,
}: {
  initialError?: string | null;
  onSave: (config: AgwLocalConfig) => Promise<void>;
  safeBottom: number;
  safeTop: number;
}): React.JSX.Element {
  const [encodedConfig, setEncodedConfig] = React.useState("");
  const [error, setError] = React.useState<string | null>(
    initialError ?? null
  );
  const [isSaving, setIsSaving] = React.useState(false);

  React.useEffect(() => {
    setError(initialError ?? null);
  }, [initialError]);

  async function handleImport() {
    if (isSaving) {
      return;
    }

    setIsSaving(true);
    setError(null);

    try {
      await onSave(parseEncodedConfig(encodedConfig));
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
      testID="agw-config-setup-sheet"
    >
      <View style={styles.configSheet}>
        <Text style={styles.configEyebrow}>FIRST RUN</Text>
        <Text style={styles.configTitle}>Server Configuration</Text>
        <Text style={styles.configDescription}>
          Paste the Base64URL configuration payload to continue.
        </Text>
        <TextInput
          autoCapitalize="none"
          autoCorrect={false}
          multiline
          onChangeText={setEncodedConfig}
          placeholder="Base64URL payload"
          placeholderTextColor="#7b8190"
          style={styles.configTextArea}
          testID="agw-config-import-input"
          textAlignVertical="top"
          value={encodedConfig}
        />
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
            onPress={handleImport}
            style={({ pressed }) => [
              styles.configPrimaryButton,
              pressed && styles.pressed,
            ]}
            testID="agw-config-import-save"
          >
            <Text style={styles.configPrimaryButtonText}>
              {isSaving ? "Saving" : "Import"}
            </Text>
          </Pressable>
        </View>
      </View>
    </View>
  );
}
