import * as SecureStore from "expo-secure-store";
import type { AgwLocalConfig } from "./agw-config";
import { parseConfigFileContent, serializeConfig } from "./agw-config";

const localConfigKey = "agw.localConfig";

export async function readLocalConfig(): Promise<AgwLocalConfig | null> {
  const content = await SecureStore.getItemAsync(localConfigKey);

  if (!content) {
    return null;
  }

  return parseConfigFileContent(content);
}

export async function writeLocalConfig(config: AgwLocalConfig): Promise<void> {
  await SecureStore.setItemAsync(localConfigKey, serializeConfig(config));
}

export async function deleteLocalConfig(): Promise<void> {
  await SecureStore.deleteItemAsync(localConfigKey);
}
