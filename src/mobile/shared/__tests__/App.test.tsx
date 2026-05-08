import React from 'react';
import renderer, {act} from 'react-test-renderer';
import App from '../src/rn/App';

jest.mock('react-native-safe-area-context', () => {
  const React = require('react');

  return {
    SafeAreaProvider: ({children}: {children: React.ReactNode}) =>
      React.createElement(React.Fragment, null, children),
    useSafeAreaInsets: () => ({top: 0, right: 0, bottom: 0, left: 0}),
  };
});

(globalThis as {IS_REACT_ACT_ENVIRONMENT?: boolean}).IS_REACT_ACT_ENVIRONMENT = true;

describe('App', () => {
  it('renders the selected route from initial props', async () => {
    let tree: renderer.ReactTestRenderer | undefined;

    await act(async () => {
      tree = renderer.create(
        <App routeName="details" title="Native detail" source="SwiftUI" />,
      );
    });

    const output = collectText(tree?.toJSON());

    expect(output).toContain('Native detail');
    expect(output).toContain('Inspect route-specific data passed by the native app.');
    expect(output).toContain('Opened from SwiftUI');
  });

  it('renders a fallback screen for an unknown route', async () => {
    let tree: renderer.ReactTestRenderer | undefined;

    await act(async () => {
      tree = renderer.create(<App routeName="missing" title="Missing" />);
    });

    expect(collectText(tree?.toJSON())).toContain('Unknown route: missing');
  });

  it('renders Android bottom page navigation and switches routes', async () => {
    let tree: renderer.ReactTestRenderer | undefined;

    await act(async () => {
      tree = renderer.create(<App routeName="home" title="Home" source="Android" />);
    });

    expect(collectText(tree?.toJSON())).toContain('HomeSettingsDetails');

    const settingsTab = tree!.root.findByProps({testID: 'android-tab-settings'});

    await act(async () => {
      settingsTab.props.onPress();
    });

    const output = collectText(tree?.toJSON());

    expect(output).toContain('Settings');
    expect(output).toContain('Route: settings');
    expect(output).toContain('Opened from Android');
  });
});

function collectText(node: renderer.ReactTestRendererJSON | renderer.ReactTestRendererJSON[] | null | undefined): string {
  if (!node) {
    return '';
  }

  if (Array.isArray(node)) {
    return node.map(collectText).join('');
  }

  return (node.children ?? [])
    .map(child => (typeof child === 'string' ? child : collectText(child)))
    .join('');
}
