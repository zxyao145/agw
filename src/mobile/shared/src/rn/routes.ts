export type RouteName = 'home' | 'settings' | 'details';

export type ReactNativeInitialProps = {
  routeName?: string;
  title?: string;
  source?: string;
};

export type RouteDefinition = {
  routeName: RouteName;
  title: string;
  description: string;
  accentColor: string;
};

export const routeOrder: RouteName[] = ['home', 'settings', 'details'];

export const routes: Record<RouteName, RouteDefinition> = {
  home: {
    routeName: 'home',
    title: 'Home',
    description: 'A React Native landing page opened from the native shell.',
    accentColor: '#16a34a',
  },
  settings: {
    routeName: 'settings',
    title: 'Settings',
    description: 'Manage preferences from a React Native screen.',
    accentColor: '#2563eb',
  },
  details: {
    routeName: 'details',
    title: 'Details',
    description: 'Inspect route-specific data passed by the native app.',
    accentColor: '#dc2626',
  },
};

export function resolveRoute(routeName?: string): RouteDefinition | undefined {
  if (routeName === 'home' || routeName === 'settings' || routeName === 'details') {
    return routes[routeName];
  }

  return undefined;
}
