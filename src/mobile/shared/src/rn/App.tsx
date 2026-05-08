import React from "react";
import {
  StatusBar,
  StyleSheet,
  Text,
  useColorScheme,
  View,
} from "react-native";
import {
  SafeAreaProvider,
  useSafeAreaInsets,
} from "react-native-safe-area-context";
import AgwMobilePage from "./pages/home/AgwMobilePage";
import { ReactNativeInitialProps, resolveRoute } from "./routes";

function App(props: ReactNativeInitialProps): React.JSX.Element {
  const isDarkMode = useColorScheme() === "dark";

  return (
    <SafeAreaProvider>
      <StatusBar barStyle={isDarkMode ? "light-content" : "dark-content"} />
      <RouteScreen {...props} />
    </SafeAreaProvider>
  );
}

function RouteScreen(props: ReactNativeInitialProps): React.JSX.Element {
  const safeAreaInsets = useSafeAreaInsets();
  const route = resolveRoute(props.routeName);

  if (!route) {
    return (
      <View style={[styles.container, { paddingTop: safeAreaInsets.top + 24 }]}>
        <Text style={styles.eyebrow}>Agw React Native</Text>
        <Text style={styles.title}>
          Unknown route: {props.routeName ?? "none"}
        </Text>
        <Text style={styles.body}>
          Swift opened a route that is not registered in JavaScript.
        </Text>
      </View>
    );
  }

  return <AgwMobilePage initialTab={route.initialTab} />;
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    borderTopWidth: 6,
    paddingHorizontal: 24,
    backgroundColor: "#f8fafc",
  },
  eyebrow: {
    color: "#475569",
    fontSize: 13,
    fontWeight: "600",
    letterSpacing: 0,
    marginBottom: 12,
    textTransform: "uppercase",
  },
  title: {
    color: "#0f172a",
    fontSize: 34,
    fontWeight: "700",
    letterSpacing: 0,
    marginBottom: 16,
  },
  body: {
    color: "#334155",
    fontSize: 17,
    lineHeight: 25,
    marginBottom: 24,
  },
});

export default App;
