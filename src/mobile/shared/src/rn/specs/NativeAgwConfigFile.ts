import type { TurboModule } from "react-native";
import { TurboModuleRegistry } from "react-native";

export interface Spec extends TurboModule {
  readConfig(): string | null;
  writeConfig(value: string): string | null;
  deleteConfig(): string | null;
}

export default TurboModuleRegistry.get<Spec>("NativeAgwConfigFile");
