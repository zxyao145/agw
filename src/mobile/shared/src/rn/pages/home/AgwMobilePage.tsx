import React from "react";
import { View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { ChatPanel } from "./components/chat-panel";
import { Composer } from "./components/composer";
import { FilesPanel } from "./components/files-panel";
import { HistoryDrawer } from "./components/history-drawer";
import { styles } from "./components/styles";
import { TopBar } from "./components/top-bar";
import type { AgwTabName } from "./components/types";

export type { AgwTabName } from "./components/types";

type AgwMobilePageProps = {
  initialTab?: AgwTabName;
};

function AgwMobilePage({
  initialTab = "chat",
}: AgwMobilePageProps): React.JSX.Element {
  const safeAreaInsets = useSafeAreaInsets();
  const [activeTab, setActiveTab] = React.useState<AgwTabName>(initialTab);
  const [isDrawerOpen, setIsDrawerOpen] = React.useState(false);

  return (
    <View style={styles.root}>
      <View style={styles.phoneFrame}>
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
        {isDrawerOpen ? (
          <HistoryDrawer
            onClose={() => setIsDrawerOpen(false)}
            safeBottom={safeAreaInsets.bottom}
            safeTop={safeAreaInsets.top}
          />
        ) : null}
      </View>
    </View>
  );
}

export default AgwMobilePage;
