import type { AgwLocalConfig } from "./agw-config";
import { parseConfigFileContent, serializeConfig } from "./agw-config";
import NativeAgwConfigFile from "../specs/NativeAgwConfigFile";

let inMemoryConfigContent: string | null = null;

export async function readLocalConfig(): Promise<AgwLocalConfig | null> {
  const content = NativeAgwConfigFile?.readConfig() ?? inMemoryConfigContent;

  if (!content) {
    return null;
  }

  return parseConfigFileContent(content);
}

export async function writeLocalConfig(config: AgwLocalConfig): Promise<void> {
  const content = serializeConfig(config);

  if (NativeAgwConfigFile) {
    const error = NativeAgwConfigFile.writeConfig(content);

    if (error) {
      throw new Error(error);
    }

    return;
  }

  inMemoryConfigContent = content;
}

export async function deleteLocalConfig(): Promise<void> {
  if (NativeAgwConfigFile) {
    const error = NativeAgwConfigFile.deleteConfig();

    if (error) {
      throw new Error(error);
    }

    return;
  }

  inMemoryConfigContent = null;
}
