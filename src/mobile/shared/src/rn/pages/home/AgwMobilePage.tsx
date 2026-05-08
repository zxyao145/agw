import React from "react";
import { Text, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import type { AgwLocalConfig } from "../../config/agw-config";
import { readLocalConfig, writeLocalConfig } from "../../config/config-store";
import { ChatPanel } from "./components/chat-panel";
import { Composer } from "./components/composer";
import { ConfigSettingsSheet } from "./components/config-settings-sheet";
import { ConfigSetupSheet } from "./components/config-setup-sheet";
import { FilesPanel } from "./components/files-panel";
import { HistoryDrawer } from "./components/history-drawer";
import { styles } from "./components/styles";
import { TopBar } from "./components/top-bar";
import type { AgwTabName } from "./components/types";

export type { AgwTabName } from "./components/types";

type AgwMobilePageProps = {
  initialSettingsOpen?: boolean;
  initialTab?: AgwTabName;
};

function AgwMobilePage({
  initialSettingsOpen = false,
  initialTab = "chat",
}: AgwMobilePageProps): React.JSX.Element {
  const safeAreaInsets = useSafeAreaInsets();
  const [activeTab, setActiveTab] = React.useState<AgwTabName>(initialTab);
  const [config, setConfig] = React.useState<AgwLocalConfig | null>(null);
  const [configLoadState, setConfigLoadState] = React.useState<
    "loading" | "ready" | "missing"
  >("loading");
  const [configLoadError, setConfigLoadError] = React.useState<string | null>(
    null
  );
  const [isDrawerOpen, setIsDrawerOpen] = React.useState(false);
  const [isSettingsOpen, setIsSettingsOpen] =
    React.useState(initialSettingsOpen);

  React.useEffect(() => {
    let isMounted = true;

    async function loadConfig() {
      try {
        const storedConfig = await readLocalConfig();

        if (!isMounted) {
          return;
        }

        setConfig(storedConfig);
        setConfigLoadState(storedConfig ? "ready" : "missing");
        setConfigLoadError(null);
      } catch (error) {
        if (!isMounted) {
          return;
        }

        setConfig(null);
        setConfigLoadState("missing");
        setConfigLoadError(
          error instanceof Error ? error.message : "Configuration is invalid."
        );
      }
    }

    loadConfig();

    return () => {
      isMounted = false;
    };
  }, []);

  async function saveConfig(nextConfig: AgwLocalConfig) {
    await writeLocalConfig(nextConfig);
    setConfig(nextConfig);
    setConfigLoadState("ready");
    setConfigLoadError(null);
  }

  function openSettings() {
    setIsDrawerOpen(false);
    setIsSettingsOpen(true);
  }

  return (
    <View style={styles.root}>
      <View style={styles.phoneFrame}>
        {configLoadState === "loading" ? (
          <View style={styles.loadingPanel}>
            <Text style={styles.loadingText}>Loading Configuration</Text>
          </View>
        ) : (
          <>
            <TopBar
              activeTab={activeTab}
              onOpenDrawer={() => setIsDrawerOpen(true)}
              onTabChange={setActiveTab}
              safeTop={safeAreaInsets.top}
            />
            <View style={styles.mainPanel}>
              {activeTab === "chat" ? <ChatPanel /> : <FilesPanel />}
            </View>
            <Composer safeBottom={safeAreaInsets.bottom} />
          </>
        )}
        {isDrawerOpen ? (
          <HistoryDrawer
            onClose={() => setIsDrawerOpen(false)}
            onOpenSettings={openSettings}
            safeBottom={safeAreaInsets.bottom}
            safeTop={safeAreaInsets.top}
          />
        ) : null}
        {configLoadState === "missing" ? (
          <ConfigSetupSheet
            initialError={configLoadError}
            onSave={saveConfig}
            safeBottom={safeAreaInsets.bottom}
            safeTop={safeAreaInsets.top}
          />
        ) : null}
        {config && isSettingsOpen ? (
          <ConfigSettingsSheet
            config={config}
            onClose={() => setIsSettingsOpen(false)}
            onSave={saveConfig}
            safeBottom={safeAreaInsets.bottom}
            safeTop={safeAreaInsets.top}
          />
        ) : null}
      </View>
    </View>
  );
}

export default AgwMobilePage;
