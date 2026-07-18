const CONNECTION_ALIAS_REGEX = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
const CONNECTION_ALIAS_MAX_LENGTH = 128;

export function isConnectionAliasValid(alias: string): boolean {
  return (
    alias.length > 0 &&
    alias.length <= CONNECTION_ALIAS_MAX_LENGTH &&
    CONNECTION_ALIAS_REGEX.test(alias)
  );
}

export function createDefaultConnectionAlias(pluginId: string): string {
  return `${pluginId.toLowerCase()}-account`;
}
