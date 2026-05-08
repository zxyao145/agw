import React from 'react';
import {
  Pressable,
  StatusBar,
  StyleSheet,
  Text,
  useColorScheme,
  View,
} from 'react-native';
import {
  SafeAreaProvider,
  useSafeAreaInsets,
} from 'react-native-safe-area-context';
import {
  ReactNativeInitialProps,
  RouteName,
  resolveRoute,
  routeOrder,
  routes,
} from './routes';

function App(props: ReactNativeInitialProps): React.JSX.Element {
  const isDarkMode = useColorScheme() === 'dark';

  return (
    <SafeAreaProvider>
      <StatusBar barStyle={isDarkMode ? 'light-content' : 'dark-content'} />
      {props.source === 'Android' ? (
        <AndroidTabbedRoutes initialRouteName={props.routeName} />
      ) : (
        <RouteScreen {...props} />
      )}
    </SafeAreaProvider>
  );
}

function AndroidTabbedRoutes({
  initialRouteName,
}: {
  initialRouteName?: string;
}): React.JSX.Element {
  const safeAreaInsets = useSafeAreaInsets();
  const initialRoute = resolveRoute(initialRouteName)?.routeName ?? 'home';
  const [activeRouteName, setActiveRouteName] =
    React.useState<RouteName>(initialRoute);
  const activeRoute = routes[activeRouteName];

  return (
    <View style={styles.androidShell}>
      <RouteScreen
        routeName={activeRoute.routeName}
        title={activeRoute.title}
        source="Android"
      />
      <View
        accessibilityRole="tablist"
        style={[styles.tabBar, {paddingBottom: safeAreaInsets.bottom + 6}]}>
        {routeOrder.map(routeName => {
          const route = routes[routeName];
          const isSelected = activeRouteName === routeName;

          return (
            <Pressable
              accessibilityRole="tab"
              accessibilityState={{selected: isSelected}}
              key={routeName}
              onPress={() => setActiveRouteName(routeName)}
              style={[
                styles.tabButton,
                isSelected && {
                  backgroundColor: `${route.accentColor}18`,
                  borderTopColor: route.accentColor,
                },
              ]}
              testID={`android-tab-${routeName}`}>
              <Text
                style={[
                  styles.tabText,
                  isSelected && {color: route.accentColor, fontWeight: '700'},
                ]}>
                {route.title}
              </Text>
            </Pressable>
          );
        })}
      </View>
    </View>
  );
}

function RouteScreen(props: ReactNativeInitialProps): React.JSX.Element {
  const safeAreaInsets = useSafeAreaInsets();
  const route = resolveRoute(props.routeName);

  if (!route) {
    return (
      <View style={[styles.container, {paddingTop: safeAreaInsets.top + 24}]}>
        <Text style={styles.eyebrow}>Agw React Native</Text>
        <Text style={styles.title}>Unknown route: {props.routeName ?? 'none'}</Text>
        <Text style={styles.body}>Swift opened a route that is not registered in JavaScript.</Text>
      </View>
    );
  }

  return (
    <View
      style={[
        styles.container,
        {paddingTop: safeAreaInsets.top + 24, borderTopColor: route.accentColor},
      ]}>
      <Text style={styles.eyebrow}>Agw React Native</Text>
      <Text style={styles.title}>{props.title ?? route.title}</Text>
      <Text style={styles.body}>{route.description}</Text>
      <View style={[styles.badge, {backgroundColor: route.accentColor}]}>
        <Text style={styles.badgeText}>Route: {route.routeName}</Text>
      </View>
      <Text style={styles.meta}>Opened from {props.source ?? 'native'}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  androidShell: {
    flex: 1,
    backgroundColor: '#f8fafc',
  },
  container: {
    flex: 1,
    borderTopWidth: 6,
    paddingHorizontal: 24,
    backgroundColor: '#f8fafc',
  },
  eyebrow: {
    color: '#475569',
    fontSize: 13,
    fontWeight: '600',
    letterSpacing: 0,
    marginBottom: 12,
    textTransform: 'uppercase',
  },
  title: {
    color: '#0f172a',
    fontSize: 34,
    fontWeight: '700',
    letterSpacing: 0,
    marginBottom: 16,
  },
  body: {
    color: '#334155',
    fontSize: 17,
    lineHeight: 25,
    marginBottom: 24,
  },
  badge: {
    alignSelf: 'flex-start',
    borderRadius: 6,
    paddingHorizontal: 12,
    paddingVertical: 8,
  },
  badgeText: {
    color: '#ffffff',
    fontSize: 14,
    fontWeight: '700',
  },
  meta: {
    color: '#64748b',
    fontSize: 15,
    marginTop: 20,
  },
  tabBar: {
    alignItems: 'center',
    backgroundColor: '#ffffff',
    borderTopColor: '#e2e8f0',
    borderTopWidth: StyleSheet.hairlineWidth,
    flexDirection: 'row',
    paddingHorizontal: 8,
    paddingTop: 6,
  },
  tabButton: {
    alignItems: 'center',
    borderRadius: 6,
    borderTopColor: 'transparent',
    borderTopWidth: 3,
    flex: 1,
    justifyContent: 'center',
    minHeight: 52,
  },
  tabText: {
    color: '#64748b',
    fontSize: 13,
    fontWeight: '600',
    letterSpacing: 0,
  },
});

export default App;
