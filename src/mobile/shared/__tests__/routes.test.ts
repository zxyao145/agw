import {resolveRoute} from '../src/rn/routes';

describe('resolveRoute', () => {
  it('returns a configured React Native page for a known route', () => {
    expect(resolveRoute('settings')).toEqual({
      routeName: 'settings',
      title: 'Settings',
      description: 'Manage preferences from a React Native screen.',
      accentColor: '#2563eb',
    });
  });

  it('returns undefined for an unknown route', () => {
    expect(resolveRoute('missing')).toBeUndefined();
  });
});
